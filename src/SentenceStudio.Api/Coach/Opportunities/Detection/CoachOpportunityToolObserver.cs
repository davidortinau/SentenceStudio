using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Opportunities.Mapping;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;

namespace SentenceStudio.Api.Coach.Opportunities.Detection;

/// <summary>
/// Subscriber 1 on the tool-call seam: a bounded refusal reaches the opportunity ledger.
/// </summary>
/// <remarks>
/// <para>
/// This is what <c>ObservedCoachFunction</c> used to be. It was a wrapper that both intercepted the
/// call and recorded the signal; it is now only the recording half, and the interception lives once
/// in <see cref="CoachObservedFunction"/>. The behaviour it produces is unchanged — the same
/// mapper, the same closed capability code, the same "never break the tool call" guarantee — but a
/// second consumer of the same calls no longer needs a second wrapper, a second edit to
/// <c>CoachToolFactory</c>, or an implicit ordering between two sibling interceptors.
/// </para>
/// <para>
/// <b>Only refusals are recorded.</b> A successful read is not an opportunity, and a fault is not
/// one either: the mapper is keyed on <see cref="CoachToolFailureKind"/>, which only a bounded
/// <c>CoachToolException</c> carries. An untyped fault has no kind to map and is deliberately
/// dropped here rather than bucketed into some catch-all, because a catch-all bucket in an
/// opportunity ledger is a number nobody can act on.
/// </para>
/// <para>
/// <b>Budget refusals never arrive.</b> They are raised by the outer budget wrapper before this
/// seam runs, and are counted once at the turn boundary. That is why
/// <see cref="CoachToolFailureKind.BudgetExhausted"/> needs no special case here.
/// </para>
/// <para>
/// <b>Never throws.</b> The seam guards each observer independently, and this one also swallows its
/// own failures so a recorder outage cannot turn a bounded, actionable tool refusal into a failed
/// turn the learner reads about.
/// </para>
/// </remarks>
public sealed class CoachOpportunityToolObserver : ICoachToolCallObserver
{
    private readonly ICoachOpportunityRecorder _recorder;
    private readonly CoachWriteTurnScope? _turnScope;

    public CoachOpportunityToolObserver(
        ICoachOpportunityRecorder recorder,
        CoachWriteTurnScope? turnScope = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _turnScope = turnScope;
    }

    /// <inheritdoc />
    public async ValueTask OnCompletedAsync(
        CoachToolCallObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Outcome != CoachToolCallOutcome.Refused
            || observation.FailureKind is not { } kind)
        {
            return;
        }

        try
        {
            var signal = CoachToolFailureOpportunityMapper.Map(
                kind,
                observation.ToolName,
                _turnScope?.ConversationId,
                _turnScope?.TurnId,
                observation.SubjectCode?.Value);

            if (signal is { } value)
            {
                await _recorder.RecordAsync(value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception recordingFailure)
        {
            // Every exception, including OperationCanceledException. The recorder never throws by
            // contract, but the contract is an interface anyone can implement, and an escape here
            // would replace a bounded, actionable tool refusal with an unrelated failure the model
            // then reports to the learner.
            //
            // Discarded rather than logged — the recorder owns its own content-free failure
            // logging, and passing an exception object to a logger is forbidden on coach paths.
            _ = recordingFailure;
        }
    }
}
