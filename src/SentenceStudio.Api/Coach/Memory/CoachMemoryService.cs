using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// Resolves the request's owner and maps between the store and the public contract.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a <see cref="CoachOwner"/> is constructed for an HTTP request, and it is
/// built from the request scope rather than from anything the caller sent. A request that cannot
/// name its owner is answered with <see cref="CoachMemoryStatusCode.NoOwner"/>, which the endpoint
/// layer turns into a 404 — the same answer a caller gets for someone else's fact, so probing
/// cannot distinguish "not yours" from "not there".
/// </para>
/// <para>
/// The service performs no writes of its own beyond delegating to the store, and it never touches
/// plans, settings, progress, SRS state, or accounts. Memory is a preference record; it is not an
/// authority to change the learner's data.
/// </para>
/// </remarks>
public sealed class CoachMemoryService : ICoachMemoryService
{
    private readonly ICoachMemoryStore _store;
    private readonly IUserScopeProvider _userScope;
    private readonly IOptions<CoachMemoryOptions> _options;
    private readonly ILogger<CoachMemoryService> _logger;

    /// <summary>Creates the service.</summary>
    public CoachMemoryService(
        ICoachMemoryStore store,
        IUserScopeProvider userScope,
        IOptions<CoachMemoryOptions> options,
        ILogger<CoachMemoryService> logger)
    {
        _store = store;
        _userScope = userScope;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Value.Enabled;

    /// <inheritdoc />
    public async Task<(CoachMemoryStatusCode Status, CoachMemoryPageDto? Page)> ListAsync(
        CoachMemoryListFilter filter,
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveOwner(out var owner))
        {
            return (CoachMemoryStatusCode.NoOwner, null);
        }

        var page = await _store.ListAsync(owner, filter, pageSize, cursor, cancellationToken).ConfigureAwait(false);
        if (page.Status != CoachMemoryStatusCode.Success)
        {
            return (page.Status, null);
        }

        return (CoachMemoryStatusCode.Success, new CoachMemoryPageDto(
            page.Items.Select(i => i.ToDto()).ToList(),
            page.NextCursor));
    }

    /// <inheritdoc />
    public async Task<(CoachMemoryStatusCode Status, CoachMemoryFactDto? Fact)> ApproveAsync(
        string factId,
        CoachMemoryApproveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveOwner(out var owner))
        {
            return (CoachMemoryStatusCode.NoOwner, null);
        }

        CoachMemoryStoredValue? edited = null;
        if (request.EditedValue is not null)
        {
            edited = CoachMemoryStoredValue.FromDto(request.EditedValue);
        }

        var result = await _store.ApproveAsync(owner, factId, request.ExpectedVersion, edited, cancellationToken).ConfigureAwait(false);
        return (result.Status, result.Fact?.ToDto());
    }

    /// <inheritdoc />
    public async Task<CoachMemoryStatusCode> RejectAsync(
        string factId,
        CoachMemoryRejectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return TryResolveOwner(out var owner)
            ? await _store.RejectAsync(owner, factId, request.ExpectedVersion, cancellationToken).ConfigureAwait(false)
            : CoachMemoryStatusCode.NoOwner;
    }

    /// <inheritdoc />
    public async Task<(CoachMemoryStatusCode Status, CoachMemoryFactDto? Fact)> EditAsync(
        string factId,
        CoachMemoryEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveOwner(out var owner))
        {
            return (CoachMemoryStatusCode.NoOwner, null);
        }

        if (request.Value is null)
        {
            return (CoachMemoryStatusCode.InvalidRequest, null);
        }

        var value = CoachMemoryStoredValue.FromDto(request.Value);
        var result = await _store.EditActiveAsync(owner, factId, request.ExpectedVersion, value, cancellationToken).ConfigureAwait(false);
        return (result.Status, result.Fact?.ToDto());
    }

    /// <inheritdoc />
    public async Task<CoachMemoryStatusCode> ForgetAsync(
        string factId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return TryResolveOwner(out var owner)
            ? await _store.ForgetAsync(owner, factId, expectedVersion, cancellationToken).ConfigureAwait(false)
            : CoachMemoryStatusCode.NoOwner;
    }

    /// <inheritdoc />
    public async Task<(CoachMemoryStatusCode Status, CoachMemoryForgetAllResponse? Result)> ForgetAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveOwner(out var owner))
        {
            return (CoachMemoryStatusCode.NoOwner, null);
        }

        var result = await _store.ForgetAllAsync(owner, cancellationToken).ConfigureAwait(false);
        return result.Status != CoachMemoryStatusCode.Success
            ? (result.Status, null)
            : (CoachMemoryStatusCode.Success, new CoachMemoryForgetAllResponse(result.Forgotten));
    }

    private bool TryResolveOwner(out CoachOwner owner)
    {
        // The non-throwing variant on purpose: an unauthenticated probe should get the same 404 a
        // foreign id gets, not a 500 and not a distinguishable 401 that confirms the route exists.
        if (!_userScope.TryGetUserProfileId(out var userProfileId) || string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning("[Coach] Memory request had no active user id — refusing.");
            owner = default;
            return false;
        }

        return CoachOwner.TryCreate(userProfileId, null, out owner);
    }
}
