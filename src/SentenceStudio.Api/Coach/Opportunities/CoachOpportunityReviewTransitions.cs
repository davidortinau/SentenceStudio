namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// Which review transitions a reviewer may make, and which ones the ledger refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because status is not only a label — it decides retention.</b>
/// <see cref="CoachOpportunityRetentionSweep"/> ages out
/// <see cref="CoachOpportunityStatus.New"/> and <see cref="CoachOpportunityStatus.Dismissed"/>
/// rows and preserves the rest, so any transition that walks a decided row back into a
/// retention-eligible status is a delete with extra steps: the row silently disappears at the
/// next sweep and the decision it recorded goes with it. Without a policy, a single mistyped
/// review body could do that, and nothing would report it.
/// </para>
/// <para>
/// <b>The rule is monotonic in one direction only: nothing leaves
/// <see cref="CoachOpportunityStatus.Accepted"/>.</b> Accepted means "this is real product work
/// and something downstream — a spec, a backlog entry, a branch — now points at this row". That
/// claim cannot be un-made by an edit here, because the artifacts pointing at it do not go away
/// when the row's status changes. Accepted is therefore terminal, and a row accepted in error is
/// corrected where the error was made, not by rewinding the ledger.
/// </para>
/// <para>
/// <b>Reopening is explicitly allowed in the two cases where nothing has been claimed yet.</b>
/// <see cref="CoachOpportunityStatus.Deferred"/> means "real, but not now" — the whole point is
/// that it comes back, so it may move to any status including
/// <see cref="CoachOpportunityStatus.New"/>. <see cref="CoachOpportunityStatus.Dismissed"/> means
/// "not worth carrying", and a dismissed problem that keeps recurring is precisely the case a
/// reviewer must be able to reconsider, so it may also move anywhere. Both are already
/// retention-eligible or freely re-decidable, so allowing them costs nothing and forbidding them
/// would make a wrong dismissal permanent.
/// </para>
/// <para>
/// Same-status writes are always allowed: a reviewer refining a note code or attaching a spec
/// path on an already-accepted row is an ordinary edit, not a transition.
/// </para>
/// </remarks>
public static class CoachOpportunityReviewTransitions
{
    /// <summary>
    /// The statuses <see cref="CoachOpportunityRetentionSweep"/> will eventually delete.
    /// </summary>
    /// <remarks>
    /// Declared here as well as used there so the transition rule and the retention rule are
    /// stated once each in terms of the same set, and
    /// <c>CoachOpportunityLifecycleTests</c> asserts they agree. A future status added to the
    /// sweep but not to this set would silently re-open the walk-back it exists to prevent.
    /// </remarks>
    public static IReadOnlyList<CoachOpportunityStatus> RetentionEligible { get; } =
    [
        CoachOpportunityStatus.New,
        CoachOpportunityStatus.Dismissed
    ];

    /// <summary>
    /// The statuses a retention pass must never delete, because each records a decision.
    /// </summary>
    public static IReadOnlyList<CoachOpportunityStatus> Retained { get; } =
    [
        CoachOpportunityStatus.Reviewed,
        CoachOpportunityStatus.Accepted,
        CoachOpportunityStatus.Deferred
    ];

    /// <summary>True when <paramref name="status"/> is one the sweep may age out.</summary>
    public static bool IsRetentionEligible(CoachOpportunityStatus status) =>
        status is CoachOpportunityStatus.New or CoachOpportunityStatus.Dismissed;

    /// <summary>
    /// True when a reviewer may move a row from <paramref name="current"/> to
    /// <paramref name="requested"/>.
    /// </summary>
    /// <remarks>
    /// Total over the enum by construction: every pair is either the same status, a move out of
    /// <see cref="CoachOpportunityStatus.Accepted"/>, or allowed. A new status member is
    /// therefore allowed by default rather than silently rejected, which is the safer failure —
    /// and <c>CoachOpportunityLifecycleTests</c> enumerates the full matrix so a member that
    /// needs its own rule is caught when it is added.
    /// </remarks>
    public static bool IsAllowed(CoachOpportunityStatus current, CoachOpportunityStatus requested)
    {
        if (current == requested)
        {
            // Re-recording the same decision with a different note code or spec path.
            return true;
        }

        // Accepted is terminal. Anything downstream that points at this row keeps pointing at it,
        // and moving it back to New, Reviewed, or Dismissed would either hide it from triage or
        // hand it to the retention sweep.
        return current != CoachOpportunityStatus.Accepted;
    }

    /// <summary>
    /// True when the refused transition would have made a decided row retention-eligible.
    /// </summary>
    /// <remarks>
    /// Used only to choose the log message, so an operator reading the log can tell "you tried to
    /// re-open an accepted row" apart from "you tried to re-label an accepted row" without either
    /// message naming a learner or a conversation.
    /// </remarks>
    public static bool WouldRestoreRetentionEligibility(
        CoachOpportunityStatus current,
        CoachOpportunityStatus requested) =>
        !IsAllowed(current, requested) && IsRetentionEligible(requested);
}
