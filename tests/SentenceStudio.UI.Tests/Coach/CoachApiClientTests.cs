using System.Net;
using System.Text;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Transport-level behavior of the coach API client: unavailability, typed problem mapping and
/// cancellation. These are the contracts the UI relies on to pick a state.
/// </summary>
public class CoachApiClientTests
{
    private static CoachApiClient Create(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("https://api.test") });

    [Fact]
    public async Task GetAvailabilityAsync_TreatsA404AsUnavailableRatherThanAnError()
    {
        // The whole route group 404s when the feature flag is off or the learner is outside the
        // cohort. That must hide the entry point, not raise.
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var availability = await client.GetAvailabilityAsync();

        availability.IsAvailable.Should().BeFalse();
        availability.State.Should().Be(CoachAvailabilityState.Disabled);
    }

    [Fact]
    public async Task GetAvailabilityAsync_ReturnsTheServerAnswerWhenAvailable()
    {
        var client = Create(_ => Json(HttpStatusCode.OK, """
            {"isAvailable":true,"state":"ResumeAvailable","activeSessionId":"s-1"}
            """));

        var availability = await client.GetAvailabilityAsync();

        availability.IsAvailable.Should().BeTrue();
        availability.State.Should().Be(CoachAvailabilityState.ResumeAvailable);
        availability.ActiveSessionId.Should().Be("s-1");
    }

    [Fact]
    public async Task GetAvailabilityAsync_CarriesTheDurableHistoryAndMemoryFlagsThrough()
    {
        var client = Create(_ => Json(HttpStatusCode.OK, """
            {"isAvailable":true,"state":"ResumeAvailable",
             "isDurableHistoryAvailable":true,"isMemoryAvailable":false}
            """));

        var availability = await client.GetAvailabilityAsync();

        // Independently, so a client cannot conflate the two surfaces.
        availability.IsDurableHistoryAvailable.Should().BeTrue();
        availability.IsMemoryAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetAvailabilityAsync_WhenTheServerCannotBeReached_ClaimsNeitherFeature()
    {
        // A client that got no answer knows nothing about the server's features. Defaulting the
        // flags to false is what keeps it from offering history or memory it cannot use.
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var availability = await client.GetAvailabilityAsync();

        availability.IsDurableHistoryAvailable.Should().BeFalse();
        availability.IsMemoryAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetAvailabilityAsync_FromAServerThatPredatesTheFlags_ClaimsNeitherFeature()
    {
        var client = Create(_ => Json(HttpStatusCode.OK, """
            {"isAvailable":true,"state":"ResumeAvailable"}
            """));

        var availability = await client.GetAvailabilityAsync();

        availability.IsAvailable.Should().BeTrue();
        availability.IsDurableHistoryAvailable.Should().BeFalse();
        availability.IsMemoryAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetSessionAsync_ReturnsNullForAMissingOrUnownedSession()
    {
        // A non-owner and a missing session are indistinguishable by design; both 404.
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await client.GetSessionAsync("s-1")).Should().BeNull();
    }

    [Fact]
    public async Task SubmitTurnAsync_ThrowsATypedProblemException()
    {
        var client = Create(_ => Problem(HttpStatusCode.Conflict, CoachProblemTypes.PlanVersionConflict));

        var act = () => client.SubmitTurnAsync("s-1", new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "hi" });

        var exception = await act.Should().ThrowAsync<CoachApiException>();
        exception.Which.ProblemType.Should().Be(CoachProblemTypes.PlanVersionConflict);
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NonProblemErrorBodyStillProducesATypedException()
    {
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>gateway</html>", Encoding.UTF8, "text/html")
        });

        var act = () => client.UndoAsync("s-1", new CoachUndoRequest());

        var exception = await act.Should().ThrowAsync<CoachApiException>();
        exception.Which.ProblemType.Should().BeNull();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task DeleteSessionAsync_TreatsA404AsAlreadyDeleted()
    {
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => client.DeleteSessionAsync("s-1");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = Create(_ => Json(HttpStatusCode.OK, "{}"));

        var act = () => client.GetAvailabilityAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AcceptSuggestionAsync_PostsToTheApprovedRoute()
    {
        string? path = null;
        var client = Create(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            return Json(HttpStatusCode.OK, TurnJson);
        });

        await client.AcceptSuggestionAsync("s 1", "sug/1", new CoachSuggestionDecisionRequest());

        // Ids are escaped so a value with a slash cannot forge a different route.
        path.Should().Be("/api/v1/coach/sessions/s%201/suggestions/sug%2F1/accept");
    }

    private const string TurnJson = """
        {
          "sessionId":"s-1","turnId":"t-1","status":"Completed","stopReason":"Completed",
          "sessionStatus":"Active",
          "activeConstraints":{"availableMinutes":10,"audioAllowed":true,"speechAllowed":true,"typingAllowed":true,"energyLevel":"Normal"},
          "planState":{"planDate":"2026-08-14","planVersion":"v1","appliedConstraints":{"availableMinutes":10,"audioAllowed":true,"speechAllowed":true,"typingAllowed":true,"energyLevel":"Normal"},"estimatedTotalMinutes":10,"completedCount":0,"totalCount":3,"completionPercentage":0},
          "clarificationsRemaining":2,"expiresAtUtc":"2026-08-15T00:00:00Z"
        }
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Problem(HttpStatusCode status, string type) => new(status)
    {
        Content = new StringContent($$"""{"type":"{{type}}","title":"t","detail":"d"}""",
            Encoding.UTF8, "application/problem+json")
    };

    // --- Regression: Forget must include expectedVersion query param (was 500 without it) ---

    [Fact]
    public async Task ForgetMemoryAsync_SendsExpectedVersionQueryParameter()
    {
        HttpRequestMessage? captured = null;
        var client = Create(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await client.ForgetMemoryAsync("fact-42", expectedVersion: 3);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Delete);
        captured.RequestUri!.PathAndQuery.Should().Contain("fact-42");
        captured.RequestUri.Query.Should().Contain("expectedVersion=3");
    }

    [Fact]
    public async Task ForgetMemoryAsync_404IsTreatedAsAlreadyForgotten()
    {
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        // Should not throw — 404 means the fact is already gone.
        await client.ForgetMemoryAsync("gone-fact", expectedVersion: 1);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
