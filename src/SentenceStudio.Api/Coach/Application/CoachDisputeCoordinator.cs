using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Opens, carries and closes a learner's correction across turns.
/// </summary>
/// <remarks>
/// <para>
/// <b>A correction is a state, not an event.</b> Plan principle 6: Sam cannot repeat a disputed
/// claim from the same read. That is only enforceable if the dispute survives the turn it opened
/// in — an event fires and is gone, and the next answer would have nothing to be judged against.
/// So it is written into the protected turn outcome and read back on the following turn, which is
/// also what makes it survive a reload and a session resume for free.
/// </para>
/// <para>
/// <b>Nothing here holds learner text.</b> The classifier reads the correction and returns a code;
/// the code is what is stored. The learner's sentence stays in the encrypted message ledger, where
/// account erasure and retention already reach it, and it is never copied into the outcome.
/// </para>
/// <para>
/// <b>The flag is checked once, here.</b> <c>Coach:CorrectionState:Enabled=false</c> means
/// <see cref="TryOpen"/> returns null, so no dispute exists, so the rule that reads one never
/// fires. One gate rather than a gate per component, because two gates are two things that can
/// disagree.
/// </para>
/// </remarks>
public sealed class CoachDisputeCoordinator
{
    private readonly CoachCorrectionClassifier _classifier;
    private readonly IOptionsMonitor<CoachOptions> _options;

    public CoachDisputeCoordinator(
        CoachCorrectionClassifier classifier,
        IOptionsMonitor<CoachOptions> options)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>True when correction state is switched on for this deployment.</summary>
    public bool IsEnabled => _options.CurrentValue.IsCorrectionStateEnabled;

    /// <summary>
    /// Opens a dispute when the learner's text corrects the previous turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns null for the overwhelming majority of turns, which is the point: a dispute
    /// constrains the next answer, so opening one on an ordinary question would degrade a turn for
    /// a learner who was merely curious.
    /// </para>
    /// <para>
    /// The definition codes come from the disputed turn's own trace and are what
    /// <see cref="CoachRepeatedDisputedClaimRule"/> compares the next turn against. Storing them
    /// with the dispute rather than re-deriving them later means the comparison survives a reload:
    /// the next turn does not need the previous turn's trace in memory to know what was already
    /// asked.
    /// </para>
    /// </remarks>
    /// <param name="learnerText">What the learner typed.</param>
    /// <param name="disputedMessageId">The ledger identifier of the coach message being corrected.</param>
    /// <param name="disputedTrace">The disputed turn's trace, for the definitions it read.</param>
    /// <param name="nowUtc">The clock.</param>
    public CoachTurnDisputeState? TryOpen(
        string? learnerText,
        string disputedMessageId,
        CoachTurnTraceSummary? disputedTrace,
        DateTime nowUtc)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(disputedMessageId)
            || disputedMessageId.Length > CoachTurnDisputeState.MaxDisputedMessageIdLength)
        {
            // A missing or oversized identifier means the caller cannot say what is being disputed.
            // An unanchored dispute would constrain the next answer about nothing in particular.
            return null;
        }

        var signal = _classifier.Classify(learnerText);

        if (signal == CoachCorrectionSignal.None)
        {
            return null;
        }

        return new CoachTurnDisputeState(
            signal,
            disputedMessageId,
            NormalizeInstant(nowUtc),
            ResolvedAtUtc: null,
            CoachDisputeResolution.Open,
            DefinitionsFrom(disputedTrace));
    }

    /// <summary>
    /// Closes a dispute the next answer satisfied, or leaves it open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution is derived from what the answer <em>did</em>, judged by the same rule that would
    /// otherwise refuse it. That symmetry is deliberate: if the rule finds nothing to complain
    /// about, the dispute is satisfied, and there is no second definition of "good enough" that
    /// could drift from the first.
    /// </para>
    /// <para>
    /// A re-read is preferred over a correction when both are present, because it is the stronger
    /// resolution — the coach looked somewhere new rather than only apologising for where it looked
    /// before.
    /// </para>
    /// </remarks>
    public CoachTurnDisputeState Resolve(
        CoachTurnDisputeState dispute,
        CoachClaimRuleContext nextTurn,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(dispute);
        ArgumentNullException.ThrowIfNull(nextTurn);

        if (!dispute.IsOpen)
        {
            return dispute;
        }

        var exit = CoachRepeatedDisputedClaimRule.ClassifyExit(nextTurn.WithDispute(dispute));

        if (exit == CoachDisputeExit.None)
        {
            return dispute;
        }

        return dispute with
        {
            Resolution = ToResolution(exit),
            ResolvedAtUtc = NormalizeInstant(nowUtc)
        };
    }

    /// <summary>The learner closed it themselves.</summary>
    /// <remarks>
    /// Permitted because a learner who moved on should not be held to a constraint they no longer
    /// care about. It is recorded as its own resolution rather than as a re-read, so a metric can
    /// tell a dispute the coach satisfied from one the learner gave up on — those two numbers mean
    /// opposite things about the product.
    /// </remarks>
    public CoachTurnDisputeState Dismiss(CoachTurnDisputeState dispute, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(dispute);

        return dispute.IsOpen
            ? dispute with
            {
                Resolution = CoachDisputeResolution.DismissedByLearner,
                ResolvedAtUtc = NormalizeInstant(nowUtc)
            }
            : dispute;
    }

    /// <summary>
    /// The stored resolution for an exit the rule recognised.
    /// </summary>
    /// <remarks>
    /// A total map with one arm per member. <see cref="CoachDisputeExit.None"/> never reaches here
    /// — the caller returns before it can — and it throws rather than defaulting, because silently
    /// recording an unresolved dispute as resolved would release the constraint the learner's
    /// correction earned.
    /// </remarks>
    private static CoachDisputeResolution ToResolution(CoachDisputeExit exit) => exit switch
    {
        CoachDisputeExit.ReRead => CoachDisputeResolution.ResolvedByReRead,
        CoachDisputeExit.NamedCorrection => CoachDisputeResolution.ResolvedByCorrection,
        CoachDisputeExit.Limitation => CoachDisputeResolution.ResolvedByLimitation,
        _ => throw new ArgumentOutOfRangeException(
            nameof(exit), exit, "An unresolved dispute must not be recorded as resolved.")
    };

    /// <summary>The read definitions a turn actually used. Closed codes, never arguments.</summary>
    private static IReadOnlyList<CoachScopeDefinition> DefinitionsFrom(CoachTurnTraceSummary? trace) =>
        trace is null
            ? []
            : [.. trace.Calls
                .Where(call => call.Outcome == Tools.Observation.CoachToolCallOutcome.Succeeded)
                .Select(call => call.DefinitionCode)
                .Where(definition => definition != CoachScopeDefinition.Unspecified)
                .Distinct()];

    /// <summary>
    /// Whole-second UTC, matching every other coach timestamp.
    /// </summary>
    /// <remarks>
    /// Truncated rather than rounded, for the reason <c>CoachResultScope</c> gives: rounding up
    /// places an instant in the future, and a dispute recorded as opening after the answer it
    /// disputes reads as impossible.
    /// </remarks>
    private static DateTime NormalizeInstant(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}

/// <summary>
/// Maps the stored dispute onto the wire shape the learner's client renders.
/// </summary>
/// <remarks>
/// A total mapper with an explicit arm per member rather than a cast. The two vocabularies are
/// mirrors and a cast would silently survive them diverging — which is the exact failure the
/// mirroring comment in Contracts warns about.
/// </remarks>
public static class CoachDisputeProjection
{
    /// <summary>Projects a stored dispute, or null when there is none.</summary>
    public static CoachDisputeDto? Project(CoachTurnDisputeState? dispute) =>
        dispute is null
            ? null
            : new CoachDisputeDto
            {
                Signal = ToWire(dispute.Signal),
                Status = ToWire(dispute.Resolution),
                DisputedMessageId = dispute.DisputedMessageId
            };

    /// <summary>The wire signal for a server signal.</summary>
    public static CoachDisputeSignal ToWire(CoachCorrectionSignal signal) => signal switch
    {
        CoachCorrectionSignal.MeantSomethingElse => CoachDisputeSignal.MeantSomethingElse,
        CoachCorrectionSignal.NotWhatIAsked => CoachDisputeSignal.NotWhatIAsked,
        CoachCorrectionSignal.WrongClaim => CoachDisputeSignal.WrongClaim,
        CoachCorrectionSignal.DifferentCohort => CoachDisputeSignal.DifferentCohort,
        _ => CoachDisputeSignal.Unknown
    };

    /// <summary>The wire status for a server resolution.</summary>
    public static CoachDisputeStatus ToWire(CoachDisputeResolution resolution) => resolution switch
    {
        CoachDisputeResolution.Open => CoachDisputeStatus.Open,
        CoachDisputeResolution.ResolvedByReRead => CoachDisputeStatus.ResolvedByReRead,
        CoachDisputeResolution.ResolvedByCorrection => CoachDisputeStatus.ResolvedByCorrection,
        CoachDisputeResolution.ResolvedByLimitation => CoachDisputeStatus.ResolvedByLimitation,
        CoachDisputeResolution.DismissedByLearner => CoachDisputeStatus.DismissedByLearner,
        _ => CoachDisputeStatus.Unknown
    };
}
