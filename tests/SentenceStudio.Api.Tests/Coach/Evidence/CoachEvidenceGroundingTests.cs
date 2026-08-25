using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Evidence;

/// <summary>
/// The three places the session service hands evidence to a client, now fed by real reads.
/// </summary>
/// <remarks>
/// <para>
/// Two of the three used to be the literal <c>Array.Empty&lt;CoachEvidenceDto&gt;()</c> and the
/// third built cards out of the model's own claim. Between them that meant a learner could open
/// the disclosure behind a coach statement and be shown either nothing at all or a card generated
/// from the sentence it was supposed to corroborate. Neither state was distinguishable from a
/// grounded one.
/// </para>
/// <para>
/// These tests drive the real service through the real harness. Each asserts the property that the
/// old code satisfied vacuously: that the evidence came from somewhere.
/// </para>
/// </remarks>
public class CoachEvidenceGroundingTests
{
    // =====================================================================
    // Site: the turn response
    // =====================================================================

    [Fact]
    public async Task A_turn_that_read_a_practice_balance_shows_it_as_evidence()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        harness.SeedPracticeBalanceRead(windowDays: 14, activityTypes: 3);
        harness.Coach.NextResult = NoChange(WithReference());

        var turn = await SubmitAsync(harness, session);

        turn.IsOk.Should().BeTrue(turn.Detail);
        var evidence = turn.Value!.Evidence.Should().ContainSingle().Subject;

        evidence.Kind.Should().Be(CoachEvidenceKind.PracticeBalance);
        evidence.Coverage.Should().Be(CoachEvidenceCoverage.WindowBounded);
        evidence.DefinitionCode.Should().Be(CoachDefinitionCode.PracticeWindowBalance);
        evidence.ReturnedCount.Should().Be(3, "the count comes from the read, not from the claim");
        evidence.WindowStartDate.Should().Be(harness.DateContext.UserLocalDate.AddDays(-13));
        evidence.WindowEndDate.Should().Be(harness.DateContext.UserLocalDate);
        evidence.Values.Should().NotBeEmpty("the old projection attached none");
        evidence.AsOfUtc!.Value.Ticks.Should().Be(
            evidence.AsOfUtc.Value.Ticks - (evidence.AsOfUtc.Value.Ticks % TimeSpan.TicksPerSecond),
            "the instant is whole-second all the way to the wire");
    }

    [Fact]
    public async Task A_turn_that_read_nothing_and_claims_nothing_simply_has_no_evidence()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        harness.Coach.NextResult = NoChange(new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = "Your plan looks fine as it is."
        });

        var turn = await SubmitAsync(harness, session);

        turn.IsOk.Should().BeTrue(turn.Detail);
        turn.Value!.Status.Should().Be(CoachTurnStatus.Completed,
            "an answer that cites nothing needs nothing to cite; this is honest, not a failure");
        turn.Value.Evidence.Should().BeEmpty();
    }

    // =====================================================================
    // The gate: a claim with no read behind it
    // =====================================================================

    [Fact]
    public async Task A_turn_that_cites_evidence_without_reading_anything_is_refused()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        // No SeedPracticeBalanceRead. The model asserts it consulted a practice balance; nothing
        // did. Before W3b this produced a card built from the assertion itself.
        harness.Coach.NextResult = NoChange(WithReference());

        var turn = await SubmitAsync(harness, session);

        turn.IsOk.Should().BeTrue(turn.Detail);
        turn.Value!.Status.Should().Be(
            CoachTurnStatus.Rejected,
            "a citation of reads that never happened is a refusal, not a card and not a silent empty list");
        turn.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        turn.Value.Evidence.Should().BeEmpty();
    }

    [Fact]
    public async Task The_refusal_does_not_change_the_plan()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        var before = harness.PlanService.Current.Items.Count;

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.DirectConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 5 },
                CoachMessage = "Shortening today.",
                EvidenceReferences =
                [
                    new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance, WindowDays = 14 }
                ]
            }
        };

        var turn = await SubmitAsync(harness, session);

        turn.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        harness.PlanService.Current.Items.Count.Should().Be(
            before, "an ungrounded turn is refused before it writes, exactly as a malformed one is");
    }

    [Fact]
    public async Task A_read_that_produced_no_evidence_bucket_still_supports_a_citation()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        // Skills have no CoachEvidenceKind member, so this read shows no card. It is still a read,
        // and refusing the turn for a gap in a wire enum would be the gate misfiring.
        SeedRead(harness, CoachToolNames.GetSkillList, CoachScopeDefinition.ActiveSkillList);
        harness.Coach.NextResult = NoChange(WithReference());

        var turn = await SubmitAsync(harness, session);

        turn.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        turn.Value.Evidence.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_read_does_not_support_a_citation()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        SeedRead(
            harness,
            CoachToolNames.GetPracticeBalance,
            CoachScopeDefinition.PracticeWindowBalance,
            outcome: CoachToolCallOutcome.Faulted);

        harness.Coach.NextResult = NoChange(WithReference());

        var turn = await SubmitAsync(harness, session);

        turn.Value!.Status.Should().Be(
            CoachTurnStatus.Rejected, "a read that threw grounds nothing, however it was attempted");
    }

    // =====================================================================
    // Site: the session read, and the stored suggestion re-read
    // =====================================================================

    [Fact]
    public async Task A_session_read_shows_no_evidence_because_it_consulted_nothing()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        var read = await harness.Service.GetSessionAsync(session, CancellationToken.None);

        read.IsOk.Should().BeTrue(read.Detail);
        read.Value!.Evidence.Should().BeEmpty(
            "a plain session read makes no scoped call, so it has nothing to show — and it is now "
            + "empty because the buffer is, not because the field was pinned shut");
    }

    [Fact]
    public async Task A_session_read_reports_whatever_that_request_actually_read()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        // The hardcoded empty could not have done this. It is what would have gone wrong the first
        // time a session read ran inside a turn that had consulted something.
        harness.SeedPracticeBalanceRead();

        var read = await harness.Service.GetSessionAsync(session, CancellationToken.None);

        read.Value!.Evidence.Should().ContainSingle()
            .Which.DefinitionCode.Should().Be(CoachDefinitionCode.PracticeWindowBalance);
    }

    // =====================================================================
    // Embargo
    // =====================================================================

    [Fact]
    public async Task Withheld_due_words_cross_as_a_count_and_a_reason_and_never_as_words()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        SeedRead(
            harness,
            CoachToolNames.ListUserVocabularies,
            CoachScopeDefinition.UndueVocabularySearch,
            returned: 10,
            matched: 14,
            withheld: 4,
            withheldReason: CoachScopeWithheldReason.DueReviewEmbargo);

        harness.Coach.NextResult = NoChange(new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = "Ten words are ready to practise.",
            EvidenceReferences =
            [
                new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.VocabularyDue }
            ]
        });

        var turn = await SubmitAsync(harness, session);

        var evidence = turn.Value!.Evidence.Should().ContainSingle().Subject;
        evidence.MatchedCount.Should().Be(14);
        evidence.ReturnedCount.Should().Be(10);
        evidence.WithheldCount.Should().Be(4);
        evidence.WithheldReason.Should().Be(CoachWithheldReason.DueReviewEmbargo);

        var json = System.Text.Json.JsonSerializer.Serialize(turn.Value.Evidence);
        foreach (var forbidden in new[] { "만기", "사과", "apple", "mnemonic", "transcript" })
        {
            json.Should().NotContain(forbidden, "the count crosses and the words do not");
        }
    }

    [Fact]
    public async Task No_evidence_field_can_carry_free_text_from_a_read()
    {
        using var harness = new CoachApplicationHarness();
        var session = await StartAsync(harness);

        harness.SeedPracticeBalanceRead();
        harness.Coach.NextResult = NoChange(WithReference());

        var turn = await SubmitAsync(harness, session);
        var evidence = turn.Value!.Evidence.Should().ContainSingle().Subject;

        // Label and Summary are fixed server copy chosen by the definition code, so no read can put
        // its own strings on them. Asserted against the projection's own table rather than a
        // literal, so a copy change stays a one-place edit.
        evidence.Label.Should().Be(
            SentenceStudio.Api.Coach.Evidence.CoachTurnEvidenceProjection.LabelFor(
                CoachEvidenceKind.PracticeBalance));
        evidence.Summary.Should().Be(
            SentenceStudio.Api.Coach.Evidence.CoachTurnEvidenceProjection.SummaryFor(
                CoachScopeDefinition.PracticeWindowBalance));

        foreach (var value in evidence.Values)
        {
            value.Unit.Should().Be(CoachEvidenceUnit.Items, "every projected value is a row count");
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static CoachTurnIntent WithReference() => new()
    {
        Kind = CoachIntentKind.NoChange,
        CoachMessage = "Your input and output are close to balanced this fortnight.",
        EvidenceReferences =
        [
            new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance, WindowDays = 14 }
        ]
    };

    private static CoachAgentTurnResult NoChange(CoachTurnIntent intent) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = intent
    };

    private static void SeedRead(
        CoachApplicationHarness harness,
        string toolName,
        CoachScopeDefinition definition,
        int returned = 1,
        int? matched = null,
        int withheld = 0,
        CoachScopeWithheldReason withheldReason = CoachScopeWithheldReason.None,
        CoachToolCallOutcome outcome = CoachToolCallOutcome.Succeeded)
    {
        ((ICoachTurnObservationSink)harness.Observations).Add(new CoachToolCallObservation(
            toolName,
            Ordinal: 1,
            Outcome: outcome,
            FailureKind: null,
            ArgumentMask: CoachToolArgumentMask.None,
            ElapsedMs: 1,
            Scope: outcome == CoachToolCallOutcome.Succeeded || definition != CoachScopeDefinition.Unspecified
                ? new CoachResultScope
                {
                    Coverage = CoachScopeCoverage.CompleteOwnedSet,
                    Order = CoachScopeOrder.MasteryDescending,
                    OrderHonored = true,
                    Filters = CoachScopeFilters.OwnerScoped,
                    AsOfUtc = harness.DateContext.UtcNow,
                    ReturnedCount = returned,
                    MatchedCount = matched,
                    WithheldCount = withheld,
                    WithheldReason = withheldReason,
                    DefinitionCode = definition,
                    MinimumEvidence = CoachScopeMinimumEvidence.None,
                    TieBreak = CoachScopeTieBreak.None,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                }
                : null));
    }

    private static async Task<string> StartAsync(CoachApplicationHarness harness)
    {
        var started = await harness.Service.StartSessionAsync(
            new StartCoachSessionRequest(), CancellationToken.None);

        started.IsOk.Should().BeTrue(started.Detail);
        return started.Value!.SessionId;
    }

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitAsync(
        CoachApplicationHarness harness,
        string sessionId) =>
        harness.Service.SubmitTurnAsync(
            sessionId,
            new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "how am I doing?" },
            CancellationToken.None);
}
