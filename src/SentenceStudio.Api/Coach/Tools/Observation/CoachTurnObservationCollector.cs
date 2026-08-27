namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// The subscriber that puts a completed call into the turn's buffer.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the whole of it. The buffer is the shared capture W3's evidence projection and
/// W4's trace summary both read; neither projection belongs here, because a projection that ran
/// inside the tool call would put its cost on the learner's latency and its failures on the tool's
/// result.
/// </para>
/// <para>
/// <b>No persistence.</b> This writes to an in-memory, request-scoped list and nothing else. The
/// trace section on the protected outcome is W4b's, and it is written once at the turn boundary
/// from what this collected.
/// </para>
/// <para>
/// Never throws: the seam guards each observer anyway, and a buffer append that failed would
/// otherwise cost a learner the answer to their question.
/// </para>
/// </remarks>
public sealed class CoachTurnObservationCollector : ICoachToolCallObserver
{
    private readonly ICoachTurnObservationSink _sink;

    public CoachTurnObservationCollector(ICoachTurnObservationSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <inheritdoc />
    public ValueTask OnCompletedAsync(
        CoachToolCallObservation observation,
        CancellationToken cancellationToken)
    {
        // Every outcome, not only the successful ones. "The model called this tool four times and
        // three of them were refused" is the shape of turn a trace exists to explain, and a buffer
        // that kept only the successes would describe a turn that did not happen.
        _sink.Add(observation);
        return ValueTask.CompletedTask;
    }
}
