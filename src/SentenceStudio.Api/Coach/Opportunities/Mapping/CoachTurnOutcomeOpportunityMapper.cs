using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Opportunities.Mapping;

/// <summary>
/// Turns one completed turn's outcome into at most one ledger signal.
/// </summary>
/// <remarks>
/// <para>
/// Exhaustive over <see cref="CoachStopReason"/>, <see cref="CoachIntentKind"/>, and
/// <see cref="CoachViolationKind"/>, with no silently-recording default arm. A new member with no
/// case here falls into <c>Unmapped</c> and
/// <c>CoachOpportunityTriggerMappingTests</c> fails the build.
/// </para>
/// <para>
/// <b>At most one row per turn.</b> A turn can match several rules — a referent loss is also a
/// clarification, an off-topic request also stops with a reason — so
/// <see cref="Map"/> applies a fixed precedence and records the most specific thing that
/// happened. Tool-boundary and write-ledger rows are separate observations and are not affected
/// by this precedence.
/// </para>
/// <para>
/// <b>Injection detections are absent from this file on purpose.</b> A prompt-injection hit is
/// never recorded: an attacker who can put text into a corpus the coach reads could otherwise
/// write rows into a surface an operator reads, which is the feedback loop this design exists to
/// avoid. The embargo scanner's own refusal and counters are unchanged.
/// </para>
/// </remarks>
public static class CoachTurnOutcomeOpportunityMapper
{
    /// <summary>The declared disposition of a turn-boundary trigger.</summary>
    public enum TurnDisposition
    {
        /// <summary>Individually reviewable, with pointers.</summary>
        Product = 0,

        /// <summary>Counted only.</summary>
        AggregateOnly = 1,

        /// <summary>Never recorded.</summary>
        Never = 2,

        /// <summary>No case exists yet. The build fails rather than guessing.</summary>
        Unmapped = 3
    }

    /// <summary>What the ledger does with a given stop reason.</summary>
    public static TurnDisposition DispositionFor(CoachStopReason reason) => reason switch
    {
        // A turn that did what it was asked is not an opportunity.
        CoachStopReason.Completed => TurnDisposition.Never,

        // The learner or the client withdrew, or the checkpoint aged out, or another run held
        // the slot. None of those is a gap in what the coach can do.
        CoachStopReason.Cancelled => TurnDisposition.Never,
        CoachStopReason.SessionExpired => TurnDisposition.Never,
        CoachStopReason.ConcurrencyLimit => TurnDisposition.Never,

        // Input the server refused before any model work. Bounded and already reported to the
        // learner; the rate is not a product signal about capability.
        CoachStopReason.InputRejected => TurnDisposition.Never,

        // A question the coach asked is normal. It only becomes an opportunity when the
        // referent detector says the learner had already answered something — see Map.
        CoachStopReason.ClarificationRequested => TurnDisposition.Never,

        CoachStopReason.ValidationFailed => TurnDisposition.AggregateOnly,

        // Counted under its own code, never as a data-access failure: the turn boundary knows a
        // tool failed and nothing else, while ObservedCoachFunction has already recorded which
        // tool and which failure kind. Two codes rather than one keeps the counts comparable
        // instead of double-counting one event under a cause nobody established.
        CoachStopReason.ToolFailure => TurnDisposition.AggregateOnly,

        CoachStopReason.IterationLimit => TurnDisposition.AggregateOnly,
        CoachStopReason.OutputTokenLimit => TurnDisposition.AggregateOnly,
        CoachStopReason.Timeout => TurnDisposition.AggregateOnly,
        CoachStopReason.RateLimit => TurnDisposition.AggregateOnly,

        // An unexplained failure is an operational signal worth counting, never a dossier.
        CoachStopReason.Failed => TurnDisposition.AggregateOnly,

        _ => TurnDisposition.Unmapped
    };

    /// <summary>What the ledger does with a given intent kind.</summary>
    public static TurnDisposition DispositionFor(CoachIntentKind intent) => intent switch
    {
        CoachIntentKind.NoChange => TurnDisposition.Never,
        CoachIntentKind.DirectConstraintChange => TurnDisposition.Never,
        CoachIntentKind.SuggestConstraintChange => TurnDisposition.Never,
        CoachIntentKind.AcceptPendingSuggestion => TurnDisposition.Never,
        CoachIntentKind.RejectPendingSuggestion => TurnDisposition.Never,
        CoachIntentKind.AskClarification => TurnDisposition.Never,
        CoachIntentKind.PedagogicalAnswer => TurnDisposition.Never,

        // The one intent that names a gap: the learner asked about something the coach does not
        // cover. Counted with no conversation id, because "what did this learner ask that we
        // refused" is a dossier and the count is the whole signal.
        CoachIntentKind.OffTopic => TurnDisposition.AggregateOnly,

        _ => TurnDisposition.Unmapped
    };

    /// <summary>What the ledger does with a given validation violation kind.</summary>
    public static TurnDisposition DispositionFor(CoachViolationKind violation) => violation switch
    {
        // Embargo is the injection and identity-leak boundary. Never recorded: an
        // attacker-controlled corpus must not be able to write into an operator's screen.
        CoachViolationKind.Embargo => TurnDisposition.Never,

        // The rate is the signal, not the instance. Sam declining to narrate an account read is
        // a known behaviour; how often it happens is what tells us whether it is a problem.
        CoachViolationKind.AnswerLeak => TurnDisposition.AggregateOnly,

        // "Delete all my vocabulary" and friends. Counted with no conversation id and no
        // pointers, so a refusal never becomes an inspectable record of what somebody asked for.
        CoachViolationKind.WriteCommand => TurnDisposition.AggregateOnly,
        CoachViolationKind.BannedClaim => TurnDisposition.AggregateOnly,

        // Shape faults in the model's own answer.
        CoachViolationKind.IntentShape => TurnDisposition.AggregateOnly,
        CoachViolationKind.LengthLimit => TurnDisposition.AggregateOnly,
        CoachViolationKind.EvidenceWindow => TurnDisposition.AggregateOnly,

        // A preview naming a resource the learner does not own is an ownership failure. Counted,
        // never linked: the identifiers involved are exactly what must not become inspectable.
        CoachViolationKind.Ownership => TurnDisposition.AggregateOnly,

        // A deployment defect, not a learner gap: a tool reached the model that the allow-list
        // does not permit.
        CoachViolationKind.ToolAllowList => TurnDisposition.AggregateOnly,

        _ => TurnDisposition.Unmapped
    };

    /// <summary>
    /// Maps one turn outcome to at most one signal.
    /// </summary>
    /// <param name="referentLoss">
    /// The referent-loss observation, when <c>CoachUnboundAnswerDetector</c> found one. Highest
    /// precedence: it is the most specific statement available about what went wrong.
    /// </param>
    /// <param name="stopReason">The turn's stop reason.</param>
    /// <param name="intent">The model's declared intent, when the turn produced one.</param>
    /// <param name="violation">The validation violation that refused the turn, when any.</param>
    /// <param name="conversationId">The conversation, used only for Product rows.</param>
    /// <param name="turnId">The turn identity, used only for Product rows.</param>
    /// <param name="turnOperationId">The durable turn operation, used only for Product rows.</param>
    /// <param name="modelOutputUnreadable">
    /// True when the model answered but the answer did not deserialize into a turn intent.
    /// Distinguished from an ordinary validation failure because the fix is different.
    /// </param>
    public static CoachOpportunitySignal? Map(
        CoachReferentLoss? referentLoss,
        CoachStopReason stopReason,
        CoachIntentKind? intent,
        CoachViolationKind? violation,
        string? conversationId,
        string? turnId,
        string? turnOperationId,
        bool modelOutputUnreadable = false,
        bool answerShapeRefused = false)
    {
        // --- precedence 1: the referent loss ------------------------------------------------
        //
        // Deliberately first. Every other rule below describes what the server did; this one
        // describes what the learner experienced, and when both are true the learner's
        // experience is the product signal worth reviewing.
        if (referentLoss is { } loss)
        {
            return new CoachOpportunitySignal(
                CoachOpportunityKind.AmbiguousFollowUp,
                CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
                CoachOpportunitySurface.TurnOutcome,
                CoachOpportunityDisposition.Product,
                OfferLink: loss.OfferLink,
                ToolName: null,
                FailureCode: null,
                StopReason: stopReason,
                Evidence: loss.Evidence,
                TurnId: turnId,
                TurnOperationId: turnOperationId);
        }

        // --- precedence 2: a validation violation names the rule that refused the turn -------
        if (violation is { } kind)
        {
            var violationDisposition = DispositionFor(kind);

            if (violationDisposition == TurnDisposition.Never)
            {
                // The whole turn records nothing, not just this rule. The violation is the
                // authoritative explanation of why the turn was refused, so falling through to
                // the stop reason would record the very same event under a different name — and
                // for the one violation that is Never (Embargo, the injection and identity-leak
                // boundary) that would hand an attacker who can place text in a corpus the coach
                // reads a channel into a screen an operator reads. That is exactly the feedback
                // loop this design exists to avoid.
                return null;
            }

            if (violationDisposition == TurnDisposition.AggregateOnly)
            {
                var (violationKind, violationCapability) = Classify(kind, answerShapeRefused);
                return new CoachOpportunitySignal(
                    violationKind,
                    violationCapability,
                    CoachOpportunitySurface.TurnOutcome,
                    CoachOpportunityDisposition.AggregateOnly,
                    StopReason: stopReason);
            }
        }

        // --- precedence 3: an out-of-scope request ------------------------------------------
        if (intent is { } intentKind && DispositionFor(intentKind) == TurnDisposition.AggregateOnly)
        {
            return new CoachOpportunitySignal(
                CoachOpportunityKind.OutOfScopeRequest,
                CoachOpportunityCapabilityCodes.OffTopic,
                CoachOpportunitySurface.TurnOutcome,
                CoachOpportunityDisposition.AggregateOnly,
                StopReason: stopReason);
        }

        // --- precedence 4: the stop reason --------------------------------------------------
        if (DispositionFor(stopReason) != TurnDisposition.AggregateOnly)
        {
            return null;
        }

        var (stopKind, stopCapability) = Classify(stopReason, modelOutputUnreadable);
        return new CoachOpportunitySignal(
            stopKind,
            stopCapability,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly,
            StopReason: stopReason);
    }

    private static (CoachOpportunityKind Kind, string Capability) Classify(
        CoachViolationKind violation, bool answerShapeRefused) =>
        violation switch
        {
            CoachViolationKind.AnswerLeak =>
                (CoachOpportunityKind.ValidationFailure,
                 CoachOpportunityCapabilityCodes.AnswerLeakRefusal),

            CoachViolationKind.WriteCommand or CoachViolationKind.BannedClaim =>
                (CoachOpportunityKind.HarmfulOrUnsafeRequest,
                 CoachOpportunityCapabilityCodes.DestructiveRequestRefused),

            CoachViolationKind.ToolAllowList =>
                (CoachOpportunityKind.ValidationFailure,
                 CoachOpportunityCapabilityCodes.ToolAllowListViolation),

            CoachViolationKind.Ownership =>
                (CoachOpportunityKind.UnsupportedCapability,
                 CoachOpportunityCapabilityCodes.EntityLookupByName),

            // IntentShape, LengthLimit, EvidenceWindow: the model's answer did not hold together.
            // When the shape-projection path is the source, emit a distinct code so operators can
            // slice production telemetry between intent validation and post-projection failures.
            // Within IntentShape, further distinguish "answer_required" and "evidence_reference_invalid"
            // from generic shape failures, using the first violation's Code field.
            CoachViolationKind.IntentShape when !answerShapeRefused =>
                (CoachOpportunityKind.ValidationFailure,
                 CoachOpportunityCapabilityCodes.IntentShapeInvalid),

            CoachViolationKind.EvidenceWindow =>
                (CoachOpportunityKind.ValidationFailure,
                 CoachOpportunityCapabilityCodes.EvidenceReferenceInvalid),

            _ => answerShapeRefused
                ? (CoachOpportunityKind.ValidationFailure,
                   CoachOpportunityCapabilityCodes.AnswerShapeInvalid)
                : (CoachOpportunityKind.ValidationFailure,
                   CoachOpportunityCapabilityCodes.IntentShapeInvalid)
        };

    private static (CoachOpportunityKind Kind, string Capability) Classify(
        CoachStopReason reason,
        bool modelOutputUnreadable) => reason switch
    {
        CoachStopReason.ValidationFailed when modelOutputUnreadable =>
            (CoachOpportunityKind.ValidationFailure,
             CoachOpportunityCapabilityCodes.ModelOutputUnreadable),

        CoachStopReason.ValidationFailed =>
            (CoachOpportunityKind.ValidationFailure,
             CoachOpportunityCapabilityCodes.IntentShapeInvalid),

        CoachStopReason.ToolFailure =>
            (CoachOpportunityKind.ToolExecutionFailure,
             CoachOpportunityCapabilityCodes.TurnToolFailureFallback),

        CoachStopReason.IterationLimit =>
            (CoachOpportunityKind.CapacityOrBudgetRefusal,
             CoachOpportunityCapabilityCodes.IterationLimit),

        CoachStopReason.OutputTokenLimit =>
            (CoachOpportunityKind.CapacityOrBudgetRefusal,
             CoachOpportunityCapabilityCodes.OutputTokenLimit),

        CoachStopReason.Timeout =>
            (CoachOpportunityKind.CapacityOrBudgetRefusal,
             CoachOpportunityCapabilityCodes.TurnTimeout),

        CoachStopReason.RateLimit =>
            (CoachOpportunityKind.CapacityOrBudgetRefusal,
             CoachOpportunityCapabilityCodes.DailyRunLimit),

        // Failed.
        _ =>
            (CoachOpportunityKind.ValidationFailure,
             CoachOpportunityCapabilityCodes.ModelOutputUnreadable)
    };
}
