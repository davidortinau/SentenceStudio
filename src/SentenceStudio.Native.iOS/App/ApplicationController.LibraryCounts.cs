using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// The Library list's counts (Captain 2026-11-13: "Still missing counts"). Resources keeps the
/// count its list read; the other rows read one COUNT each, for the signed-in owner only: when
/// Library shows (again only after a sync brought rows, as Resources reads), and always when one
/// of its pages closes or a Skills change finishes.
/// </summary>
internal sealed partial class ApplicationController
{
    private (string Owner, long SyncGeneration, NativeLibraryCountSet Counts)? _libraryCounts;
    private long _libraryCountGeneration;
    private DeviceUserScopeProvider? _libraryScope;

    /// <summary>
    /// Vocabulary: while its page is open, the list's own count (it follows deletes at once);
    /// otherwise the counted one, which follows imports and syncs.
    /// </summary>
    internal int? LibraryVocabularyCount => IsVocabularyOpen
        ? _vocabulary?.KnownWordCount ?? TrustedLibraryCounts?.Words
        : TrustedLibraryCounts?.Words ?? _vocabulary?.KnownWordCount;
    internal int? LibrarySkillCount => TrustedLibraryCounts?.Skills;
    internal int? LibraryPairCount => TrustedLibraryCounts?.Pairs;

    private NativeLibraryCountSet? TrustedLibraryCounts =>
        _libraryCounts is { } counted && LibraryOwner() == counted.Owner ? counted.Counts : null;

    private string? LibraryOwner()
    {
        if (_services is null || _authState is not {
                Phase: NativePhase.Ready, UserId: { Length: > 0 } owner })
            return null;
        _libraryScope ??= _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
        return _libraryScope.TryGetUserProfileId(out var current) && current == owner ? owner : null;
    }

    private long SyncGeneration() =>
        _services?.Provider.GetService<INativeSyncReadiness>()?.SyncGeneration ?? 0;

    /// <summary>Library shows: count unless the counts are this owner's and no sync came since.</summary>
    private void RefreshLibraryCountsIfStale()
    {
        if (_libraryCounts is { } counted && counted.Owner == LibraryOwner()
            && counted.SyncGeneration == SyncGeneration())
            return;
        RefreshLibraryCounts();
    }

    /// <summary>
    /// Reads the counts again (the latest request wins; a failure keeps the last ones). Never
    /// throws: the counts are a side read and must not fail the action that asked for them.
    /// </summary>
    private void RefreshLibraryCounts()
    {
        try
        {
            if (_disposed || _services is null || LibraryOwner() is not { } owner)
                return;
            var generation = Interlocked.Increment(ref _libraryCountGeneration);
            var syncGeneration = SyncGeneration();
            var counter = new NativeLibraryCounts(
                _services.Provider.GetRequiredService<IServiceScopeFactory>());
            _ = CountLibraryAsync(counter, owner, syncGeneration, generation);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Library counts could not start.");
        }
    }

    private async Task CountLibraryAsync(NativeLibraryCounts counter, string owner,
        long syncGeneration, long generation)
    {
        try
        {
            var counts = await Task.Run(() => counter.CountAsync(owner, _lifetime.Token));
            if (_disposed || counts is null
                || generation != Interlocked.Read(ref _libraryCountGeneration)
                || LibraryOwner() != owner)
                return;
            _libraryCounts = (owner, syncGeneration, counts);
            Publish();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The rows keep their last counts (or none); nothing else depends on them.
            _logger?.LogWarning(ex, "Library counts could not be read.");
        }
    }
}
