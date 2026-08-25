using System.Net;
using System.Text;
using System.Text.Json;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The write-approval half of the coach API client: which route each action goes to, what it
/// carries, and — above all — where the one-use confirmation is allowed to appear.
/// </summary>
/// <remarks>
/// <para>
/// The route shapes are pinned because they are a wire contract with a server this project cannot
/// reference. A rename on either side is a 404 at runtime and nowhere else, so both halves pin the
/// literal.
/// </para>
/// <para>
/// The confirmation assertions are the important ones. It must travel in a header, must never
/// appear in a URL — which is logged by proxies, browsers, and analytics alike — and must never
/// appear in a request body, which is the part most likely to be captured in a trace.
/// </para>
/// </remarks>
public class CoachWriteApiClientTests
{
    private const string Confirmation = "X-Coach-Write-Confirmation";

    private static CoachApiClient Create(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new WriteStubHandler(responder)) { BaseAddress = new Uri("https://api.test") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string ProposalJson = """
        {
          "operationId": "op-1",
          "conversationId": "c-1",
          "turnId": "t-1",
          "changeKind": "VocabularyAdd",
          "riskClass": "WriteSoft",
          "status": "Proposed",
          "approvalMode": "accept",
          "summary": "Add a word",
          "lines": ["Term: one"],
          "expiresAtUtc": "2026-08-19T12:00:00Z",
          "requiresConfirmation": false,
          "isReversible": true
        }
        """;

    private const string ExecutedJson = """
        {
          "operationId": "op-1",
          "conversationId": "c-1",
          "changeKind": "VocabularyAdd",
          "riskClass": "WriteSoft",
          "status": "Executed",
          "approvalMode": "accept",
          "summary": "Added a word",
          "lines": [],
          "expiresAtUtc": "2026-08-19T12:00:00Z",
          "alreadyExecuted": true,
          "receipt": {
            "operationId": "op-1",
            "changeKind": "VocabularyAdd",
            "riskClass": "WriteSoft",
            "status": "Executed",
            "targetKind": "VocabularyWord",
            "targetId": "w-9",
            "summary": "Added a word",
            "lines": ["Term: one"],
            "executedAtUtc": "2026-08-19T11:30:00Z",
            "canUndo": true,
            "undoExpiresAtUtc": "2026-08-19T11:35:00Z"
          }
        }
        """;

    private const string ChallengeJson = """
        {
          "operationId": "op-1",
          "toolName": "propose_vocabulary_removal",
          "confirmationSecret": "one-use-value",
          "summary": "Remove a word",
          "lines": ["This cannot be undone."],
          "expiresAtUtc": "2026-08-19T12:02:00Z"
        }
        """;

    // ---------------------------------------------------------------- routes

    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    [InlineData("undo")]
    public async Task Each_action_posts_to_its_own_nested_route_with_no_body(string action)
    {
        HttpRequestMessage? seen = null;
        var client = Create(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, ProposalJson);
        });

        _ = action switch
        {
            "accept" => await client.AcceptWriteAsync("c-1", "op-1"),
            "reject" => await client.RejectWriteAsync("c-1", "op-1"),
            _ => await client.UndoWriteAsync("c-1", "op-1")
        };

        seen!.Method.Should().Be(HttpMethod.Post);
        seen.RequestUri!.AbsolutePath.Should()
            .Be($"/api/v1/coach/conversations/c-1/writes/op-1/{action}");
        seen.Content.Should().BeNull(
            "the server already holds the arguments; restating them would open a window where the "
            + "approved change differs from the previewed one");
    }

    [Fact]
    public async Task Reading_a_change_uses_a_get_on_the_operation_route()
    {
        HttpRequestMessage? seen = null;
        var client = Create(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, ProposalJson);
        });

        var state = await client.GetWriteOperationAsync("c-1", "op-1");

        seen!.Method.Should().Be(HttpMethod.Get);
        seen.RequestUri!.AbsolutePath.Should().Be("/api/v1/coach/conversations/c-1/writes/op-1");
        state!.Status.Should().Be(CoachWriteStatus.Proposed);
        state.RiskClass.Should().Be(CoachWriteRiskClass.WriteSoft);
    }

    [Fact]
    public async Task Identifiers_are_escaped_into_the_path()
    {
        HttpRequestMessage? seen = null;
        var client = Create(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, ProposalJson);
        });

        await client.AcceptWriteAsync("c/1", "op 1");

        seen!.RequestUri!.AbsolutePath.Should().Be("/api/v1/coach/conversations/c%2F1/writes/op%201/accept");
    }

    // ---------------------------------------------------------------- the confirmation

    [Fact]
    public async Task A_confirmation_is_requested_on_its_own_route_and_read_back()
    {
        HttpRequestMessage? seen = null;
        var client = Create(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, ChallengeJson);
        });

        var challenge = await client.RequestWriteConfirmationAsync("c-1", "op-1");

        seen!.Method.Should().Be(HttpMethod.Post);
        seen.RequestUri!.AbsolutePath.Should()
            .Be("/api/v1/coach/conversations/c-1/writes/op-1/confirmation");
        challenge!.Value.Should().Be("one-use-value");
        challenge.OperationId.Should().Be("op-1");
    }

    [Fact]
    public async Task Confirming_sends_the_value_in_the_header_and_nowhere_else()
    {
        HttpRequestMessage? seen = null;
        var client = Create(request =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, ExecutedJson);
        });

        await client.ConfirmWriteAsync("c-1", "op-1", new CoachWriteConfirmation
        {
            OperationId = "op-1",
            Value = "one-use-value",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2)
        });

        seen!.Headers.GetValues(Confirmation).Should().ContainSingle().Which.Should().Be("one-use-value");
        seen.RequestUri!.ToString().Should().NotContain("one-use-value",
            "a URL is logged by every proxy, browser, and analytics pipeline it passes through");
        seen.Content.Should().BeNull("a request body is the part most likely to be captured in a trace");
    }

    [Fact]
    public async Task No_other_action_sends_a_confirmation_header()
    {
        var headers = new List<bool>();
        var client = Create(request =>
        {
            headers.Add(request.Headers.Contains(Confirmation));
            return Json(HttpStatusCode.OK, ProposalJson);
        });

        await client.AcceptWriteAsync("c-1", "op-1");
        await client.RejectWriteAsync("c-1", "op-1");
        await client.UndoWriteAsync("c-1", "op-1");
        await client.GetWriteOperationAsync("c-1", "op-1");

        headers.Should().AllSatisfy(sent => sent.Should().BeFalse());
    }

    /// <summary>
    /// The value must not be recoverable from anything the object prints.
    /// </summary>
    /// <remarks>
    /// A positional record would generate a <c>ToString</c> that prints every member, and a single
    /// interpolated log line or a debugger dump pasted into a bug report would disclose it. This
    /// is why the type is a class with an overridden <c>ToString</c>.
    /// </remarks>
    [Fact]
    public void A_confirmation_never_prints_what_it_holds()
    {
        var confirmation = new CoachWriteConfirmation
        {
            OperationId = "op-1",
            Value = "one-use-value",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2)
        };

        confirmation.ToString().Should().NotContain("one-use-value");
        confirmation.ToString().Should().Contain("op-1");
    }

    [Fact]
    public void A_confirmation_is_only_usable_inside_its_window()
    {
        var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

        new CoachWriteConfirmation { Value = "v", ExpiresAtUtc = now.AddSeconds(1) }
            .IsUsableAt(now).Should().BeTrue();

        new CoachWriteConfirmation { Value = "v", ExpiresAtUtc = now }
            .IsUsableAt(now).Should().BeFalse();

        new CoachWriteConfirmation { Value = string.Empty, ExpiresAtUtc = now.AddHours(1) }
            .IsUsableAt(now).Should().BeFalse();
    }

    // ---------------------------------------------------------------- refusals

    /// <summary>
    /// A change that never existed, one belonging to somebody else, and one addressed through the
    /// wrong conversation all answer 404, and the client keeps them indistinguishable.
    /// </summary>
    [Fact]
    public async Task A_not_found_answer_becomes_null_rather_than_an_exception()
    {
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await client.GetWriteOperationAsync("c-1", "op-1")).Should().BeNull();
        (await client.AcceptWriteAsync("c-1", "op-1")).Should().BeNull();
        (await client.RejectWriteAsync("c-1", "op-1")).Should().BeNull();
        (await client.UndoWriteAsync("c-1", "op-1")).Should().BeNull();
        (await client.RequestWriteConfirmationAsync("c-1", "op-1")).Should().BeNull();
    }

    [Fact]
    public async Task A_refusal_surfaces_its_status_so_the_card_can_choose_a_sentence()
    {
        var client = Create(_ => Json(
            HttpStatusCode.UnprocessableEntity,
            """{"type":"https://sentencestudio.dev/problems/coach-invalid-turn-input","detail":"no"}"""));

        var act = () => client.AcceptWriteAsync("c-1", "op-1");

        var thrown = await act.Should().ThrowAsync<CoachApiException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        thrown.Which.ProblemType.Should().Be(CoachProblemTypes.InvalidTurnInput);
    }

    [Fact]
    public async Task An_executed_change_reads_back_its_receipt()
    {
        var client = Create(_ => Json(HttpStatusCode.OK, ExecutedJson));

        var state = await client.AcceptWriteAsync("c-1", "op-1");

        state!.Status.Should().Be(CoachWriteStatus.Executed);
        state.Receipt!.CanUndo.Should().BeTrue();
        state.Receipt.TargetKind.Should().Be(CoachWriteTargetKind.VocabularyWord);
        state.Receipt.TargetId.Should().Be("w-9");
    }

    [Fact]
    public async Task Blank_identifiers_are_refused_before_a_request_is_sent()
    {
        var sent = 0;
        var client = Create(_ =>
        {
            sent++;
            return Json(HttpStatusCode.OK, ProposalJson);
        });

        await new Func<Task>(() => client.AcceptWriteAsync(" ", "op-1"))
            .Should().ThrowAsync<ArgumentException>();
        await new Func<Task>(() => client.AcceptWriteAsync("c-1", " "))
            .Should().ThrowAsync<ArgumentException>();

        sent.Should().Be(0);
    }

    private sealed class WriteStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
