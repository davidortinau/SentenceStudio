using System.Net;
using System.Text;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The client half of wire tolerance, exercised through the real transport and the real timeline.
/// </summary>
/// <remarks>
/// <para>
/// The contract-level tests in <c>SentenceStudio.UnitTests</c> prove the converter degrades a value
/// correctly. These prove the two things that only show up once the converter is wired into
/// something: that <see cref="CoachApiClient"/> actually installs it on every path, and that a
/// degraded value lands in a timeline slot the chat pane renders without controls.
/// </para>
/// <para>
/// A client that parsed the value correctly and then rendered it as ordinary prose would pass every
/// contract test and still show a learner a proposal with no way to answer it.
/// </para>
/// </remarks>
public class CoachWireToleranceClientTests
{
    private static CoachApiClient Create(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("https://api.test") });

    // ------------------------------------------------------------------ transport

    [Fact]
    public async Task A_turn_response_with_an_unknown_message_kind_does_not_throw()
    {
        // The regression this guards: before the tolerant options, an unknown enum name threw
        // inside ReadFromJsonAsync, so the failure was not "one card is missing" but "the turn
        // failed" — with the learner's own message left pending on screen.
        var client = Create(_ => Json(HttpStatusCode.OK, TurnJsonWith("ActionCard")));

        var turn = await client.SubmitTurnAsync(
            "s-1", new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "hi" });

        turn.Status.Should().Be(CoachTurnStatus.Completed);
        turn.Messages.Should().HaveCount(2);
        turn.Messages[0].Text.Should().Be("Here is today's plan.");
        turn.Messages[1].Kind.Should().Be(CoachMessageKind.Unrecognized);
    }

    [Fact]
    public async Task An_availability_response_with_an_unknown_state_does_not_open_the_coach()
    {
        var client = Create(_ => Json(HttpStatusCode.OK,
            """{"isAvailable":true,"state":"InviteOnly","activeSessionId":"s-1"}"""));

        var availability = await client.GetAvailabilityAsync();

        availability.State.Should().Be(CoachAvailabilityState.Disabled);
    }

    [Fact]
    public async Task A_history_page_with_an_unknown_message_kind_keeps_the_rest_of_the_thread()
    {
        var client = Create(_ => Json(HttpStatusCode.OK, HistoryJson));

        var page = await client.GetConversationMessagesAsync("c-1");

        page.Should().NotBeNull();
        page!.Items.Should().HaveCount(3, "one unreadable kind must not take the page with it");
        page.Items[1].Message.Kind.Should().Be(CoachMessageKind.Unrecognized);
        page.Items[2].Message.Text.Should().Be("Twenty minutes.");
    }

    [Fact]
    public async Task A_write_operation_with_an_unknown_status_comes_back_unactionable()
    {
        var client = Create(_ => Json(HttpStatusCode.OK, """
            {"operationId":"w-1","conversationId":"c-1","changeKind":"CalendarSync",
             "riskClass":"WriteCatastrophic","status":"AwaitingSecondFactor","approvalMode":"accept",
             "summary":"Sync your calendar","expiresAtUtc":"2026-08-21T00:00:00Z"}
            """));

        var operation = await client.GetWriteOperationAsync("c-1", "w-1");

        operation.Should().NotBeNull();
        operation!.Status.Should().Be(CoachWriteStatus.Unknown);
        operation.Status.Should().NotBe(CoachWriteStatus.Proposed, "only Proposed draws Accept and Reject");
        operation.RiskClass.Should().Be(CoachWriteRiskClass.Unknown, "so no approval channel is chosen");
        operation.Summary.Should().Be("Sync your calendar");
    }

    [Fact]
    public async Task A_malformed_body_still_fails()
    {
        // Tolerance is scoped to enum values. A truncated or non-JSON body is a broken response and
        // has to surface as one rather than as an empty-looking success.
        var client = Create(_ => Json(HttpStatusCode.OK, """{"sessionId":"s-1","status":"Comp"""));

        var act = () => client.SubmitTurnAsync(
            "s-1", new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "hi" });

        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task Every_request_announces_the_client_wire_revision()
    {
        // The client half of the adoption gate. Nothing reads it yet; it has to be in the field
        // before a gate can hold a value back from clients that never announced themselves.
        HttpRequestMessage? captured = null;
        var client = Create(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, """{"isAvailable":false,"state":"Disabled"}""");
        });

        await client.GetAvailabilityAsync();

        captured.Should().NotBeNull();
        captured!.Headers.TryGetValues(WireHeaders.ClientProtocolVersion, out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be(WireProtocolVersion.Current.ToString());
    }

    // ------------------------------------------------------------------ rendering

    [Theory]
    [InlineData(CoachMessageRole.Coach)]
    [InlineData(CoachMessageRole.Learner)]
    public void An_unrecognized_message_gets_the_unsupported_slot_whoever_sent_it(CoachMessageRole role)
    {
        // Role is checked second on purpose: the missing case is most likely the controls, so the
        // neutral placeholder wins over the bubble no matter which side it would have sat on.
        var kind = CoachTimelineEntry.KindFor(new CoachMessageDto
        {
            MessageId = "m-1",
            Role = role,
            Kind = CoachMessageKind.Unrecognized,
            Text = "...",
            CreatedAtUtc = DateTime.UtcNow
        });

        kind.Should().Be(CoachTimelineKind.UnsupportedMessage);
    }

    [Theory]
    [InlineData(CoachMessageKind.Text, CoachMessageRole.Coach, CoachTimelineKind.CoachMessage)]
    [InlineData(CoachMessageKind.Text, CoachMessageRole.Learner, CoachTimelineKind.LearnerMessage)]
    [InlineData(CoachMessageKind.Notice, CoachMessageRole.Coach, CoachTimelineKind.CoachMessage)]
    public void A_known_message_keeps_its_existing_slot(
        CoachMessageKind kind, CoachMessageRole role, CoachTimelineKind expected)
    {
        CoachTimelineEntry.KindFor(new CoachMessageDto
        {
            MessageId = "m-1",
            Role = role,
            Kind = kind,
            Text = "hello",
            CreatedAtUtc = DateTime.UtcNow
        }).Should().Be(expected);
    }

    [Fact]
    public void An_unsupported_entry_is_not_conversational_so_it_carries_no_message_affordances()
    {
        // IsConversational is what the pane and the workspace use to decide an entry can be copied,
        // reported, paired with an answer, or anchored to a write card. An unsupported entry has to
        // be outside all of that.
        var entry = new CoachTimelineEntry
        {
            TurnSequence = 1,
            Sequence = 1,
            Kind = CoachTimelineKind.UnsupportedMessage,
            Timestamp = DateTimeOffset.Now
        };

        entry.IsConversational.Should().BeFalse();
    }

    [Fact]
    public void An_unsupported_message_is_distinct_from_an_unreadable_one()
    {
        // Different facts, different copy: unreadable means the content is gone, unsupported means
        // it arrived and this build cannot present it. Telling a learner their message was lost
        // when it was not is its own bug.
        CoachTimelineKind.UnsupportedMessage.Should().NotBe(CoachTimelineKind.UnreadableMessage);
    }

    private const string HistoryJson = """
        {"conversationId":"c-1","unreadableCount":0,
         "items":[
           {"sequence":1,"message":{"messageId":"m-1","role":"Learner","kind":"Text",
                                    "text":"How long today?","createdAtUtc":"2026-08-20T10:00:00Z"}},
           {"sequence":2,"message":{"messageId":"m-2","role":"Coach","kind":"InlineExercise",
                                    "text":"...","createdAtUtc":"2026-08-20T10:00:05Z"}},
           {"sequence":3,"message":{"messageId":"m-3","role":"Coach","kind":"Text",
                                    "text":"Twenty minutes.","createdAtUtc":"2026-08-20T10:00:06Z"}}
         ]}
        """;

    private const string Constraints =
        """{"availableMinutes":10,"audioAllowed":true,"speechAllowed":true,"typingAllowed":true,"energyLevel":"Normal"}""";

    private static string TurnJsonWith(string secondMessageKind) => $$"""
        {
          "sessionId":"s-1","turnId":"t-1","status":"Completed","stopReason":"Completed",
          "sessionStatus":"Active",
          "messages":[
            {"messageId":"m-1","role":"Coach","kind":"Text","text":"Here is today's plan.",
             "createdAtUtc":"2026-08-20T10:00:00Z"},
            {"messageId":"m-2","role":"Coach","kind":"{{secondMessageKind}}","text":"...",
             "createdAtUtc":"2026-08-20T10:00:01Z"}
          ],
          "activeConstraints":{{Constraints}},
          "planState":{"planDate":"2026-08-20","planVersion":"v1","appliedConstraints":{{Constraints}},
                       "estimatedTotalMinutes":10,"completedCount":0,"totalCount":3,"completionPercentage":0},
          "clarificationsRemaining":2,"expiresAtUtc":"2026-08-21T00:00:00Z"
        }
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
