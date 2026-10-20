using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal sealed record LearningResourceWord(
    string Id, string TargetTerm, string? NativeTerm, string? Tags,
    LexicalUnitType LexicalUnitType);

internal sealed record LearningResourceViewState
{
    internal NativeResourceDetailPhase Phase { get; init; } = NativeResourceDetailPhase.Loading;
    internal string Title { get; init; } = string.Empty;
    internal string? Description { get; init; }
    internal string? MediaType { get; init; }
    internal string? Language { get; init; }
    internal string? MediaUrl { get; init; }
    internal string? Tags { get; init; }
    internal string? Transcript { get; init; }
    internal string? Translation { get; init; }
    internal DateTime CreatedAt { get; init; }
    internal DateTime UpdatedAt { get; init; }
    internal bool IsSmartResource { get; init; }
    internal bool IsSaving { get; init; }
    internal bool IsReloading { get; init; }
    internal bool IsGenerating { get; init; }
    internal bool IsImporting { get; init; }
    internal bool IsQuickAddSearching { get; init; }
    internal bool IsQuickAddAttaching { get; init; }
    internal bool QuickAddResultsAvailable { get; init; }
    internal bool CanQuickAdd { get; init; }
    internal bool HasDraft { get; init; }
    internal bool CanSave { get; init; } = true;
    internal IReadOnlyList<LearningResourceWord> Vocabulary { get; init; } = [];
    internal IReadOnlyList<LearningResourceWord> QuickAddResults { get; init; } = [];
    internal string? ErrorKey { get; init; }
    internal string? Status { get; init; }
    internal string? FileStatus { get; init; }
    internal NativeResourceSyncPhase Sync { get; init; }
    internal string? SyncErrorKey { get; init; }
    internal bool ShowDeleteConfirmation { get; init; }
}

