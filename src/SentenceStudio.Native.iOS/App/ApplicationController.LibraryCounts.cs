using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// The Library list's counts (Captain 2026-11-13: "Still missing counts"). Resources keeps the
/// count its list read; the other rows read one COUNT each when Library shows and when one of
/// its pages closes, for the signed-in owner only.
/// </summary>
internal sealed partial class ApplicationController
{
    private (string Owner, NativeLibraryCountSet Counts)? _libraryCounts;
    private long _libraryCountGeneration;

    /// <summary>Vocabulary: the open list's own count when it has one, else the counted one.</summary>
    internal int? LibraryVocabularyCount => _vocabulary?.KnownWordCount ?? TrustedLibraryCounts?.Words;
    internal int? LibrarySkillCount => TrustedLibraryCounts?.Skills;
    internal int? LibraryPairCount => TrustedLibraryCounts?.Pairs;

    private NativeLibraryCountSet? TrustedLibraryCounts =>
        _libraryCounts is { } counted && LibraryOwner() == counted.Owner ? counted.Counts : null;

    private string? LibraryOwner()
    {
        if (_services is null || _authState is not {
                Phase: NativePhase.Ready, UserId: { Length: > 0 } owner })
            return null;
        var scope = _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
        return scope.TryGetUserProfileId(out var current) && current == owner ? owner : null;
    }

    /// <summary>Reads the counts again (the latest request wins; a failure keeps the last ones).</summary>
    private void RefreshLibraryCounts()
    {
        if (_disposed || _services is null || LibraryOwner() is not { } owner)
            return;
        var generation = Interlocked.Increment(ref _libraryCountGeneration);
        var counter = new NativeLibraryCounts(
            _services.Provider.GetRequiredService<IServiceScopeFactory>());
        _ = CountLibraryAsync(counter, owner, generation);
    }

    private async Task CountLibraryAsync(NativeLibraryCounts counter, string owner,
        long generation)
    {
        try
        {
            var counts = await Task.Run(() => counter.CountAsync(owner, _lifetime.Token));
            if (_disposed || counts is null
                || generation != Interlocked.Read(ref _libraryCountGeneration)
                || LibraryOwner() != owner)
                return;
            _libraryCounts = (owner, counts);
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
