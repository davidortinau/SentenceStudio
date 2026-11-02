using System.Net;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebApp.Coach;

namespace SentenceStudio.WebApp.Tests.Coach;

public sealed class CoachAGUITransportTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver
    };

    [Fact]
    public async Task DurableTurnPreservesRecoveryHandlesAndReadsAuthoritativeSnapshot()
    {
        var operation = new CoachTurnOperationDto
        {
            OperationId = "operation-1", ConversationId = "conversation-1",
            State = CoachTurnOperationState.Completed,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        using var handler = new ResponseHandler(Events(
            new RunStartedEvent { ThreadId = "conversation-1", RunId = "operation-1" },
            new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement(new CoachAgentState { Operation = operation }) },
            new RunFinishedEvent { ThreadId = "conversation-1", RunId = "operation-1" }));
        var transport = new CoachAGUITransport(new ClientFactory(handler));
        var result = await transport.SubmitConversationTurnAsync("conversation-1", new()
        {
            OperationId = "operation-1", IdempotencyKey = "idempotency-1",
            Turn = new() { InputKind = CoachTurnInputKind.Text, Text = "Hello", ClientTurnId = "turn-1" }
        }, default);

        result.OperationId.Should().Be("operation-1");
        var input = JsonSerializer.Deserialize<RunAgentInput>(handler.Request!, Json)!;
        input.ThreadId.Should().Be("conversation-1");
        input.RunId.Should().Be("operation-1");
        var state = input.State!.Value.Deserialize<CoachAgentState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        state.DurableTurn!.IdempotencyKey.Should().Be("idempotency-1");
        state.DurableTurn.Turn.ClientTurnId.Should().Be("turn-1");
    }

    [Fact]
    public async Task RunErrorPreservesTheApplicationProblemType()
    {
        using var handler = new ResponseHandler(Events(
            new RunStartedEvent { ThreadId = "session-1", RunId = "run-1" },
            new RunErrorEvent { Code = CoachProblemTypes.Unavailable, Message = "Coach unavailable" }));
        var transport = new CoachAGUITransport(new ClientFactory(handler));
        var act = () => transport.SubmitTurnAsync("session-1", Text(), default);
        var error = await act.Should().ThrowAsync<CoachApiException>();
        error.Which.ProblemType.Should().Be(CoachProblemTypes.Unavailable);
    }

    [Fact]
    public async Task StreamWithoutSnapshotIsNotReportedAsSuccess()
    {
        using var handler = new ResponseHandler(Events(
            new RunStartedEvent { ThreadId = "session-1", RunId = "run-1" },
            new RunFinishedEvent { ThreadId = "session-1", RunId = "run-1" }));
        var transport = new CoachAGUITransport(new ClientFactory(handler));
        var act = () => transport.SubmitTurnAsync("session-1", Text(), default);
        var error = await act.Should().ThrowAsync<CoachApiException>();
        error.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task SnapshotFromTruncatedRunIsNotReportedAsSuccess()
    {
        using var handler = new ResponseHandler(Events(
            new RunStartedEvent { ThreadId = "session-1", RunId = "run-1" },
            new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement(new CoachAgentState()) }));
        var transport = new CoachAGUITransport(new ClientFactory(handler));
        var act = () => transport.SubmitTurnAsync("session-1", Text(), default);
        var error = await act.Should().ThrowAsync<CoachApiException>().WithMessage("*before the run completed*");
        error.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task MalformedSnapshotIsARecoverableTransportFailure()
    {
        using var handler = new ResponseHandler(Events(
            new RunStartedEvent { ThreadId = "session-1", RunId = "run-1" },
            new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement("not a coach state") },
            new RunFinishedEvent { ThreadId = "session-1", RunId = "run-1" }));
        var transport = new CoachAGUITransport(new ClientFactory(handler));
        var act = () => transport.SubmitTurnAsync("session-1", Text(), default);

        var error = await act.Should().ThrowAsync<CoachApiException>();
        error.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        error.Which.InnerException.Should().BeOfType<JsonException>();
    }

    [Fact]
    public async Task HttpProblemPreservesOwnershipFailureBeforeStreamParsing()
    {
        using var handler = new ResponseHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"type":"owned-thread-not-found","title":"Not found","detail":"Thread unavailable"}""",
                Encoding.UTF8, "application/problem+json")
        });
        using var problems = new CoachAGUIProblemHandler { InnerHandler = handler };
        var transport = new CoachAGUITransport(new ClientFactory(problems));
        var act = () => transport.SubmitTurnAsync("session-1", Text(), default);
        var error = await act.Should().ThrowAsync<CoachApiException>();
        error.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        error.Which.ProblemType.Should().Be("owned-thread-not-found");
    }

    private static CoachTurnRequest Text() => new() { InputKind = CoachTurnInputKind.Text, Text = "Hello" };

    private static HttpResponseMessage Events(params BaseEvent[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(events.Select(e =>
            $"data: {JsonSerializer.Serialize(e, Json)}\n\n")), Encoding.UTF8, "text/event-stream")
    };

    private sealed class ResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Request = await request.Content!.ReadAsStringAsync(token);
            return response;
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://coach.invalid")
        };
    }
}
