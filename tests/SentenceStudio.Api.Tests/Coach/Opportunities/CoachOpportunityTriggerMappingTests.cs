using System.Reflection;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Mapping;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// Every closed-vocabulary member that can reach a mapper must have a declared disposition.
/// </summary>
/// <remarks>
/// <para>
/// This is the build-breaking gate that keeps the taxonomy honest. Adding a
/// <see cref="CoachWriteFailureCodes"/> constant, a <see cref="CoachToolFailureKind"/> member, a
/// <see cref="CoachStopReason"/>, a <see cref="CoachIntentKind"/>, or a
/// <see cref="CoachViolationKind"/> without deciding what the ledger does with it fails here,
/// rather than silently producing no signal — which is how a real product gap becomes invisible.
/// </para>
/// <para>
/// The same property <c>CoachWriteOperationStates</c> relies on: a classification that can be
/// left out is a classification that eventually is.
/// </para>
/// </remarks>
public class CoachOpportunityTriggerMappingTests
{
    private static IEnumerable<string> WriteFailureCodes() =>
        typeof(CoachWriteFailureCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void EveryWriteFailureCodeHasADeclaredDisposition()
    {
        var undeclared = WriteFailureCodes()
            .Where(code => CoachWriteAuditOpportunityMapper.DispositionFor(code)
                           == CoachWriteAuditOpportunityMapper.WriteFailureDisposition.Unmapped)
            .ToList();

        undeclared.Should().BeEmpty(
            "a new refusal code with no declared disposition records nothing, and a product gap " +
            "that records nothing is invisible: add a case to CoachWriteAuditOpportunityMapper");
    }

    [Fact]
    public void EveryToolFailureKindHasADeclaredDisposition()
    {
        var undeclared = Enum.GetValues<CoachToolFailureKind>()
            .Where(kind => CoachToolFailureOpportunityMapper.DispositionFor(kind)
                           == CoachToolFailureOpportunityMapper.ToolFailureDisposition.Unmapped)
            .ToList();

        undeclared.Should().BeEmpty();
    }

    [Fact]
    public void EveryStopReasonHasADeclaredDisposition()
    {
        var undeclared = Enum.GetValues<CoachStopReason>()
            .Where(reason => CoachTurnOutcomeOpportunityMapper.DispositionFor(reason)
                             == CoachTurnOutcomeOpportunityMapper.TurnDisposition.Unmapped)
            .ToList();

        undeclared.Should().BeEmpty();
    }

    /// <summary>
    /// A turn-level tool failure is counted under its own code, never as a data-access failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CoachStopReason.ToolFailure</c> is a single turn-level verdict: the turn boundary knows
    /// a tool failed and nothing else. <c>ObservedCoachFunction</c> — which knows <em>which</em>
    /// tool and <em>which</em> failure kind — has already recorded the detailed row from the tool
    /// boundary.
    /// </para>
    /// <para>
    /// Mapping both to <c>tool_data_access</c> asserted a cause nobody established and made the
    /// rollup read as though every tool failure were a read failure — while double-counting one
    /// event under a single code. Two codes keep the counts comparable instead: a turn-level
    /// count materially higher than the tool-boundary count means failures are reaching the turn
    /// from somewhere the observer does not wrap, which is a real gap worth seeing.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATurnLevelToolFailureIsNotLabelledAsDataAccess()
    {
        var turnSignal = CoachTurnOutcomeOpportunityMapper.Map(
            referentLoss: null,
            CoachStopReason.ToolFailure,
            intent: null,
            violation: null,
            "conv-1",
            "turn-1",
            turnOperationId: null);

        turnSignal.Should().NotBeNull("the turn-level count is still worth having");

        turnSignal!.Value.CapabilityCode
            .Should().Be(CoachOpportunityCapabilityCodes.TurnToolFailureFallback);

        turnSignal.Value.CapabilityCode
            .Should().NotBe(CoachOpportunityCapabilityCodes.ToolDataAccess,
                "the turn boundary does not know the tool failed on a data read, and saying so " +
                "would both invent a cause and double-count the tool boundary's own row");

        turnSignal.Value.Disposition.Should().Be(CoachOpportunityDisposition.AggregateOnly,
            "an unattributed failure is a number, never a dossier");

        turnSignal.Value.ToolName.Should().BeNull(
            "the turn boundary has no tool name to give; the one that does is the tool boundary");

        // And the tool boundary keeps the detailed code, so the two never collide.
        var toolSignal = CoachToolFailureOpportunityMapper.Map(
            CoachToolFailureKind.DataAccess, CoachToolNames.GetSkillList, "conv-1", "turn-1");

        toolSignal.Should().NotBeNull();
        toolSignal!.Value.CapabilityCode
            .Should().Be(CoachOpportunityCapabilityCodes.ToolDataAccess);
        toolSignal.Value.ToolName.Should().Be(CoachToolNames.GetSkillList);

        // Different capability codes mean different fingerprints, which is what keeps the two
        // counts separable in the rollup rather than silently summed.
        CoachOpportunityFingerprint.Compute(turnSignal.Value)
            .Should().NotBe(CoachOpportunityFingerprint.Compute(toolSignal.Value));
    }

    /// <summary>
    /// The turn-level fallback code is in the closed set and is not reused anywhere else.
    /// </summary>
    [Fact]
    public void TheTurnToolFailureFallbackIsItsOwnClosedCode()
    {
        CoachOpportunityCapabilityCodes
            .IsKnown(CoachOpportunityCapabilityCodes.TurnToolFailureFallback)
            .Should().BeTrue();

        CoachOpportunityCapabilityCodes.TurnToolFailureFallback
            .Should().NotBe(CoachOpportunityCapabilityCodes.ToolDataAccess);

        // No tool-boundary failure kind may produce it: that would put the unattributed code on a
        // row that does know which tool failed, which is the confusion this split removes.
        foreach (var kind in Enum.GetValues<CoachToolFailureKind>())
        {
            var signal = CoachToolFailureOpportunityMapper.Map(
                kind, CoachToolNames.GetSkillList, "conv-1", "turn-1");

            signal?.CapabilityCode.Should().NotBe(
                CoachOpportunityCapabilityCodes.TurnToolFailureFallback,
                $"the tool boundary knows what happened for {kind}");
        }
    }

    [Fact]
    public void EveryIntentKindHasADeclaredDisposition()
    {
        var undeclared = Enum.GetValues<CoachIntentKind>()
            .Where(intent => CoachTurnOutcomeOpportunityMapper.DispositionFor(intent)
                             == CoachTurnOutcomeOpportunityMapper.TurnDisposition.Unmapped)
            .ToList();

        undeclared.Should().BeEmpty();
    }

    [Fact]
    public void EveryViolationKindHasADeclaredDisposition()
    {
        var undeclared = Enum.GetValues<CoachViolationKind>()
            .Where(violation => CoachTurnOutcomeOpportunityMapper.DispositionFor(violation)
                                == CoachTurnOutcomeOpportunityMapper.TurnDisposition.Unmapped)
            .ToList();

        undeclared.Should().BeEmpty();
    }

    [Fact]
    public void EveryMappedSignalUsesAKnownCapabilityCode()
    {
        foreach (var code in WriteFailureCodes())
        {
            var signal = CoachWriteAuditOpportunityMapper.Map(
                code, CoachToolNames.ProposeVocabularyEntry, "conv-1", "turn-1", "op-1");

            if (signal is { } value)
            {
                CoachOpportunityCapabilityCodes.IsKnown(value.CapabilityCode).Should().BeTrue(
                    $"the write mapper produced '{value.CapabilityCode}' for '{code}', which the " +
                    "recorder would drop");
            }
        }

        foreach (var kind in Enum.GetValues<CoachToolFailureKind>())
        {
            var signal = CoachToolFailureOpportunityMapper.Map(
                kind, CoachToolNames.GetLearnerProfileSummary, "conv-1", "turn-1");

            if (signal is { } value)
            {
                CoachOpportunityCapabilityCodes.IsKnown(value.CapabilityCode).Should().BeTrue();
            }
        }

        foreach (var reason in Enum.GetValues<CoachStopReason>())
        {
            var signal = CoachTurnOutcomeOpportunityMapper.Map(
                null, reason, null, null, "conv-1", "turn-1", null);

            if (signal is { } value)
            {
                CoachOpportunityCapabilityCodes.IsKnown(value.CapabilityCode).Should().BeTrue();
            }
        }
    }

    /// <summary>
    /// A security event must never become an inspectable artifact.
    /// </summary>
    [Fact]
    public void AnUnauthorizedToolFailureIsNeverRecorded()
    {
        CoachToolFailureOpportunityMapper.DispositionFor(CoachToolFailureKind.Unauthorized)
            .Should().Be(CoachToolFailureOpportunityMapper.ToolFailureDisposition.Never);
        CoachToolFailureOpportunityMapper
            .Map(CoachToolFailureKind.Unauthorized, CoachToolNames.GetSkillList, "conv-1", "turn-1")
            .Should().BeNull();
    }

    /// <summary>
    /// An embargo hit is the injection boundary. Recording it would give an attacker who can
    /// place text in a corpus the coach reads a channel into a screen an operator reads.
    /// </summary>
    [Fact]
    public void AnEmbargoViolationIsNeverRecorded()
    {
        CoachTurnOutcomeOpportunityMapper.DispositionFor(CoachViolationKind.Embargo)
            .Should().Be(CoachTurnOutcomeOpportunityMapper.TurnDisposition.Never);

        CoachTurnOutcomeOpportunityMapper.Map(
                null, CoachStopReason.ValidationFailed, null, CoachViolationKind.Embargo,
                "conv-1", "turn-1", null)
            .Should().BeNull("an injection detection must never reach the ledger");
    }

    /// <summary>
    /// The three unresolved-target codes are exactly the shape a cross-tenant probe produces.
    /// </summary>
    [Theory]
    [InlineData(CoachWriteFailureCodes.OperationNotFound)]
    [InlineData(CoachWriteFailureCodes.ConversationMismatch)]
    [InlineData(CoachWriteFailureCodes.NoIdentity)]
    public void AnUnresolvedTargetIsAggregateOnlyAndUnlinked(string code)
    {
        CoachWriteAuditOpportunityMapper.DispositionFor(code)
            .Should().Be(CoachWriteAuditOpportunityMapper.WriteFailureDisposition.AggregateOnlyUnlinked);

        var signal = CoachWriteAuditOpportunityMapper.Map(
            code, CoachToolNames.ProposeVocabularyRemoval, "conv-victim", "turn-1", "op-victim");

        signal.Should().NotBeNull();
        signal!.Value.Disposition.Should().Be(CoachOpportunityDisposition.AggregateOnly);
        signal.Value.Evidence.ConversationId.Should().BeNull(
            "a probe for another learner's operation must not produce an inspectable row naming " +
            "their conversation");
        signal.Value.TurnId.Should().BeNull();
        signal.Value.WriteOperationId.Should().BeNull();
        signal.Value.CapabilityCode.Should()
            .Be(CoachOpportunityCapabilityCodes.ApprovalTargetUnresolved);
    }

    /// <summary>
    /// A destructive request is counted, never linked. Trading forensics for the guarantee that a
    /// refusal never becomes a dossier is the deliberate choice here.
    /// </summary>
    [Theory]
    [InlineData(CoachViolationKind.WriteCommand)]
    [InlineData(CoachViolationKind.BannedClaim)]
    public void ADestructiveRequestIsCountedWithoutPointers(CoachViolationKind violation)
    {
        var signal = CoachTurnOutcomeOpportunityMapper.Map(
            null, CoachStopReason.ValidationFailed, null, violation,
            "conv-1", "turn-1", "op-1");

        signal.Should().NotBeNull();
        signal!.Value.Kind.Should().Be(CoachOpportunityKind.HarmfulOrUnsafeRequest);
        signal.Value.CapabilityCode.Should()
            .Be(CoachOpportunityCapabilityCodes.DestructiveRequestRefused);
        signal.Value.Disposition.Should().Be(CoachOpportunityDisposition.AggregateOnly);
        signal.Value.Evidence.ConversationId.Should().BeNull();
        signal.Value.TurnId.Should().BeNull();
    }

    /// <summary>
    /// The three entries seeded by hand in <c>docs/sam-future-opportunities.md</c> must be
    /// reproducible from live signal. That is the acceptance test for whether the taxonomy is the
    /// right shape at all: a vocabulary that cannot describe the problems a human already wrote
    /// down is the wrong vocabulary.
    /// </summary>
    [Fact]
    public void TheTaxonomyReproducesEveryHandSeededBacklogEntry()
    {
        // Entry 1 — referent lost after a coach offer.
        var entryOne = CoachTurnOutcomeOpportunityMapper.Map(
            new Api.Coach.Opportunities.Detection.CoachReferentLoss(
                CoachOpportunityOfferLink.PriorCoachQuestion,
                new CoachOpportunityEvidencePointer("conv-1", "msg-2", 2, "msg-1", 1)),
            CoachStopReason.ClarificationRequested,
            CoachIntentKind.AskClarification,
            null, "conv-1", "turn-1", null);

        entryOne.Should().NotBeNull();
        entryOne!.Value.CapabilityCode.Should()
            .Be(CoachOpportunityCapabilityCodes.ReferentLostAfterOffer);

        // Entry 2 — a guarded preference change for session_minutes.
        var entryTwo = CoachToolFailureOpportunityMapper.Map(
            CoachToolFailureKind.InvalidArgument,
            CoachToolNames.ProposePreferenceChange,
            "conv-1", "turn-1", "session_minutes");

        entryTwo.Should().NotBeNull();
        entryTwo!.Value.CapabilityCode.Should().Be("preference_setting_session_minutes");
        entryTwo.Value.Kind.Should().Be(CoachOpportunityKind.ProposalRefusedByPolicy);

        // Entry 3 — an entity named by title that the server could not resolve or own.
        var entryThree = CoachWriteAuditOpportunityMapper.Map(
            CoachWriteFailureCodes.EntityNotOwned,
            CoachToolNames.ProposeVocabularyRemoval,
            "conv-1", "turn-1", "op-1");

        entryThree.Should().NotBeNull();
        entryThree!.Value.CapabilityCode.Should()
            .Be(CoachOpportunityCapabilityCodes.EntityLookupByName);
        entryThree.Value.Kind.Should().Be(CoachOpportunityKind.UnsupportedCapability);
    }

    /// <summary>
    /// The per-turn proposal bound is one of the most product-relevant refusals Sam produces, and
    /// it used to be written by a call site that bypassed the shared audit helper entirely.
    /// </summary>
    [Fact]
    public void TheOneProposalPerTurnRefusalIsReviewable()
    {
        var signal = CoachWriteAuditOpportunityMapper.Map(
            CoachWriteFailureCodes.ProposalBudgetExhausted,
            CoachToolNames.ProposeSkillEntry, "conv-1", "turn-1", operationId: string.Empty);

        signal.Should().NotBeNull();
        signal!.Value.Kind.Should().Be(CoachOpportunityKind.CapacityOrBudgetRefusal);
        signal.Value.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.OneProposalPerTurn);
        signal.Value.Disposition.Should().Be(CoachOpportunityDisposition.Product);
    }

    /// <summary>
    /// A completed turn is not an opportunity, and neither is a refusal the learner caused by
    /// cancelling.
    /// </summary>
    [Theory]
    [InlineData(CoachStopReason.Completed)]
    [InlineData(CoachStopReason.Cancelled)]
    [InlineData(CoachStopReason.SessionExpired)]
    [InlineData(CoachStopReason.ConcurrencyLimit)]
    [InlineData(CoachStopReason.InputRejected)]
    [InlineData(CoachStopReason.ClarificationRequested)]
    public void ANormalOutcomeRecordsNothing(CoachStopReason reason) =>
        CoachTurnOutcomeOpportunityMapper
            .Map(null, reason, CoachIntentKind.NoChange, null, "conv-1", "turn-1", null)
            .Should().BeNull();

    /// <summary>
    /// A referent loss outranks everything else the same turn could be classified as.
    /// </summary>
    [Fact]
    public void AReferentLossTakesPrecedenceOverTheStopReason()
    {
        var loss = new Api.Coach.Opportunities.Detection.CoachReferentLoss(
            CoachOpportunityOfferLink.PriorClarification,
            new CoachOpportunityEvidencePointer("conv-1", "msg-2", 2, "msg-1", 1));

        var signal = CoachTurnOutcomeOpportunityMapper.Map(
            loss, CoachStopReason.ValidationFailed, CoachIntentKind.OffTopic,
            CoachViolationKind.AnswerLeak, "conv-1", "turn-1", "op-1");

        signal.Should().NotBeNull();
        signal!.Value.Kind.Should().Be(CoachOpportunityKind.AmbiguousFollowUp);
        signal.Value.Disposition.Should().Be(CoachOpportunityDisposition.Product);
        signal.Value.Evidence.MessageId.Should().Be("msg-2");
        signal.Value.Evidence.OfferMessageId.Should().Be("msg-1");
        signal.Value.ToolName.Should().BeNull("no tool call was made on the referent-loss turn");
    }

    /// <summary>
    /// Every failure code the ledger accepts must come from an existing closed vocabulary, so the
    /// ledger stays joinable to the write audit rather than inventing a parallel one.
    /// </summary>
    [Fact]
    public void TheFailureVocabularyIsTheExistingOne()
    {
        foreach (var code in WriteFailureCodes())
        {
            CoachOpportunityFailureCodes.IsKnown(code).Should().BeTrue(
                $"'{code}' is a write-ledger refusal the opportunity ledger must be able to cite");
        }

        foreach (var kind in Enum.GetValues<CoachToolFailureKind>())
        {
            CoachOpportunityFailureCodes
                .IsKnown(CoachOpportunityFailureCodes.ForToolFailure(kind))
                .Should().BeTrue();
        }

        CoachOpportunityFailureCodes.IsKnown("something a model made up").Should().BeFalse();
    }
}
