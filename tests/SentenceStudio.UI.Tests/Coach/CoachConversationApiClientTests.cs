using System.Net;
using System.Text;
using System.Text.Json;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The durable-history half of the coach API client: conversation listing, message paging, turn
/// submission and operation polling.
/// </summary>
/// <remarks>
/// The behaviour worth pinning here is the client's side of the recovery contract. A turn is
/// submitted over a network that can drop the response, so the client has to decide the operation
/// id <em>before</em> it sends, keep it, and be able to ask about it afterwards. If the id were
/// assigned by the server and only learned from the response, a lost response would leave the
/// caller with a turn it can neither find nor safely repeat.
/// </remarks>
public class CoachConversationApiClientTests
{
    private static CoachApiClient Create(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("https://api.test") });

    [Fact]
    public async Task A_turn_carries_an_operation_id_the_caller_chose_before_sending()
    {
        string? body = null;
        var client = Create(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.Accepted, OperationJson);
        });

        await client.SubmitConversationTurnAsync("c-1", new CoachConversationTurnRequest { Turn = Turn("hello") });

        var sent = JsonDocument.Parse(body!).RootElement;
        sent.GetProperty("operationId").GetString().Should().NotBeNullOrWhiteSpace(
            "a caller that never learns the id cannot poll for a turn whose response was lost");
        sent.GetProperty("idempotencyKey").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_two_handles_are_different_values()
    {
        // The idempotency key is hashed before storage and is never a lookup handle; the operation
        // id is. Reusing one value for both would make the poll handle recoverable from the digest.
        string? body = null;
        var client = Create(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.Accepted, OperationJson);
        });

        await client.SubmitConversationTurnAsync("c-1", new CoachConversationTurnRequest { Turn = Turn("hello") });

        var sent = JsonDocument.Parse(body!).RootElement;
        sent.GetProperty("operationId").GetString()
            .Should().NotBe(sent.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task A_caller_supplied_operation_id_is_sent_unchanged()
    {
        // A retry after a lost response reuses the id the caller kept. Regenerating it here would
        // turn every retry into a second turn.
        string? body = null;
        var client = Create(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.Accepted, OperationJson);
        });

        await client.SubmitConversationTurnAsync("c-1", new CoachConversationTurnRequest
        {
            Turn = Turn("hello"),
            OperationId = "op-kept-by-the-caller",
            IdempotencyKey = "key-kept-by-the-caller"
        });

        var sent = JsonDocument.Parse(body!).RootElement;
        sent.GetProperty("operationId").GetString().Should().Be("op-kept-by-the-caller");
        sent.GetProperty("idempotencyKey").GetString().Should().Be("key-kept-by-the-caller");
    }

    [Fact]
    public async Task The_idempotency_key_travels_as_a_header_as_well_as_a_field()
    {
        string? header = null;
        var client = Create(request =>
        {
            header = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.FirstOrDefault()
                : null;
            return Json(HttpStatusCode.Accepted, OperationJson);
        });

        await client.SubmitConversationTurnAsync("c-1", new CoachConversationTurnRequest
        {
            Turn = Turn("hello"),
            IdempotencyKey = "key-1"
        });

        header.Should().Be("key-1");
    }

    [Fact]
    public async Task An_operation_can_be_polled_by_the_id_the_caller_sent()
    {
        string? path = null;
        var client = Create(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            return Json(HttpStatusCode.OK, OperationJson);
        });

        var operation = await client.GetConversationOperationAsync("c 1", "op/1");

        path.Should().Be("/api/v1/coach/conversations/c%201/operations/op%2F1");
        operation!.OperationId.Should().Be("op-1");
    }

    [Fact]
    public async Task An_unknown_operation_reads_as_null_rather_than_an_error()
    {
        // A poll that arrives before the claim is committed, and a poll for someone else's
        // operation, are the same 404 by design.
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await client.GetConversationOperationAsync("c-1", "op-1")).Should().BeNull();
    }

    [Fact]
    public async Task Listing_conversations_passes_the_cursor_and_limit_through()
    {
        string? query = null;
        var client = Create(request =>
        {
            query = request.RequestUri?.Query;
            return Json(HttpStatusCode.OK, """{"items":[],"nextCursor":null}""");
        });

        await client.ListConversationsAsync(pageSize: 20, cursor: "cur/1");

        query.Should().Contain("cursor=cur%2F1").And.Contain("pageSize=20");
    }

    [Fact]
    public async Task A_missing_or_unowned_conversation_reads_as_null()
    {
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await client.GetConversationAsync("c-1")).Should().BeNull();
        (await client.GetConversationMessagesAsync("c-1")).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_conversation_treats_a_404_as_already_gone()
    {
        // Delete is idempotent on the server; the client must not turn a repeat into an error.
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => client.DeleteConversationAsync("c-1");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_conflicting_turn_surfaces_as_a_typed_problem()
    {
        var client = Create(_ => Problem(HttpStatusCode.Conflict, CoachProblemTypes.PlanVersionConflict));

        var act = () => client.SubmitConversationTurnAsync("c-1", new CoachConversationTurnRequest { Turn = Turn("hi") });

        var exception = await act.Should().ThrowAsync<CoachApiException>();
        exception.Which.ProblemType.Should().Be(CoachProblemTypes.PlanVersionConflict);
    }

    [Fact]
    public async Task An_export_is_streamed_rather_than_buffered_into_a_string()
    {
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"messages":[]}""", Encoding.UTF8, "application/json")
        });

        await using var stream = await client.ExportConversationAsync("c-1");

        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        (await reader.ReadToEndAsync()).Should().Contain("messages");
    }

    private static CoachTurnRequest Turn(string text) =>
        new() { InputKind = CoachTurnInputKind.Text, Text = text };

    private const string OperationJson = """
        {"operationId":"op-1","conversationId":"c-1","state":"Completed",
         "createdAtUtc":"2026-08-17T00:00:00Z","updatedAtUtc":"2026-08-17T00:00:01Z"}
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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
