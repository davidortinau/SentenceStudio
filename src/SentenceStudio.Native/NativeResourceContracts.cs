using System.Collections.Immutable;
using SentenceStudio.Data;

namespace SentenceStudio.Native;

public enum NativeResourceListPhase
{
    Idle, Loading, Populated, Empty, NoMatches, LoadError, Blocked
}

public enum NativeResourceDetailPhase
{
    Idle, Loading, Ready, NotFound, LoadError, ReadOnly, Saving, SaveError, Conflict, Blocked
}

public enum NativeResourceSyncPhase { Unconfirmed, Pending, Confirmed }

public static class NativeResourceSyncStatus
{
    public static string Describe(NativeResourceSyncPhase phase, string? errorKey,
        Func<string, string> text)
    {
        var status = phase switch
        {
            NativeResourceSyncPhase.Pending =>
                $"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}",
            NativeResourceSyncPhase.Confirmed when errorKey is null =>
                text("NativeResources_SyncConfirmed"),
            _ => string.Empty
        };
        return errorKey is null ? status
            : string.IsNullOrEmpty(status) ? text(errorKey)
            : $"{status}\n{text(errorKey)}";
    }
}

/// <summary>Shares the entire sync and refresh UI action while it is running.</summary>
public sealed class NativeResourceSyncAction
{
    private readonly object _gate = new();
    private Task? _inflight;

    public Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false } running)
                return running;
            return _inflight = action();
        }
    }
}

public enum NativeResourceField { Title, Description, Tags }

public sealed record NativeResourceItem(
    string Id, string Title, string? Description, string? Tags,
    string? MediaType, string? Language, bool IsSmartResource,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record NativeResourceDetail(
    string Id, string Title, string? Description, string? Tags,
    string? MediaType, string? Language, string? MediaUrl,
    bool IsSmartResource, DateTime CreatedAt, DateTime UpdatedAt,
    bool HasTranscript, string? TranscriptPreview,
    bool HasTranslation, string? TranslationPreview, int VocabularyCount,
    ResourceMetadataSnapshot OriginalMetadata);

public sealed record NativeResourceDraft(
    string Title, string? Description, string? Tags,
    bool TitleDirty = false, bool DescriptionDirty = false, bool TagsDirty = false);

public sealed record NativeResourceListState(
    NativeResourceListPhase Phase,
    ImmutableArray<NativeResourceItem> Items = default,
    ImmutableArray<string> Types = default,
    ImmutableArray<string> Languages = default,
    string SearchText = "",
    string? MediaType = null,
    string? Language = null,
    string? ErrorKey = null,
    NativeResourceSyncPhase Sync = NativeResourceSyncPhase.Unconfirmed,
    string? SyncErrorKey = null);

public sealed record NativeResourceDetailState(
    NativeResourceDetailPhase Phase,
    NativeResourceDetail? Resource = null,
    NativeResourceDraft? Draft = null,
    string? ErrorKey = null,
    NativeResourceSyncPhase Sync = NativeResourceSyncPhase.Unconfirmed,
    string? SyncErrorKey = null,
    ResourceMetadataUpdateResult? LastSaveResult = null,
    bool Reloading = false);

public interface INativeResourceStore
{
    Task<IReadOnlyList<NativeResourceItem>> ListAsync(
        string owner, CancellationToken cancellationToken);

    Task<NativeResourceDetail?> GetAsync(
        string owner, string resourceId, CancellationToken cancellationToken);

    Task<ResourceMetadataUpdateResult> SaveMetadataAsync(
        string owner, string resourceId, ResourceMetadataSnapshot original,
        string title, string? description, string? tags,
        CancellationToken cancellationToken);
}
