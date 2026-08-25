using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression cover for the 2026-08-14 defect: <c>GET /api/v1/coach/availability</c> reported
/// Disabled at 21:52 America/Chicago while Today's Plan existed for Aug 14.
/// </summary>
/// <remarks>
/// No client sent <c>X-Timezone</c>, so the API's <c>HttpPlanDateContext</c> fell back to UTC.
/// The UTC date was already Aug 15, the Aug 14 plan looked absent, and the availability policy
/// concluded there was no plan to edit.
/// </remarks>
public class PlanTimeZoneHeaderHandlerTests
{
    private const string Chicago = "America/Chicago";

    /// <summary>21:52 on 2026-08-14 in America/Chicago (CDT, UTC-5) is 02:52 on Aug 15 in UTC.</summary>
    private static readonly DateTime LateEveningUtc = new(2026, 8, 15, 2, 52, 0, DateTimeKind.Utc);

    // ================================================================ header is sent

    [Fact]
    public async Task AvailabilityRequestCarriesTheLearnersTimeZone()
    {
        var (client, captured) = CreateCoachClient(Chicago);

        await client.GetAvailabilityAsync();

        captured.Should().ContainSingle();
        captured[0].Headers.GetValues(PlanDateHeaders.TimeZone).Should().ContainSingle().Which.Should().Be(Chicago);
    }

    [Fact]
    public void TheHeaderNameIsSharedWithTheApiRatherThanDuplicated()
    {
        // AppLib cannot reference the API assembly, so the constant lives in Shared and both
        // sides use it. A literal on either side could be renamed without the other noticing.
        PlanDateHeaders.TimeZone.Should().Be("X-Timezone");
    }

    // ================================================================ the actual defect

    [Fact]
    public void ChicagoLateEveningResolvesToTheLocalDateNotTheUtcDate()
    {
        // What the API does with the header we now send.
        TimeZoneResolver.TryResolve(Chicago, out var zone).Should().BeTrue();
        var withHeader = new PlanDateContext(zone, () => LateEveningUtc);

        withHeader.UserLocalDate.Should().Be(new DateOnly(2026, 8, 14),
            "the learner's plan for Aug 14 must still be today's plan at 21:52 local");
    }

    [Fact]
    public void WithoutTheHeaderTheSameInstantResolvesToTheWrongDay()
    {
        // The pre-fix behavior, pinned so the regression is unmistakable.
        TimeZoneResolver.TryResolve(null, out var fallback).Should().BeFalse();
        fallback.Should().Be(TimeZoneInfo.Utc);

        var withoutHeader = new PlanDateContext(fallback, () => LateEveningUtc);

        withoutHeader.UserLocalDate.Should().Be(new DateOnly(2026, 8, 15),
            "this off-by-one day is exactly what made the coach report itself unavailable");
        withoutHeader.UserLocalDate.Should().NotBe(new DateOnly(2026, 8, 14));
    }

    [Fact]
    public async Task AUtcLearnerStillSendsAnExplicitHeader()
    {
        // Explicit UTC and absent-header both resolve to UTC server-side, but sending it
        // explicitly keeps the contract observable instead of relying on a fallback.
        var (client, captured) = CreateCoachClient(TimeZoneInfo.Utc.Id);

        await client.GetAvailabilityAsync();

        captured[0].Headers.GetValues(PlanDateHeaders.TimeZone).Should().ContainSingle()
            .Which.Should().Be(TimeZoneInfo.Utc.Id);
    }

    [Fact]
    public async Task NoPlanDateContextRegisteredFallsBackToTheServersUtcBehavior()
    {
        // The handler must never break a call just because the context is unavailable.
        var captured = new List<HttpRequestMessage>();
        var services = new ServiceCollection().BuildServiceProvider();
        var client = BuildCoachClient(services, captured);

        await client.GetAvailabilityAsync();

        captured.Should().ContainSingle();
        captured[0].Headers.Contains(PlanDateHeaders.TimeZone).Should().BeFalse();
    }

    [Fact]
    public async Task AnExplicitlySetHeaderIsNotOverwritten()
    {
        var captured = new List<HttpRequestMessage>();
        var handler = new PlanTimeZoneHeaderHandler(BuildProvider(Chicago))
        {
            InnerHandler = new CapturingHandler(captured)
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/x");
        request.Headers.TryAddWithoutValidation(PlanDateHeaders.TimeZone, "Asia/Seoul");

        await invoker.SendAsync(request, CancellationToken.None);

        captured[0].Headers.GetValues(PlanDateHeaders.TimeZone).Should().ContainSingle()
            .Which.Should().Be("Asia/Seoul");
    }

    // ================================================================ no stale capture

    [Fact]
    public async Task TheTimeZoneIsResolvedPerRequestNotCapturedOnce()
    {
        // HttpClientFactory caches a handler chain for minutes. A constructor-injected context
        // would freeze one learner's timezone into that chain and hand it to everyone else.
        var captured = new List<HttpRequestMessage>();
        var provider = new MutableTimeZoneProvider(Chicago);

        var handler = new PlanTimeZoneHeaderHandler(provider)
        {
            InnerHandler = new CapturingHandler(captured)
        };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/a"), CancellationToken.None);

        provider.TimeZoneId = "Asia/Seoul";
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/b"), CancellationToken.None);

        captured[0].Headers.GetValues(PlanDateHeaders.TimeZone).Single().Should().Be(Chicago);
        captured[1].Headers.GetValues(PlanDateHeaders.TimeZone).Single().Should().Be("Asia/Seoul",
            "the same cached chain must follow the current learner, not the first one");
        provider.Resolutions.Should().Be(2, "the context is resolved once per request");
    }

    // ================================================================ every method

    public static TheoryData<string, Func<ICoachApiClient, Task>> AllCoachOperations() => new()
    {
        { "availability", c => c.GetAvailabilityAsync() },
        { "start session", c => c.StartSessionAsync(new StartCoachSessionRequest()) },
        { "get session", c => c.GetSessionAsync("s-1") },
        { "submit turn", c => c.SubmitTurnAsync("s-1", new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "hi" }) },
        { "accept suggestion", c => c.AcceptSuggestionAsync("s-1", "sug-1", new CoachSuggestionDecisionRequest()) },
        { "reject suggestion", c => c.RejectSuggestionAsync("s-1", "sug-1", new CoachSuggestionDecisionRequest()) },
        { "undo", c => c.UndoAsync("s-1", new CoachUndoRequest()) },
        { "cancel", c => c.CancelSessionAsync("s-1") },
        { "delete session", c => c.DeleteSessionAsync("s-1") }
    };

    [Theory]
    [MemberData(nameof(AllCoachOperations))]
    public async Task EveryCoachOperationSendsTheTimeZoneHeader(string operation, Func<ICoachApiClient, Task> invoke)
    {
        var (client, captured) = CreateCoachClient(Chicago);

        await invoke(client);

        captured.Should().ContainSingle($"'{operation}' should issue exactly one request");
        captured[0].Headers.Contains(PlanDateHeaders.TimeZone)
            .Should().BeTrue($"'{operation}' is plan-date sensitive and must carry the learner's timezone");
        captured[0].Headers.GetValues(PlanDateHeaders.TimeZone).Single().Should().Be(Chicago);
    }

    [Fact]
    public async Task ThePlansClientSendsTheSameHeader()
    {
        // Plan generation is keyed to the learner's local date too, and had the identical bug.
        var captured = new List<HttpRequestMessage>();
        var handler = new PlanTimeZoneHeaderHandler(BuildProvider(Chicago))
        {
            InnerHandler = new CapturingHandler(captured, PlanJson)
        };

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test") };
        var client = new PlansApiClient(http);

        await client.GeneratePlanAsync(new SentenceStudio.Contracts.Plans.GeneratePlanRequest());

        captured[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/plans/generate");
        captured[0].Headers.GetValues(PlanDateHeaders.TimeZone).Single().Should().Be(Chicago);
    }

    // ================================================================ helpers

    private const string PlanJson = "{}";

    private const string ConstraintsJson =
        """{"availableMinutes":10,"audioAllowed":true,"speechAllowed":true,"typingAllowed":true,"energyLevel":"Normal"}""";

    private const string PlanStateJson =
        $$"""{"planDate":"2026-08-14","planVersion":"v1","appliedConstraints":{{ConstraintsJson}},"estimatedTotalMinutes":10,"completedCount":0,"totalCount":3,"completionPercentage":0}""";

    private const string AvailabilityJson = """{"isAvailable":true,"state":"Available"}""";

    private const string SessionJson =
        $$"""{"sessionId":"s-1","status":"Active","activeConstraints":{{ConstraintsJson}},"planState":{{PlanStateJson}},"clarificationsRemaining":2,"createdAtUtc":"2026-08-15T02:52:00Z","expiresAtUtc":"2026-08-16T02:52:00Z"}""";

    private const string TurnJson =
        $$"""{"sessionId":"s-1","turnId":"t-1","status":"Completed","stopReason":"Completed","sessionStatus":"Active","activeConstraints":{{ConstraintsJson}},"planState":{{PlanStateJson}},"clarificationsRemaining":2,"expiresAtUtc":"2026-08-16T02:52:00Z"}""";

    /// <summary>
    /// Answers each coach route with a body its contract can actually deserialize, so a test
    /// failure means the header is missing rather than the stub being unrealistic.
    /// </summary>
    private static (HttpStatusCode Status, string Body) ResponseFor(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (request.Method == HttpMethod.Delete || path.EndsWith("/cancel", StringComparison.Ordinal))
        {
            return (HttpStatusCode.NoContent, string.Empty);
        }

        if (path.EndsWith("/availability", StringComparison.Ordinal))
        {
            return (HttpStatusCode.OK, AvailabilityJson);
        }

        if (path.EndsWith("/turns", StringComparison.Ordinal)
            || path.EndsWith("/accept", StringComparison.Ordinal)
            || path.EndsWith("/reject", StringComparison.Ordinal)
            || path.EndsWith("/undo", StringComparison.Ordinal))
        {
            return (HttpStatusCode.OK, TurnJson);
        }

        if (path.Contains("/coach/sessions", StringComparison.Ordinal))
        {
            return (HttpStatusCode.OK, SessionJson);
        }

        return (HttpStatusCode.OK, PlanJson);
    }

    private static (ICoachApiClient Client, List<HttpRequestMessage> Captured) CreateCoachClient(string timeZoneId)
    {
        var captured = new List<HttpRequestMessage>();
        return (BuildCoachClient(BuildProvider(timeZoneId), captured), captured);
    }

    private static ICoachApiClient BuildCoachClient(IServiceProvider provider, List<HttpRequestMessage> captured)
    {
        var handler = new PlanTimeZoneHeaderHandler(provider)
        {
            InnerHandler = new CapturingHandler(captured)
        };

        return new CoachApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") });
    }

    private static IServiceProvider BuildProvider(string timeZoneId)
    {
        TimeZoneResolver.TryResolve(timeZoneId, out var zone);

        // Transient, exactly as both hosts register it.
        return new ServiceCollection()
            .AddTransient<IPlanDateContext>(_ => new PlanDateContext(zone, () => LateEveningUtc))
            .BuildServiceProvider();
    }

    /// <summary>A provider whose timezone can change between requests, standing in for a
    /// different learner arriving on a cached handler chain.</summary>
    private sealed class MutableTimeZoneProvider(string timeZoneId) : IServiceProvider
    {
        public string TimeZoneId { get; set; } = timeZoneId;

        public int Resolutions { get; private set; }

        public object? GetService(Type serviceType)
        {
            if (serviceType != typeof(IPlanDateContext))
            {
                return null;
            }

            Resolutions++;
            TimeZoneResolver.TryResolve(TimeZoneId, out var zone);
            return new PlanDateContext(zone, () => LateEveningUtc);
        }
    }

    private sealed class CapturingHandler(List<HttpRequestMessage> captured, string? body = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captured.Add(request);

            var (status, payload) = body is null
                ? ResponseFor(request)
                : (HttpStatusCode.OK, body);

            var response = new HttpResponseMessage(status);
            if (!string.IsNullOrEmpty(payload))
            {
                response.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
