using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// Durable, owner-scoped storage for learner memory.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes a <see cref="CoachOwner"/> and every query filters on it. An empty owner is
/// answered with a refusal and a warning, never with an unfiltered query.
/// </para>
/// <para>
/// Nothing here writes an active fact on its own. A fact becomes active only when the learner
/// approves it, and an approval that would collide with an existing active fact supersedes it in
/// one transaction rather than racing it.
/// </para>
/// </remarks>
public interface ICoachMemoryStore
{
    /// <summary>
    /// Records a candidate from an explicit learner statement.
    /// </summary>
    /// <remarks>
    /// The caller must hand in the committed learner message and the exact span the learner used.
    /// The span is verified against the message and then discarded: the store keeps a count and a
    /// pair of dates, never the words.
    /// </remarks>
    Task<CoachMemoryResult> CreateCandidateAsync(
        CoachOwner owner,
        CreateCoachMemoryCandidateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one bounded page of the owner's facts.</summary>
    Task<CoachMemoryPage> ListAsync(
        CoachOwner owner,
        CoachMemoryListFilter filter,
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one fact the owner holds.</summary>
    Task<CoachMemoryResult> GetAsync(CoachOwner owner, string factId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a candidate, optionally replacing its value first, and supersedes any active fact
    /// occupying the same kind and scope.
    /// </summary>
    Task<CoachMemoryResult> ApproveAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CoachMemoryStoredValue? editedValue = null,
        CancellationToken cancellationToken = default);

    /// <summary>Declines a candidate. The row is removed; nothing is remembered.</summary>
    Task<CoachMemoryStatusCode> RejectAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Edits the value of an already-active fact.</summary>
    Task<CoachMemoryResult> EditActiveAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CoachMemoryStoredValue value,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets one fact.</summary>
    Task<CoachMemoryStatusCode> ForgetAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets everything the owner holds.</summary>
    Task<CoachMemoryForgetAllResult> ForgetAllAsync(CoachOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the active, unexpired facts eligible for prompt selection.
    /// </summary>
    /// <remarks>Ordering and capping belong to the selector; this is the raw eligible set.</remarks>
    Task<IReadOnlyList<CoachMemoryFactRecord>> ListEligibleForContextAsync(
        CoachOwner owner,
        CancellationToken cancellationToken = default);

    /// <summary>Stamps the facts the selector actually used.</summary>
    Task<int> MarkUsedAsync(
        CoachOwner owner,
        IReadOnlyCollection<string> factIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every fact whose only provenance is one conversation.
    /// </summary>
    /// <remarks>
    /// Not flag-gated. Deleting a conversation must remove what it produced whether or not the
    /// memory feature is currently switched on.
    /// </remarks>
    Task<int> DeleteForSourceConversationAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every fact for one owner. Not flag-gated; used by account deletion.</summary>
    Task<int> DeleteAllForOwnerAsync(CoachOwner owner, CancellationToken cancellationToken = default);
}
