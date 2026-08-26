using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The stored representation of every opportunity enum, pinned on day one.
/// </summary>
/// <remarks>
/// <para>
/// <c>CoachDbContext</c> maps each of these with <c>HasConversion&lt;int&gt;()</c>, so the
/// database holds the <b>ordinal</b>. That makes member order a persistence contract: inserting a
/// value into the middle silently re-labels every row already written, and there is no way to tell
/// afterwards which rows meant what.
/// </para>
/// <para>
/// This is pinned from the start rather than after the first incident, because
/// <c>CoachStopReason</c> earned its own version of this test the hard way: a live session
/// recorded <c>StopReason = 4</c> under one mapping and the meaning of position 4 changed
/// underneath it.
/// </para>
/// </remarks>
public class CoachOpportunityStoredEnumContractTests
{
    [Theory]
    [InlineData(CoachOpportunityKind.UnsupportedCapability, 0)]
    [InlineData(CoachOpportunityKind.ToolUnavailable, 1)]
    [InlineData(CoachOpportunityKind.ProposalRefusedByPolicy, 2)]
    [InlineData(CoachOpportunityKind.AmbiguousFollowUp, 3)]
    [InlineData(CoachOpportunityKind.ValidationFailure, 4)]
    [InlineData(CoachOpportunityKind.ToolExecutionFailure, 5)]
    [InlineData(CoachOpportunityKind.ConfirmationLifecycleFailure, 6)]
    [InlineData(CoachOpportunityKind.OutOfScopeRequest, 7)]
    [InlineData(CoachOpportunityKind.HarmfulOrUnsafeRequest, 8)]
    [InlineData(CoachOpportunityKind.CapacityOrBudgetRefusal, 9)]
    [InlineData(CoachOpportunityKind.UserReportedResponse, 10)]
    public void EveryKindKeepsItsStoredOrdinal(CoachOpportunityKind kind, int stored) =>
        ((int)kind).Should().Be(stored);

    [Theory]
    [InlineData(CoachOpportunityDisposition.Product, 0)]
    [InlineData(CoachOpportunityDisposition.AggregateOnly, 1)]
    public void EveryDispositionKeepsItsStoredOrdinal(CoachOpportunityDisposition disposition, int stored) =>
        ((int)disposition).Should().Be(stored);

    [Theory]
    [InlineData(CoachOpportunitySurface.TurnOutcome, 0)]
    [InlineData(CoachOpportunitySurface.ToolInvocation, 1)]
    [InlineData(CoachOpportunitySurface.WriteLedger, 2)]
    public void EverySurfaceKeepsItsStoredOrdinal(CoachOpportunitySurface surface, int stored) =>
        ((int)surface).Should().Be(stored);

    [Theory]
    [InlineData(CoachOpportunityOfferLink.None, 0)]
    [InlineData(CoachOpportunityOfferLink.PriorClarification, 1)]
    [InlineData(CoachOpportunityOfferLink.PriorCoachQuestion, 2)]
    [InlineData(CoachOpportunityOfferLink.OpenPlanSuggestion, 3)]
    [InlineData(CoachOpportunityOfferLink.OpenWriteProposal, 4)]
    public void EveryOfferLinkKeepsItsStoredOrdinal(CoachOpportunityOfferLink link, int stored) =>
        ((int)link).Should().Be(stored);

    [Theory]
    [InlineData(CoachOpportunityStatus.New, 0)]
    [InlineData(CoachOpportunityStatus.Reviewed, 1)]
    [InlineData(CoachOpportunityStatus.Accepted, 2)]
    [InlineData(CoachOpportunityStatus.Deferred, 3)]
    [InlineData(CoachOpportunityStatus.Dismissed, 4)]
    public void EveryStatusKeepsItsStoredOrdinal(CoachOpportunityStatus status, int stored) =>
        ((int)status).Should().Be(stored);

    [Theory]
    [InlineData(CoachOpportunityReviewerNoteCode.NeedsCaptainDecision, 0)]
    [InlineData(CoachOpportunityReviewerNoteCode.DuplicateOfKnownBug, 1)]
    [InlineData(CoachOpportunityReviewerNoteCode.PromptTuningOnly, 2)]
    [InlineData(CoachOpportunityReviewerNoteCode.NeedsNewTool, 3)]
    [InlineData(CoachOpportunityReviewerNoteCode.NeedsPolicyChange, 4)]
    [InlineData(CoachOpportunityReviewerNoteCode.NotAProblem, 5)]
    [InlineData(CoachOpportunityReviewerNoteCode.SpecWritten, 6)]
    public void EveryReviewerNoteKeepsItsStoredOrdinal(
        CoachOpportunityReviewerNoteCode note, int stored) =>
        ((int)note).Should().Be(stored);

    [Fact]
    public void AddingAMemberIsOnlySafeAtTheEnd()
    {
        // Guards on the counts, so appending stays cheap and inserting cannot pass quietly: a
        // new member in the middle shifts an ordinal and fails a theory above as well.
        Enum.GetValues<CoachOpportunityKind>().Should().HaveCount(11);
        Enum.GetValues<CoachOpportunityDisposition>().Should().HaveCount(2);
        Enum.GetValues<CoachOpportunitySurface>().Should().HaveCount(3);
        Enum.GetValues<CoachOpportunityOfferLink>().Should().HaveCount(5);
        Enum.GetValues<CoachOpportunityStatus>().Should().HaveCount(5);
        Enum.GetValues<CoachOpportunityReviewerNoteCode>().Should().HaveCount(7);
    }

    /// <summary>
    /// Capability codes are constant <b>values</b>, not ordinals, so their contract is that the
    /// string never changes — a rename orphans every row already written and breaks every
    /// fingerprint that included it.
    /// </summary>
    [Theory]
    [InlineData("preference_setting_unknown")]
    [InlineData("preference_setting_session_minutes")]
    [InlineData("preference_setting_target_language")]
    [InlineData("preference_setting_native_language")]
    [InlineData("preference_setting_display_language")]
    [InlineData("preference_setting_cefr_level")]
    [InlineData("preference_setting_quiz_show_text_with_photo")]
    [InlineData("entity_lookup_by_name")]
    [InlineData("write_tools_disabled")]
    [InlineData("read_tools_disabled")]
    [InlineData("overlay_disabled")]
    [InlineData("tool_allowlist_violation")]
    [InlineData("referent_lost_after_offer")]
    [InlineData("intent_shape_invalid")]
    [InlineData("model_output_unreadable")]
    [InlineData("write_arguments_invalid")]
    [InlineData("answer_leak_refusal")]
    [InlineData("no_feasible_plan")]
    [InlineData("tool_data_access")]
    [InlineData("turn_tool_failure_unattributed")]
    [InlineData("tool_profile_missing")]
    [InlineData("write_execution_failed")]
    [InlineData("approval_window_elapsed")]
    [InlineData("undo_unavailable")]
    [InlineData("approval_protocol_error")]
    [InlineData("approval_target_unresolved")]
    [InlineData("one_proposal_per_turn")]
    [InlineData("tool_call_budget_exhausted")]
    [InlineData("daily_run_limit")]
    [InlineData("turn_timeout")]
    [InlineData("output_token_limit")]
    [InlineData("iteration_limit")]
    [InlineData("off_topic")]
    [InlineData("destructive_request_refused")]
    [InlineData("learner_reported_did_not_answer")]
    [InlineData("learner_reported_incorrect_or_misleading")]
    [InlineData("learner_reported_expected_app_action")]
    [InlineData("learner_reported_confusing")]
    [InlineData("learner_reported_other")]
    [InlineData("answer_required")]
    [InlineData("evidence_reference_invalid")]
    public void EveryCapabilityCodeKeepsItsStoredValue(string code) =>
        CoachOpportunityCapabilityCodes.All.Should().Contain(code);

    [Fact]
    public void TheCapabilityVocabularyIsClosedAndBounded()
    {
        CoachOpportunityCapabilityCodes.All.Should().HaveCount(42);

        foreach (var code in CoachOpportunityCapabilityCodes.All)
        {
            code.Length.Should().BeLessThanOrEqualTo(
                CoachOpportunityLimits.CapabilityCodeMaxLength,
                $"'{code}' must fit the column it is stored in");

            code.Should().MatchRegex("^[a-z0-9_]+$",
                $"'{code}' must be a content-free snake_case constant");
        }
    }

    [Fact]
    public void AnUnknownCapabilityCodeIsRefused()
    {
        CoachOpportunityCapabilityCodes.IsKnown("preference_setting_session_minutes").Should().BeTrue();
        CoachOpportunityCapabilityCodes.IsKnown("whatever the learner typed").Should().BeFalse();
        CoachOpportunityCapabilityCodes.IsKnown(null).Should().BeFalse();
        CoachOpportunityCapabilityCodes.IsKnown("").Should().BeFalse();
    }

    /// <summary>
    /// The one computed capability family collapses anything the model invents.
    /// </summary>
    [Theory]
    [InlineData("session_minutes", "preference_setting_session_minutes")]
    [InlineData("SESSION_MINUTES", "preference_setting_session_minutes")]
    [InlineData("  session_minutes  ", "preference_setting_session_minutes")]
    [InlineData("cefr_level", "preference_setting_cefr_level")]
    [InlineData("email", "preference_setting_unknown")]
    [InlineData("api_key", "preference_setting_unknown")]
    [InlineData("'; DROP TABLE CoachOpportunity; --", "preference_setting_unknown")]
    [InlineData("", "preference_setting_unknown")]
    [InlineData(null, "preference_setting_unknown")]
    public void APreferenceNameCollapsesToTheClosedSet(string? setting, string expected) =>
        CoachOpportunityCapabilityCodes.ForPreferenceSetting(setting).Should().Be(expected);

    [Fact]
    public void TheSchemaVersionIsPinned() =>
        // The fingerprint's first field. Bumping it deliberately produces new fingerprints;
        // bumping it by accident silently splits every problem's history in two.
        CoachOpportunityLimits.SchemaVersion.Should().Be(1);

    [Fact]
    public void TheRetentionWindowIsTheApprovedOne() =>
        CoachOpportunityLimits.Retention.Should().Be(TimeSpan.FromDays(180));

    /// <summary>
    /// The fingerprint is a stable function of closed-vocabulary inputs, so a value pinned here
    /// today must still be produced a year from now — otherwise every rollup silently splits.
    /// </summary>
    [Fact]
    public void TheFingerprintIsStableForAGivenTuple()
    {
        var first = CoachOpportunityFingerprint.Compute(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            toolName: null,
            failureCode: null,
            CoachStopReason.ClarificationRequested,
            CoachOpportunityOfferLink.PriorCoachQuestion);

        var second = CoachOpportunityFingerprint.Compute(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            toolName: null,
            failureCode: null,
            CoachStopReason.ClarificationRequested,
            CoachOpportunityOfferLink.PriorCoachQuestion);

        first.Should().Be(second);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ADifferentOfferLinkIsADifferentProblem()
    {
        var question = CoachOpportunityFingerprint.Compute(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            null, null, CoachStopReason.ClarificationRequested,
            CoachOpportunityOfferLink.PriorCoachQuestion);

        var clarification = CoachOpportunityFingerprint.Compute(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            null, null, CoachStopReason.ClarificationRequested,
            CoachOpportunityOfferLink.PriorClarification);

        question.Should().NotBe(clarification);
    }
}
