using System.Collections.Immutable;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public enum NativeResourceListPhase
{
    Idle, Loading, Populated, Empty, NoMatches, LoadError, Blocked
}

public enum NativeResourceDetailPhase
{
    Idle, Loading, Ready, NotFound, LoadError, ReadOnly, Saving, SaveError, Conflict, Blocked,
    Creating, Deleting
}

public enum NativeResourceSyncPhase { Unconfirmed, Pending, Confirmed }
public enum NativeResourceViewMode { Grid, List }
public enum NativeResourceVocabularySearchPhase { Idle, Searching, Ready, Error }
public enum NativeResourceMutationResult
{
    Success, NotFoundOrForbidden, SmartReadOnly, Invalid, AlreadyPresent, Failed
}

public sealed record NativeResourceVocabularyRow(
    string Id, string TargetTerm, string NativeTerm,
    LexicalUnitType LexicalUnitType = LexicalUnitType.Unknown,
    string? Tags = null, string? Language = null, string? Lemma = null,
    VocabularyPartOfSpeech? PartOfSpeech = null)
{
    public bool IsExisting { get; init; }
}

public sealed record NativeResourceImportResult(
    int Added, int Skipped, NativeResourceMutationResult Result,
    string? ErrorKey = null)
{
    public int GeneratedTotal { get; init; }
    public int CreatedCount { get; init; }
    public int ExistingLinkedCount { get; init; }
}

public sealed record NativeResourceGeneratedVocabulary(
    IReadOnlyList<NativeResourceVocabularyRow> Words,
    int GeneratedTotal, int FilteredCount);

public interface IOwnerBoundNativeAiGateway
{
    Task<T?> SendPromptAsync<T>(
        string owner, string prompt, CancellationToken cancellationToken);

    /// <summary>
    /// One user message with separate system instructions (/api/v1/ai/chat-messages).
    /// Hosts without it fall back to a single combined prompt.
    /// </summary>
    Task<T?> SendWithInstructionsAsync<T>(
        string owner, string instructions, string message, CancellationToken cancellationToken) =>
        SendPromptAsync<T>(owner, $"SYSTEM INSTRUCTIONS:\n{instructions}\n\nTASK:\n{message}",
            cancellationToken);

    /// <summary>
    /// Sends on a source AI tier (for example Reasoning for graders). The
    /// production gateway forwards the tier; ports without tier support
    /// (test fakes) keep their default-tier behaviour.
    /// </summary>
    Task<T?> SendPromptAsync<T>(string owner, string prompt,
        SentenceStudio.Services.AiTier tier, CancellationToken cancellationToken) =>
        SendPromptAsync<T>(owner, prompt, cancellationToken);
}

public sealed record NativeResourceCreateResult(
    NativeResourceMutationResult Result, string? ResourceId = null,
    string? ErrorKey = null);

public sealed record NativeResourceVocabularySearchState(
    NativeResourceVocabularySearchPhase Phase,
    ImmutableArray<NativeResourceVocabularyRow> Results = default,
    string Query = "", string? ErrorKey = null);

public static class NativeResourceSyncStatus
{
    public static string Describe(NativeResourceSyncPhase phase, string? errorKey,
        Func<string, string> text)
    {
        var status = phase switch
        {
            NativeResourceSyncPhase.Pending =>
                $"{text("NativeResources_Sync")}: {text("DebugHealth_Pending")}",
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

public enum NativeResourceField
{
    Title, Description, Tags, MediaType, Language, MediaUrl, Transcript, Translation
}

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
    ResourceMetadataSnapshot OriginalMetadata)
{
    public string? Transcript { get; init; }
    public string? Translation { get; init; }
    public ImmutableArray<NativeResourceVocabularyRow> Vocabulary { get; init; }
    public ResourceContentSnapshot? OriginalContent { get; init; }
}

public sealed record NativeResourceDraft(
    string Title, string? Description, string? Tags,
    bool TitleDirty = false, bool DescriptionDirty = false, bool TagsDirty = false,
    string? MediaType = null, string? Language = null, string? MediaUrl = null,
    string? Transcript = null, string? Translation = null,
    bool MediaTypeDirty = false, bool LanguageDirty = false, bool MediaUrlDirty = false,
    bool TranscriptDirty = false, bool TranslationDirty = false,
    ImmutableArray<NativeResourceVocabularyRow> StagedVocabulary = default);

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
    string? SyncErrorKey = null,
    NativeResourceViewMode ViewMode = NativeResourceViewMode.Grid,
    bool StarterBusy = false,
    NativeResourceCreateResult? LastStarterResult = null);

public sealed record NativeResourceDetailState(
    NativeResourceDetailPhase Phase,
    NativeResourceDetail? Resource = null,
    NativeResourceDraft? Draft = null,
    string? ErrorKey = null,
    NativeResourceSyncPhase Sync = NativeResourceSyncPhase.Unconfirmed,
    string? SyncErrorKey = null,
    ResourceMetadataUpdateResult? LastSaveResult = null,
    bool Reloading = false,
    bool ShowDeleteConfirmation = false,
    NativeResourceVocabularySearchState? VocabularySearch = null,
    NativeResourceImportResult? LastImport = null,
    bool VocabularyBusy = false)
{
    public string? CommittedResourceId { get; init; }
}

public interface INativeResourceStore
{
    Task<NativeVocabularyCatalog> ListOwnedVocabularyAsync(
        string owner, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary browsing is not available from this store.");

    Task<NativeVocabularyDetail?> GetOwnedVocabularyAsync(
        string owner, string wordId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary detail is not available from this store.");

    /// <summary>Whether the word is still one of the owner's, without loading its detail.</summary>
    async Task<bool> OwnsVocabularyAsync(
        string owner, string wordId, CancellationToken cancellationToken) =>
        await GetOwnedVocabularyAsync(owner, wordId, cancellationToken) is not null;

    Task<NativeVocabularySaveStatus> SaveOwnedVocabularyAsync(
        string owner, NativeVocabularyEditDraft draft,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary editing is not available from this store.");

    Task<NativeVocabularyDeleteStatus> DeleteOwnedVocabularyAsync(
        string owner, string wordId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary deletion is not available from this store.");

    /// <summary>
    /// Source ReferenceSentencesSection Add and Keep: a curated sentence on the owner's word,
    /// recorded to them. <paramref name="coreWhenFirst"/> makes it the core example when the
    /// word has none of the owner's yet (the source's manual add).
    /// </summary>
    Task<NativeExampleSentenceResult> AddOwnedExampleAsync(
        string owner, string wordId, NativeExampleSentenceDraft draft,
        ExampleSentenceSource source, bool coreWhenFirst,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Example sentences are not available from this store.");

    /// <summary>Source SaveEditAsync: the sentence's text, translation and register.</summary>
    Task<NativeExampleSentenceResult> EditOwnedExampleAsync(
        string owner, string wordId, string exampleId, NativeExampleSentenceDraft draft,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Example sentences are not available from this store.");

    /// <summary>Source SetCoreAsync: one core example per word, or none.</summary>
    Task<NativeExampleSentenceResult> SetOwnedExampleCoreAsync(
        string owner, string wordId, string exampleId, bool isCore,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Example sentences are not available from this store.");

    /// <summary>Source ToggleFlagAsync: a flagged sentence feeds no quiz.</summary>
    Task<NativeExampleSentenceResult> SetOwnedExampleFlagAsync(
        string owner, string wordId, string exampleId, bool isFlagged,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Example sentences are not available from this store.");

    Task<NativeExampleSentenceResult> DeleteOwnedExampleAsync(
        string owner, string wordId, string exampleId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Example sentences are not available from this store.");

    /// <summary>
    /// Source GenerateAsync: AI suggestions for the owner's word (its stored terms), not saved.
    /// Empty when the AI returned none; throws when it could not be asked.
    /// </summary>
    Task<IReadOnlyList<NativeExampleSentenceDraft>> SuggestOwnedExamplesAsync(
        string owner, string wordId, string targetLanguage, string nativeLanguage, int count,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Example suggestions are not available from this store.");

    Task<NativeVocabularyDuplicateScan> FindOwnedVocabularyDuplicatesAsync(
        string owner, string? focusTerm, int skip, int take,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Duplicate search is not available from this store.");

    /// <summary>Source MergeVocabularyWordsAsync for the active owner.</summary>
    Task<NativeVocabularyMergeResult> MergeOwnedVocabularyDuplicatesAsync(
        string owner, string keeperWordId, IReadOnlyList<string> duplicateWordIds,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Duplicate merge is not available from this store.");

    Task<IReadOnlyList<NativeResourceItem>> ListAsync(
        string owner, CancellationToken cancellationToken);

    Task<NativeResourceDetail?> GetAsync(
        string owner, string resourceId, CancellationToken cancellationToken);

    Task<ResourceMetadataUpdateResult> SaveMetadataAsync(
        string owner, string resourceId, ResourceMetadataSnapshot original,
        string title, string? description, string? tags,
        CancellationToken cancellationToken);

    Task<ResourceMetadataUpdateResult> SaveContentAsync(
        string owner, string resourceId, ResourceContentSnapshot original,
        ResourceContentUpdate content, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Full resource editing is not available from this store.");

    Task<NativeResourceCreateResult> CreateAsync(
        string owner, NativeResourceDraft draft, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Resource creation is not available from this store.");

    Task<IReadOnlyList<NativeResourceVocabularyRow>> SearchVocabularyAsync(
        string owner, string resourceId, string language, string query,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary search is not available from this store.");

    Task<NativeResourceMutationResult> AttachVocabularyAsync(
        string owner, string resourceId, string wordId, string language,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary attachment is not available from this store.");

    Task<NativeResourceMutationResult> CreateVocabularyAsync(
        string owner, string resourceId, NativeResourceVocabularyRow word, string language,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary creation is not available from this store.");

    Task<NativeResourceImportResult> ImportVocabularyAsync(
        string owner, string resourceId, string? language,
        IReadOnlyList<NativeResourceVocabularyRow> words,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary import is not available from this store.");

    Task<NativeResourceMutationResult> RemoveVocabularyAsync(
        string owner, string resourceId, string wordId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary unlink is not available from this store.");

    Task<NativeResourceMutationResult> DeleteAsync(
        string owner, string resourceId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Resource deletion is not available from this store.");

    Task<NativeResourceCreateResult> CreateStarterAsync(
        string owner, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Starter vocabulary is not available from this store.");

    Task<IReadOnlyList<NativeResourceVocabularyRow>> GenerateVocabularyAsync(
        string owner, string resourceId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vocabulary generation is not available from this store.");

    Task<NativeResourceGeneratedVocabulary> GenerateVocabularyFromDraftAsync(
        string owner, string resourceId, NativeResourceDraft draft,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Current-draft vocabulary generation is not available from this store.");
}
