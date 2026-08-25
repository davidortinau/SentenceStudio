using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// Owned access to coach session state and the normalized revision audit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership contract.</b> Every method takes <c>userProfileId</c> as its first
/// parameter. The caller must supply the trusted value from
/// <c>IUserScopeProvider.UserProfileId</c> — never a client-supplied id. Every query in
/// the implementation filters on that value, and there is no unfiltered helper anywhere
/// in this type. A session owned by another learner is indistinguishable from a session
/// that does not exist: both return <see cref="CoachSessionLoadStatus.NotFound"/> / false /
/// an empty list, so endpoints answer 404 without leaking existence.
/// </para>
/// <para>
/// <b>Empty user id.</b> An empty or whitespace user id is a bug in the caller, not an
/// invitation to read everything. Every method logs a warning and returns the "none"
/// result. It never throws (that would 500 a Blazor circuit) and never falls through to
/// an unfiltered query.
/// </para>
/// <para>
/// Revision append and read live here rather than in a separate store: a revision is only
/// ever created inside an owned session write, so splitting the two would duplicate the
/// same ownership check without adding clarity. Usage counters, which are session-independent,
/// do live in their own <see cref="ICoachUsageStore"/>.
/// </para>
/// </remarks>
public interface ICoachSessionStore
{
    /// <summary>Creates a session owned by the caller.</summary>
    Task<CoachSession> CreateAsync(string userProfileId, CreateCoachSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads one owned session. Rejects unowned, expired, version-mismatched, and
    /// undecryptable sessions. A successful load slides the expiry forward.
    /// </summary>
    Task<CoachSessionLoadResult> LoadAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the caller's most recent resumable session, if one exists. Same rejection
    /// rules as <see cref="LoadAsync"/>.
    /// </summary>
    Task<CoachSessionLoadResult> LoadResumableAsync(string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a partial update to an owned session and slides the expiry forward.
    /// Returns false when the session is not owned, expired, or version-mismatched.
    /// </summary>
    Task<bool> UpdateAsync(string userProfileId, string sessionId, CoachSessionUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes an owned session, which removes the encrypted conversation state and
    /// any pending suggestion. Idempotent: a second call returns false without throwing.
    /// The revision audit is intentionally retained.
    /// </summary>
    Task<bool> DeleteAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the serialized agent checkpoint from every live session the owner holds, so the next
    /// turn on each rebuilds its agent session from the durable ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists for one reason: a checkpoint is an opaque serialized conversation, and anything
    /// that was in the prompt when it was written is still inside it. When a learner forgets or
    /// edits a saved preference, clearing the row from the memory store is not enough — the value
    /// would keep speaking through every resumed checkpoint until it expired on its own, up to
    /// twenty-four hours later. Forgetting has to reach the checkpoint or it is not forgetting.
    /// </para>
    /// <para>
    /// Only the checkpoint is cleared. The conversation ledger, the plan revision audit, the
    /// pending suggestion, the constraints, and the session's own lifecycle are all untouched, so
    /// the learner loses no history and no in-flight decision — the next turn simply rebuilds the
    /// agent session from committed messages, this time without the forgotten value.
    /// </para>
    /// </remarks>
    /// <returns>How many sessions were rotated.</returns>
    Task<int> ClearAgentCheckpointsAsync(string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>Records the pending suggestion awaiting a clear acceptance or rejection.</summary>
    Task<bool> SetPendingSuggestionAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        CoachConstraintDeltaDto delta,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the pending suggestion for an owned session. Returns null when none is
    /// pending, when the id does not match, or when the session is not usable.
    /// </summary>
    /// <summary>
    /// Stores a pending offer as an application-owned payload, written verbatim. The application
    /// owns the shape so server-only state — the frozen vocabulary focus selection — can travel
    /// with the delta in the existing JSON column.
    /// </summary>
    Task<bool> SetPendingSuggestionPayloadAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        string payloadJson,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the stored pending payload verbatim, or null when nothing matches.</summary>
    Task<string?> GetPendingSuggestionPayloadAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        CancellationToken cancellationToken = default);

    Task<CoachConstraintDeltaDto?> GetPendingSuggestionAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        CancellationToken cancellationToken = default);

    /// <summary>Clears any pending suggestion. Idempotent.</summary>
    Task<bool> ClearPendingSuggestionAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends the next revision to an owned session. The revision number is assigned by
    /// the store, so callers cannot create gaps or duplicates.
    /// </summary>
    Task<CoachPlanRevision?> AppendRevisionAsync(
        string userProfileId,
        string sessionId,
        CoachPlanRevisionInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an owned session's revisions in ascending revision-number order.</summary>
    /// <summary>
    /// Reads the revision one durable turn operation produced, if it produced one.
    /// </summary>
    /// <remarks>
    /// This is the recovery primitive that replaced a time-window search. After a crash between
    /// the plan commit and the audit append, the only correct question is "did <em>this</em>
    /// operation already change the plan", and the only correct answer comes from a key the
    /// operation itself wrote. Scanning for revisions created since the operation started
    /// answered a different question, and answered it wrongly whenever two conversations were
    /// revising the same plan at once.
    /// </remarks>
    Task<CoachPlanRevision?> GetRevisionByOperationAsync(
        string userProfileId,
        string operationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CoachPlanRevision>> GetRevisionsAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Reads an owned session's most recent revision, or null when there is none.</summary>
    Task<CoachPlanRevision?> GetLatestRevisionAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Marks an owned revision as undone by a later revision. Idempotent.</summary>
    Task<bool> MarkRevisionUndoneAsync(
        string userProfileId,
        string revisionId,
        string undoneByRevisionId,
        CancellationToken cancellationToken = default);
}
