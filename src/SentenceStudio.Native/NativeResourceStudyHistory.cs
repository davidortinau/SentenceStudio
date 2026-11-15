using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native;

public enum NativeResourceStudyPhase
{
    /// <summary>Nothing read (no resource open, or no trusted owner): nothing shows.</summary>
    Idle,
    Loading,
    Ready,
    /// <summary>The owner does not hold the resource: nothing shows.</summary>
    Unavailable,
    /// <summary>The read failed: the page says so.</summary>
    Failed,
}

public sealed record NativeResourceStudyState(
    NativeResourceStudyPhase Phase,
    string? ResourceId = null,
    ResourceStudySummary? Summary = null);

/// <summary>
/// The open resource's study history (issue #219) for the native heads: the shared
/// <see cref="ResourceStudySummaryService"/> read for the trusted device owner, kept while that
/// owner stays signed in. A read that a newer one replaced, or that finished after an account
/// change, never publishes.
/// </summary>
public sealed class NativeResourceStudyHistory(
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    ResourceStudySummaryService summaries,
    ILogger<NativeResourceStudyHistory> logger)
{
    private static readonly NativeResourceStudyState Nothing = new(NativeResourceStudyPhase.Idle);
    private readonly object _gate = new();
    private NativeResourceStudyState _state = Nothing;
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp? _stamp;
    private long _generation;

    /// <summary>The latest read for the current owner; Idle once the owner changed.</summary>
    public NativeResourceStudyState State
    {
        get
        {
            lock (_gate)
                return IsCurrentOwner(_owner, _stamp) ? _state : Nothing;
        }
    }

    /// <summary>
    /// Reads <paramref name="resourceId"/>'s history. Reading the resource already shown keeps
    /// its facts visible until the new ones arrive.
    /// </summary>
    public async Task<NativeResourceStudyState> LoadAsync(
        string resourceId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentOwner(out var owner))
        {
            logger.LogWarning("Resource study history refused without an active owner.");
            Reset();
            return Nothing;
        }
        var stamp = userScope.CaptureStamp();
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
            var keep = _owner == owner && _state.ResourceId == resourceId
                && _state.Summary is not null;
            _owner = owner;
            _stamp = stamp;
            _state = keep ? _state
                : new NativeResourceStudyState(NativeResourceStudyPhase.Loading, resourceId);
        }
        NativeResourceStudyState next;
        try
        {
            var summary = await summaries.GetAsync(owner, resourceId, cancellationToken);
            next = summary is null
                ? new(NativeResourceStudyPhase.Unavailable, resourceId)
                : new(NativeResourceStudyPhase.Ready, resourceId, summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resource study history read failed for resource {ResourceId}.",
                resourceId);
            next = new(NativeResourceStudyPhase.Failed, resourceId);
        }
        lock (_gate)
        {
            if (generation == _generation && IsCurrentOwner(owner, stamp))
                _state = next;
            return IsCurrentOwner(_owner, _stamp) ? _state : Nothing;
        }
    }

    /// <summary>Forgets the shown history (the form closed); a read in flight never publishes.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _state = Nothing;
            _owner = null;
            _stamp = null;
        }
    }

    private bool IsCurrentOwner(string? owner, DeviceUserScopeProvider.ScopeStamp? stamp) =>
        owner is not null && stamp is { } current && userScope.IsCurrent(current)
        && TryCurrentOwner(out var active) && active == owner;

    private bool TryCurrentOwner(out string owner)
    {
        if (userScope.TryGetUserProfileId(out owner)
            && !string.IsNullOrWhiteSpace(owner)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal))
            return true;
        owner = string.Empty;
        return false;
    }
}
