namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Something that wants to know a tool call happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract: never throws.</b> An observer exists to watch a tool call, and a watcher that can
/// break the thing it watches is worse than no watcher — a bounded, actionable tool refusal would
/// be replaced by an unrelated failure the model then reports to the learner. The seam guards each
/// observer independently anyway, because this contract is an interface anyone can implement and
/// the cost of trusting it is paid by the learner.
/// </para>
/// <para>
/// <b>Contract: fast, or asynchronous.</b> Observers run on the tool call's own path, after the
/// inner delegate has returned. The elapsed time on the observation is measured around the delegate
/// only, so a slow observer cannot make a tool look slow — but it can still make the turn slow.
/// Work that is not trivially cheap belongs behind a queue the observer owns.
/// </para>
/// <para>
/// Subscriber order is the registration order the seam is handed. It is an explicit list rather
/// than a set for that reason: "the opportunity ledger sees it first" is a property somebody may
/// one day depend on, and an implicit order is one nobody can test.
/// </para>
/// </remarks>
public interface ICoachToolCallObserver
{
    /// <summary>
    /// Called once per completed tool call, whatever the outcome. Must not throw.
    /// </summary>
    ValueTask OnCompletedAsync(
        CoachToolCallObservation observation,
        CancellationToken cancellationToken);
}
