using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// Correction state on the real turn path.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these add to the lifecycle tests.</b> <c>CoachDisputeLifecycleTests</c> proves the
/// coordinator's semantics — what opens, what repeats, what clears — by calling it directly, and
/// every one of those tests passed while nothing on the turn path called it. The classifier ran on
/// no learner message, the dispute reached no response, and the rule had no dispute to fire on.
/// These tests drive <c>SubmitTurnAsync</c> and assert on what a learner would receive, so removing
/// a call site fails them.
/// </para>
/// </remarks>
public sealed class CoachDisputeTurnPathTests
{
    private const string PriorCoachMessageId = "msg-coach-0001";
    private const string Correction = "No, that's wrong — I have practised those this week.";
    private const string OrdinaryQuestion = "How does the polite ending work?";

    /// <summary>A sentence about the learner that no read supports, so the ladder has work to do.</summary>
    private const string UnverifiedClaim = "You have reviewed these words plenty of times already.";

    // ------------------------------------------------------------------ off

    [Fact]
    public async Task The_flag_off_is_a_total_bypass_on_a_real_turn()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState(false);

        var response = await AskAsync(harness, Correction, WithPriorCoachMessage());

        response.Dispute.Should().BeNull(
            "off means the classifier is never called, not that its result is discarded");
        harness.Service.CurrentTurnDispute.Should().BeNull();
    }

    // ----------------------------------------------------------------- open

    [Fact]
    public async Task A_correction_opens_a_dispute_on_the_turn_that_carries_it()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();

        var response = await AskAsync(harness, Correction, WithPriorCoachMessage());

        response.Dispute.Should().NotBeNull(
            "the correcting turn is itself the first answer the correction constrains; opening "
            + "afterwards would give the coach one free repeat");

        response.Dispute!.Status.Should().Be(CoachDisputeStatus.Open);
        response.Dispute.Signal.Should().NotBe(CoachDisputeSignal.Unknown);
    }

    [Fact]
    public async Task The_dispute_is_keyed_to_the_exact_prior_coach_message()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();

        var response = await AskAsync(harness, Correction, WithPriorCoachMessage());

        response.Dispute!.DisputedMessageId.Should().Be(
            PriorCoachMessageId,
            "a dispute keyed to the turn in flight would constrain the answer being disputed, and "
            + "one keyed to nothing would constrain any answer at all");

        harness.Service.CurrentTurnDispute!.DisputedMessageId.Should().Be(PriorCoachMessageId);
    }

    [Fact]
    public async Task An_ordinary_message_opens_nothing()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();

        var response = await AskAsync(harness, OrdinaryQuestion, WithPriorCoachMessage());

        response.Dispute.Should().BeNull(
            "a dispute constrains the next answer, so opening one on a plain question would "
            + "degrade a turn for a learner who was merely curious");
    }

    [Fact]
    public async Task With_no_prior_coach_message_there_is_nothing_to_correct()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();

        var response = await AskAsync(harness, Correction, CoachTurnExecutionContext.Default);

        response.Dispute.Should().BeNull(
            "this is also the permanent state of a host without durable history");
    }

    // --------------------------------------------------------------- carry

    [Fact]
    public async Task An_open_dispute_is_carried_rather_than_re_anchored()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();

        var carried = OpenDispute("msg-coach-original");

        var response = await AskAsync(
            harness,
            "No, still wrong.",
            new CoachTurnExecutionContext
            {
                ActiveDispute = carried,
                PriorCoachMessageId = "msg-coach-newer",
                PriorTrace = null
            });

        response.Dispute!.DisputedMessageId.Should().Be(
            "msg-coach-original",
            "re-anchoring on every push-back would slide the original claim out from under the "
            + "constraint that is supposed to force it to change");
    }

    // ------------------------------------------------------- the rule fires

    [Fact]
    public async Task An_open_dispute_reaches_the_rules_so_a_repeat_can_be_caught()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();
        harness.SetGroundingStage(CoachGroundingStage.Observe);
        harness.SeedPracticeBalanceRead();

        // The dispute names the definition the disputed answer read. This turn reads the same one
        // again and says the same kind of thing, which is the repeat the learner objected to.
        var dispute = OpenDispute(
            PriorCoachMessageId,
            SentenceStudio.Api.Coach.Tools.CoachScopeDefinition.PracticeWindowBalance);

        await AskAsync(
            harness,
            Correction,
            new CoachTurnExecutionContext
            {
                ActiveDispute = dispute,
                PriorCoachMessageId = PriorCoachMessageId
            },
            answer: UnverifiedClaim);

        harness.ClaimFindings.Record.Should().NotBeNull(
            "the ladder ran, so the dispute had somewhere to be judged");

        harness.ClaimFindings.Record!.Findings.Should().Contain(
            finding => finding.Rule == CoachClaimRuleCode.RepeatedDisputedClaim,
            "the rule was registered and unreachable until the dispute was threaded into the "
            + "context the ladder builds");
    }

    [Fact]
    public async Task Without_a_dispute_the_repeat_rule_stays_silent_on_the_same_answer()
    {
        // The control. Same answer, same read, no correction — and the rule must not fire, or the
        // test above would be proving nothing about the dispute.
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();
        harness.SetGroundingStage(CoachGroundingStage.Observe);
        harness.SeedPracticeBalanceRead();

        await AskAsync(harness, OrdinaryQuestion, WithPriorCoachMessage(), answer: UnverifiedClaim);

        (harness.ClaimFindings.Record?.Findings ?? []).Should().NotContain(
            finding => finding.Rule == CoachClaimRuleCode.RepeatedDisputedClaim);
    }

    // ------------------------------------------------------------- resolve

    [Fact]
    public async Task A_re_read_with_a_materially_different_definition_clears_the_dispute()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();
        harness.SetGroundingStage(CoachGroundingStage.Observe);

        // Disputed over the practice window; this turn reads the vocabulary set instead. Different
        // typed facts, not merely a second call.
        harness.SeedWithheldVocabularyRead();

        var dispute = OpenDispute(
            PriorCoachMessageId,
            SentenceStudio.Api.Coach.Tools.CoachScopeDefinition.PracticeWindowBalance);

        var response = await AskAsync(
            harness,
            Correction,
            new CoachTurnExecutionContext
            {
                ActiveDispute = dispute,
                PriorCoachMessageId = PriorCoachMessageId
            });

        response.Dispute!.Status.Should().Be(
            CoachDisputeStatus.ResolvedByReRead,
            "the coach looked somewhere new, which is the strongest resolution available");

        harness.Service.CurrentTurnDispute!.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task An_unresolved_turn_leaves_the_dispute_open_for_the_next_one()
    {
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();
        harness.SetGroundingStage(CoachGroundingStage.Observe);
        harness.SeedPracticeBalanceRead();

        var dispute = OpenDispute(
            PriorCoachMessageId,
            SentenceStudio.Api.Coach.Tools.CoachScopeDefinition.PracticeWindowBalance);

        var response = await AskAsync(
            harness,
            Correction,
            new CoachTurnExecutionContext
            {
                ActiveDispute = dispute,
                PriorCoachMessageId = PriorCoachMessageId
            },
            answer: UnverifiedClaim);

        response.Dispute!.Status.Should().Be(
            CoachDisputeStatus.Open,
            "a dispute clears on a compliant answer, not on the passage of a turn");
    }

    // ------------------------------------------------------- the call sites

    [Fact]
    public void The_coordinator_is_called_from_the_session_service()
    {
        var service = SessionServiceSource();

        CountOutsideComments(service, "_disputes.TryOpen(").Should().Be(
            1, "the classifier reaches a real learner message through exactly one call");
        CountOutsideComments(service, "_disputes.Resolve(").Should().Be(
            1, "and a real answer closes it through exactly one");
        CountOutsideComments(service, "OpenOrCarryDispute(").Should().Be(
            2, "one declaration and one invocation, at turn start");
        CountOutsideComments(service, "ResolveDispute(").Should().Be(
            2, "one declaration and one invocation, after the ladder");
    }

    [Fact]
    public void The_dispute_reaches_the_grounding_context_and_the_response()
    {
        var service = SessionServiceSource();
        var evaluator = ApiSource("Coach/Validation/Claims/CoachTurnGroundingEvaluator.cs");

        service.Should().Contain("_turnDispute);",
            "the ladder is handed the dispute; without it RepeatedDisputedClaim cannot fire");
        evaluator.Should().Contain("Dispute = dispute",
            "and the evaluator puts it on the context the rules read");

        CountOutsideComments(service, "CoachDisputeProjection.Project(_turnDispute)").Should().Be(
            2, "the turn response and the session response both report it");
    }

    [Fact]
    public void The_durable_layer_loads_and_persists_the_dispute()
    {
        var conversation = ApiSource("Coach/Application/History/CoachConversationService.cs");

        CountOutsideComments(conversation, "LoadDisputeContextAsync(").Should().Be(
            2, "one declaration and one call, before the model runs");
        CountOutsideComments(conversation, "ActiveDispute = disputeContext.ActiveDispute").Should().Be(
            2, "both the first attempt and the rebuild retry carry the same correction");
        // W9 R2 added the grounding summary as a third argument. Pinned with it rather than
        // loosened to a prefix: the point of the scan is that BOTH write sites persist everything
        // the turn ended with, and a prefix match would keep passing if one site dropped an
        // argument the other kept.
        CountOutsideComments(
                conversation,
                "SerializeOutcome(answer, _sessions.CurrentTurnDispute, _sessions.CurrentTurnGrounding)")
            .Should().Be(2, "both completion writes persist what the turn ended with");
    }

    [Fact]
    public void The_notice_is_rendered_in_the_shared_component_tree()
    {
        var pane = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.UI", "Shared", "Coach", "CoachChatPane.razor"));

        pane.Should().Contain("<CoachDisputeNotice",
            "both hosts render the same component, so neither can invent its own banner");
        pane.Should().Contain("Coach.Dispute");
    }

    // ---------------------------------------------------------------- scope

    [Fact]
    public void The_load_is_bounded_and_owner_scoped_by_construction()
    {
        var store = ApiSource("Coach/Persistence/History/CoachTurnOperationStore.cs");
        var conversation = ApiSource("Coach/Application/History/CoachConversationService.cs");

        store.Should().Contain("HasOwner(owner, nameof(GetRecentOutcomesAsync))",
            "an empty owner reads nothing rather than everything");
        store.Should().Contain("o.ConversationId == conversationId",
            "a dispute must not carry between two conversations belonging to one learner");
        store.Should().Contain("Math.Min(limit, MaxRecentOutcomes)",
            "the caller cannot ask for an unbounded scan on the front of a turn");

        conversation.Should().Contain("DisputeLookbackTurns");
    }

    [Fact]
    public void The_load_fails_closed_on_every_failure_mode()
    {
        var conversation = ApiSource("Coach/Application/History/CoachConversationService.cs");

        conversation.Should().Contain("if (_disputes is null || !_disputes.IsEnabled)");
        conversation.Should().Contain("catch (Exception ex) when (ex is not OperationCanceledException)",
            "a correction the server cannot read is a correction it cannot honour");
    }

    // -------------------------------------------------------------- helpers

    private static CoachTurnExecutionContext WithPriorCoachMessage() => new()
    {
        PriorCoachMessageId = PriorCoachMessageId
    };

    private static CoachTurnDisputeState OpenDispute(
        string messageId,
        params SentenceStudio.Api.Coach.Tools.CoachScopeDefinition[] definitions) =>
        new(CoachCorrectionSignal.WrongClaim,
            messageId,
            new DateTime(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc),
            ResolvedAtUtc: null,
            CoachDisputeResolution.Open,
            definitions);

    private static async Task<CoachTurnResponse> AskAsync(
        CoachApplicationHarness harness,
        string learnerText,
        CoachTurnExecutionContext context,
        string? answer = null)
    {
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent
                {
                    Topic = CoachAnswerTopic.Vocabulary,
                    Blocks =
                    [
                        new CoachAnswerBlockIntent
                        {
                            Kind = CoachAnswerBlockKind.Answer,
                            Spans =
                            [
                                new CoachAnswerSpanIntent
                                {
                                    Text = answer ?? "The verb ending changes with the politeness level.",
                                    Language = CoachLanguageRole.Display
                                }
                            ]
                        }
                    ]
                },
                CoachMessage = string.Empty
            }
        };

        var result = await harness.Service.SubmitTurnAsync(
            sessionId,
            new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = learnerText },
            context);

        result.IsOk.Should().BeTrue();
        return result.Value!;
    }

    private static int CountOutsideComments(string source, string token)
    {
        var code = string.Join('\n', source.Split('\n').Select(line =>
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment >= 0 ? line[..comment] : line;
        }));

        var count = 0;
        var index = 0;
        while ((index = code.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string SessionServiceSource() =>
        ApiSource("Coach/Application/CoachSessionService.cs");

    private static string ApiSource(string relative) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Api",
            relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return directory!.FullName;
    }
}
