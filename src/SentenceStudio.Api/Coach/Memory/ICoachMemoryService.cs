using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// The request-scoped surface the API layer calls.
/// </summary>
/// <remarks>
/// The owner is resolved from the request scope here and nowhere else. No method accepts an owner
/// from a caller, so a request body can never name whose memory it is reading.
/// </remarks>
public interface ICoachMemoryService
{
    /// <summary>True when the memory feature is switched on.</summary>
    bool IsEnabled { get; }

    /// <summary>Lists the current learner's facts.</summary>
    Task<(CoachMemoryStatusCode Status, CoachMemoryPageDto? Page)> ListAsync(
        CoachMemoryListFilter filter,
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>Approves a candidate, optionally with a learner edit.</summary>
    Task<(CoachMemoryStatusCode Status, CoachMemoryFactDto? Fact)> ApproveAsync(
        string factId,
        CoachMemoryApproveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Declines a candidate.</summary>
    Task<CoachMemoryStatusCode> RejectAsync(
        string factId,
        CoachMemoryRejectRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Edits an active fact.</summary>
    Task<(CoachMemoryStatusCode Status, CoachMemoryFactDto? Fact)> EditAsync(
        string factId,
        CoachMemoryEditRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets one fact.</summary>
    Task<CoachMemoryStatusCode> ForgetAsync(
        string factId,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets everything the current learner has saved.</summary>
    Task<(CoachMemoryStatusCode Status, CoachMemoryForgetAllResponse? Result)> ForgetAllAsync(
        CancellationToken cancellationToken = default);
}
