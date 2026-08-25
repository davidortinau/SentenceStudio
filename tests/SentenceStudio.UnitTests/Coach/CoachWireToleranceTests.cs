using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// What an already-shipped client does when a newer server sends a value it has never heard of.
/// </summary>
/// <remarks>
/// <para>
/// Every payload here is written the way a <em>future</em> server would write it — a real enum
/// name this build does not have — and read with <see cref="WireJson.Client"/>, which is what the
/// installed app uses. The question each test answers is the one that actually matters on a
/// phone that has not been updated: does the learner lose one card, or the whole conversation?
/// </para>
/// <para>
/// The second question, asked just as often here, is whether a value the client could not read can
/// still put a button on screen. It must not. Degrading into something actionable would be worse
/// than the exception this replaces: an exception is visible, and a mislabelled Accept button is
/// not.
/// </para>
/// </remarks>
public class CoachWireToleranceTests
{
    private static readonly JsonSerializerOptions Client = WireJson.Client;

    // ---------------------------------------------------------------- a live turn

    [Fact]
    public void A_turn_carrying_an_unknown_message_kind_still_deserializes_everything_around_it()
    {
        var turn = JsonSerializer.Deserialize<CoachTurnResponse>(TurnJson("ActionCard"), Client);

        turn.Should().NotBeNull();
        turn!.SessionId.Should().Be("s-1");
        turn.TurnId.Should().Be("t-1");
        turn.Status.Should().Be(CoachTurnStatus.Completed, "the known fields around the unknown one are untouched");
        turn.StopReason.Should().Be(CoachStopReason.Completed);
        turn.SessionStatus.Should().Be(CoachSessionStatus.Active);
        turn.ClarificationsRemaining.Should().Be(2);
        turn.Messages.Should().HaveCount(2);

        // The known message keeps its text and its kind.
        turn.Messages[0].Kind.Should().Be(CoachMessageKind.Text);
        turn.Messages[0].Text.Should().Be("Here is today's plan.");

        // The unknown one is marked as unrecognised rather than passed off as prose.
        turn.Messages[1].Kind.Should().Be(CoachMessageKind.Unrecognized);
        turn.Messages[1].MessageId.Should().Be("m-2");
    }

    [Fact]
    public void An_unknown_message_kind_never_reads_as_ordinary_text()
    {
        var turn = JsonSerializer.Deserialize<CoachTurnResponse>(TurnJson("ConsentPrompt"), Client)!;

        // The failure this prevents: a consent prompt or a proposal rendered as a sentence, with
        // the controls that made it answerable silently absent. The learner would read a question
        // as a statement and never know a decision was being asked of them.
        turn.Messages[1].Kind.Should().NotBe(CoachMessageKind.Text);
        turn.Messages[1].Kind.Should().Be(CoachMessageKind.Unrecognized);
    }

    [Fact]
    public void An_unknown_message_kind_is_never_offered_for_review()
    {
        var turn = JsonSerializer.Deserialize<CoachTurnResponse>(TurnJson("ActionCard"), Client)!;

        CoachResponseReportability.IsReportableKind(turn.Messages[1].Kind)
            .Should().BeFalse("nothing is known about what it was, so there is nothing to review");
    }

    [Fact]
    public void An_unknown_turn_status_never_reads_as_success()
    {
        var json = $$"""
            {"sessionId":"s-1","turnId":"t-1","status":"PartiallyApplied","stopReason":"Deferred",
             "sessionStatus":"Hibernating","messages":[],
             "activeConstraints":{{Constraints}},"planState":{{PlanState}},
             "expiresAtUtc":"2026-08-21T00:00:00Z"}
            """;

        var turn = JsonSerializer.Deserialize<CoachTurnResponse>(json, Client)!;

        turn.Status.Should().Be(CoachTurnStatus.Failed, "an unreadable outcome is not a success");
        turn.StopReason.Should().Be(CoachStopReason.Failed);
        turn.SessionStatus.Should().Be(CoachSessionStatus.Expired, "and it does not accept another turn");
    }

    // ---------------------------------------------------------------- durable history

    [Fact]
    public void A_history_page_with_an_unknown_message_kind_keeps_every_other_message()
    {
        const string json = """
            {"conversationId":"c-1","previousCursor":"cur-1","unreadableCount":0,
             "items":[
               {"sequence":1,"message":{"messageId":"m-1","role":"Learner","kind":"Text",
                                        "text":"How long today?","createdAtUtc":"2026-08-20T10:00:00Z"}},
               {"sequence":2,"message":{"messageId":"m-2","role":"Coach","kind":"InlineExercise",
                                        "text":"...","createdAtUtc":"2026-08-20T10:00:05Z"}},
               {"sequence":3,"message":{"messageId":"m-3","role":"Coach","kind":"Text",
                                        "text":"Twenty minutes.","createdAtUtc":"2026-08-20T10:00:06Z"}}
             ]}
            """;

        var page = JsonSerializer.Deserialize<CoachMessagePageDto>(json, Client);

        page.Should().NotBeNull();
        page!.ConversationId.Should().Be("c-1");
        page.PreviousCursor.Should().Be("cur-1");

        // The thread keeps its shape: three exchanges in, three exchanges out. Dropping the middle
        // one would silently rewrite what happened.
        page.Items.Should().HaveCount(3);
        page.Items[0].Message.Text.Should().Be("How long today?");
        page.Items[1].Message.Kind.Should().Be(CoachMessageKind.Unrecognized);
        page.Items[2].Message.Text.Should().Be("Twenty minutes.");
        page.Items[2].Sequence.Should().Be(3, "ordering survives an unreadable neighbour");
    }

    [Fact]
    public void An_unknown_message_role_is_attributed_to_the_coach_not_the_learner()
    {
        const string json = """
            {"messageId":"m-9","role":"System","kind":"Text","text":"Session resumed.",
             "createdAtUtc":"2026-08-20T10:00:00Z"}
            """;

        var message = JsonSerializer.Deserialize<CoachMessageDto>(json, Client)!;

        // Putting server-authored words on the learner's side of the thread would show them saying
        // something they never typed.
        message.Role.Should().Be(CoachMessageRole.Coach);
    }

    // ---------------------------------------------------------------- writes and actions

    [Fact]
    public void An_unknown_write_status_is_never_actionable_and_never_reads_as_applied()
    {
        const string json = """
            {"operationId":"w-1","conversationId":"c-1","changeKind":"CalendarSync",
             "riskClass":"WriteCatastrophic","status":"AwaitingSecondFactor",
             "approvalMode":"accept","summary":"Sync your calendar","lines":["One line"],
             "expiresAtUtc":"2026-08-21T00:00:00Z","requiresConfirmation":true}
            """;

        var operation = JsonSerializer.Deserialize<CoachWriteOperationDto>(json, Client)!;

        // Only Proposed draws Accept and Reject. Only Executed claims something happened.
        operation.Status.Should().Be(CoachWriteStatus.Unknown);
        operation.Status.Should().NotBe(CoachWriteStatus.Proposed, "an unreadable proposal is not answerable");
        operation.Status.Should().NotBe(CoachWriteStatus.Executed, "and it certainly did not happen");

        // The approval channel is unreadable, so no channel is picked.
        operation.RiskClass.Should().Be(CoachWriteRiskClass.Unknown);

        // The heading falls back to neutral copy rather than naming a change we cannot describe.
        operation.ChangeKind.Should().Be(CoachWriteChangeKind.Unknown);

        // The content around the unknown values survives, so the card can still say something true.
        operation.OperationId.Should().Be("w-1");
        operation.Summary.Should().Be("Sync your calendar");
        operation.Lines.Should().ContainSingle().Which.Should().Be("One line");
    }

    [Fact]
    public void An_unknown_receipt_target_never_claims_to_point_at_a_row()
    {
        const string json = """
            {"operationId":"w-2","changeKind":"CalendarSync","riskClass":"WriteSoft",
             "status":"Executed","targetKind":"CalendarEvent","targetId":"cal-1",
             "summary":"Synced","executedAtUtc":"2026-08-20T10:00:00Z","canUndo":false}
            """;

        var receipt = JsonSerializer.Deserialize<CoachWriteReceiptDto>(json, Client)!;

        receipt.TargetKind.Should().Be(CoachWriteTargetKind.None,
            "a client that cannot name the thing must not offer to open or undo it");
        receipt.Status.Should().Be(CoachWriteStatus.Executed, "the readable fields are still read");
        receipt.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void An_unknown_operation_state_never_reads_as_completed_or_leaves_the_client_polling()
    {
        const string json = """
            {"operationId":"op-1","conversationId":"c-1","state":"Queued",
             "createdAtUtc":"2026-08-20T10:00:00Z","updatedAtUtc":"2026-08-20T10:00:02Z"}
            """;

        var operation = JsonSerializer.Deserialize<CoachTurnOperationDto>(json, Client)!;

        operation.State.Should().Be(CoachTurnOperationState.Failed);
        operation.State.Should().NotBe(CoachTurnOperationState.Completed);
        operation.State.Should().NotBe(CoachTurnOperationState.Running);
    }

    [Fact]
    public void An_unknown_availability_state_does_not_open_an_entry_point()
    {
        const string json = """{"isAvailable":true,"state":"InviteOnly","activeSessionId":"s-1"}""";

        var availability = JsonSerializer.Deserialize<CoachAvailabilityResponse>(json, Client)!;

        availability.State.Should().Be(CoachAvailabilityState.Disabled);
        availability.ActiveSessionId.Should().Be("s-1", "the surrounding fields are still read");
    }

    // ---------------------------------------------------------------- structured answers

    [Fact]
    public void An_unknown_answer_block_kind_is_labelled_as_an_aside_not_promoted_to_the_answer()
    {
        const string json = """
            {"topic":"Etymology","plainText":"fallback text","displayLanguageTag":"en",
             "targetLanguageTag":"ko",
             "blocks":[
               {"kind":"Answer","spans":[
                 {"language":"Display","text":"Because it is polite.","languageTag":"en"}]},
               {"kind":"Mnemonic","spans":[
                 {"language":"Cherokee","text":"remember this","languageTag":"chr"}]}
             ]}
            """;

        var answer = JsonSerializer.Deserialize<CoachAnswerDto>(json, Client)!;

        answer.Topic.Should().Be(CoachAnswerTopic.Other, "Other is literally 'fits none of the above'");
        answer.Blocks.Should().HaveCount(2);
        answer.Blocks[0].Kind.Should().Be(CoachAnswerBlockKind.Answer);

        // The direct answer leads and is rendered without a label. An unrecognised block landing
        // there would be presented as the answer to the learner's question.
        answer.Blocks[1].Kind.Should().Be(CoachAnswerBlockKind.Note);
        answer.Blocks[1].Kind.Should().NotBe(CoachAnswerBlockKind.Answer);

        // An unreadable language role inherits the display language rather than telling a screen
        // reader to switch to a voice the text may not be in.
        answer.Blocks[1].Spans[0].Language.Should().Be(CoachLanguageRole.Display);
    }

    // ---------------------------------------------------------------- memory

    [Fact]
    public void An_unknown_memory_kind_and_status_stay_on_the_least_trusted_reading()
    {
        const string json = """
            {"id":"f-1","kind":"FavouriteTopic","status":"Pinned","scope":"Household",
             "targetLanguageCode":"ko","value":{"kind":"FavouriteTopic"},
             "displayText":"cooking","provenance":"ModelInferred","evidenceCount":1,
             "createdAtUtc":"2026-08-20T10:00:00Z","updatedAtUtc":"2026-08-20T10:00:00Z","version":3}
            """;

        var fact = JsonSerializer.Deserialize<CoachMemoryFactDto>(json, Client)!;

        fact.Status.Should().Be(CoachMemoryStatus.Candidate, "never Active on a value we cannot read");
        fact.Scope.Should().Be(CoachMemoryScope.TargetLanguage, "an unreadable scope must not go Global");
        fact.Provenance.Should().Be(CoachMemoryProvenance.UserExplicit,
            "the weaker claim: never assert the learner confirmed it");
        fact.DisplayText.Should().Be("cooking", "the server's own rendering still shows");
        fact.Version.Should().Be(3, "so a later write still carries the right concurrency token");
    }

    // ---------------------------------------------------------------- known values and shapes

    [Theory]
    [InlineData(CoachMessageKind.Text)]
    [InlineData(CoachMessageKind.Clarification)]
    [InlineData(CoachMessageKind.Suggestion)]
    [InlineData(CoachMessageKind.Receipt)]
    [InlineData(CoachMessageKind.Notice)]
    [InlineData(CoachMessageKind.PedagogicalAnswer)]
    [InlineData(CoachMessageKind.Unrecognized)]
    public void A_known_value_round_trips_by_name(CoachMessageKind kind)
    {
        var json = JsonSerializer.Serialize(kind, Client);

        json.Should().Be($"\"{kind}\"", "tolerance must not cost canonical names");
        JsonSerializer.Deserialize<CoachMessageKind>(json, Client).Should().Be(kind);
    }

    [Fact]
    public void A_whole_turn_of_known_values_round_trips_unchanged()
    {
        var original = JsonSerializer.Deserialize<CoachTurnResponse>(TurnJson("Notice"), Client)!;

        var round = JsonSerializer.Deserialize<CoachTurnResponse>(
            JsonSerializer.Serialize(original, Client), Client)!;

        round.Status.Should().Be(original.Status);
        round.StopReason.Should().Be(original.StopReason);
        round.SessionStatus.Should().Be(original.SessionStatus);
        round.Messages.Select(m => m.Kind).Should().Equal(original.Messages.Select(m => m.Kind));
        round.Messages.Select(m => m.Text).Should().Equal(original.Messages.Select(m => m.Text));
    }

    [Fact]
    public void Casing_differences_are_read_as_the_value_they_are_not_degraded()
    {
        // A proxy or a hand-written client that lower-cases a name has sent a value we know.
        // Degrading it would lose real information for no safety gain.
        JsonSerializer.Deserialize<CoachWriteStatus>("\"proposed\"", Client)
            .Should().Be(CoachWriteStatus.Proposed);
    }

    [Fact]
    public void A_numeric_enum_is_read_by_ordinal_and_an_unknown_ordinal_degrades()
    {
        JsonSerializer.Deserialize<CoachWriteStatus>("1", Client).Should().Be(CoachWriteStatus.Proposed);
        JsonSerializer.Deserialize<CoachWriteStatus>("9999", Client).Should().Be(CoachWriteStatus.Unknown);
        JsonSerializer.Deserialize<CoachWriteStatus>("-4", Client).Should().Be(CoachWriteStatus.Unknown);
    }

    [Fact]
    public void An_explicit_null_degrades_rather_than_throwing()
    {
        JsonSerializer.Deserialize<CoachWriteStatus>("null", Client).Should().Be(CoachWriteStatus.Unknown);
    }

    [Fact]
    public void A_nullable_enum_keeps_null_distinct_from_unreadable()
    {
        JsonSerializer.Deserialize<CoachWriteStatus?>("null", Client).Should().BeNull("absent is not unreadable");
        JsonSerializer.Deserialize<CoachWriteStatus?>("\"Teleported\"", Client).Should().Be(CoachWriteStatus.Unknown);
        JsonSerializer.Deserialize<CoachWriteStatus?>("\"Executed\"", Client).Should().Be(CoachWriteStatus.Executed);
    }

    // ---------------------------------------------------------------- what must still fail

    [Fact]
    public void Malformed_json_still_fails()
    {
        var truncated = () => JsonSerializer.Deserialize<CoachTurnResponse>(
            """{"sessionId":"s-1","status":"Completed",""", Client);
        truncated.Should().Throw<JsonException>("a truncated body is a broken response, not a new enum");

        var notJson = () => JsonSerializer.Deserialize<CoachTurnResponse>("<html>502</html>", Client);
        notJson.Should().Throw<JsonException>();

        var wrongRoot = () => JsonSerializer.Deserialize<CoachTurnResponse>("[1,2,3]", Client);
        wrongRoot.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_structurally_wrong_enum_position_still_fails()
    {
        // An object or an array where a name belongs is a shape error. Swallowing it would hide a
        // genuinely broken response behind a plausible-looking default.
        var asObject = () => JsonSerializer.Deserialize<CoachWriteStatus>("""{"value":"Proposed"}""", Client);
        asObject.Should().Throw<JsonException>();

        var asArray = () => JsonSerializer.Deserialize<CoachWriteStatus>("""["Proposed"]""", Client);
        asArray.Should().Throw<JsonException>();

        var asBool = () => JsonSerializer.Deserialize<CoachWriteStatus>("true", Client);
        asBool.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_wrongly_typed_neighbouring_field_still_fails()
    {
        // Tolerance is scoped to enum values. A string where a number belongs is a contract break
        // and has to surface as one.
        var act = () => JsonSerializer.Deserialize<CoachMessagePageDto>(
            """{"conversationId":"c-1","unreadableCount":{"nested":true},"items":[]}""", Client);

        act.Should().Throw<JsonException>();
    }

    // ---------------------------------------------------------------- the version gate seam

    [Fact]
    public void No_wire_value_is_gated_yet_and_projection_is_identity()
    {
        WireValueGateRegistry.All.Should().BeEmpty(
            "W1 ships the seam, not a behaviour change: nothing new is emitted and nothing is suppressed");

        foreach (var kind in Enum.GetValues<CoachMessageKind>())
        {
            WireValueGateRegistry.Project(kind, WireProtocolVersion.Unknown).Should().Be(kind);
            WireValueGateRegistry.Project(kind, WireProtocolVersion.Current).Should().Be(kind);
        }
    }

    [Theory]
    [InlineData(null, WireProtocolVersion.Unknown)]
    [InlineData("", WireProtocolVersion.Unknown)]
    [InlineData("not-a-number", WireProtocolVersion.Unknown)]
    [InlineData("0", WireProtocolVersion.Unknown)]
    [InlineData("-3", WireProtocolVersion.Unknown)]
    [InlineData("1", 1)]
    [InlineData("47", 47)]
    public void A_missing_or_unreadable_client_version_is_treated_as_the_oldest_client(
        string? header, int expected)
    {
        // Assuming the newest is how a gate ships a value to exactly the clients it was built to
        // protect.
        WireValueGateRegistry.ParseClientProtocolVersion(header).Should().Be(expected);
    }

    /// <summary>
    /// A complete, valid turn body, with the second message's kind supplied by the caller. Every
    /// required member is present: the point of each test is a value the client cannot name, not a
    /// body the server would never send.
    /// </summary>
    private static string TurnJson(string secondMessageKind) => $$"""
        {"sessionId":"s-1","turnId":"t-1","status":"Completed","stopReason":"Completed",
         "sessionStatus":"Active","clarificationsRemaining":2,
         "messages":[
           {"messageId":"m-1","role":"Coach","kind":"Text","text":"Here is today's plan.",
            "createdAtUtc":"2026-08-20T10:00:00Z"},
           {"messageId":"m-2","role":"Coach","kind":"{{secondMessageKind}}","text":"...",
            "createdAtUtc":"2026-08-20T10:00:01Z"}
         ],
         "activeConstraints":{{Constraints}},
         "planState":{{PlanState}},
         "expiresAtUtc":"2026-08-21T00:00:00Z"}
        """;

    private const string Constraints = """
        {"availableMinutes":20,"audioAllowed":true,"speechAllowed":true,"typingAllowed":true,
         "energyLevel":"Normal"}
        """;

    private static string PlanState => $$"""
        {"planDate":"2026-08-20","planVersion":"v1","appliedConstraints":{{Constraints}},
         "estimatedTotalMinutes":20,"completedCount":0,"totalCount":3,"completionPercentage":0}
        """;
}
