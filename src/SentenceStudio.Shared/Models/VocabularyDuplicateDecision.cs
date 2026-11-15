namespace SentenceStudio.Shared.Models;

/// <summary>What was decided about a set of duplicate copies (stored as its name).</summary>
public enum VocabularyDuplicateDecisionKind
{
    /// <summary>The copies were merged into <see cref="VocabularyDuplicateDecision.KeeperId"/>.</summary>
    Merged,
    /// <summary>The copies mean different things and stay separate; the scan stops showing them.</summary>
    KeptApart,
}

/// <summary>Who made a duplicate decision (stored as its name).</summary>
public enum VocabularyDuplicateDecider
{
    Ai,
    Learner,
}

/// <summary>
/// A decision about one learner's duplicate copies of a target term (Captain 2026-11-14,
/// docs/duplicate-merge-ai-plan.md, "Remembering decisions"). A kept-apart decision hides its
/// copies from the duplicate scan until a new copy of the term arrives; a merged decision holds
/// what the copies were before the merge, so the learner can undo it. Owner-scoped and synced to
/// the owner's devices. Its ID comes from owner, kind, term and copies
/// (<see cref="Services.SyncIds.VocabularyDuplicateDecision"/>), so two offline devices that
/// decide the same make the same row.
/// </summary>
public class VocabularyDuplicateDecision
{
    public string Id { get; set; } = string.Empty;

    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>The duplicate group's key: the copies' normalized target term.</summary>
    public string TermKey { get; set; } = string.Empty;

    public VocabularyDuplicateDecisionKind Kind { get; set; }

    /// <summary>
    /// The word IDs decided on, ordinal-sorted and joined with ",". For a merge: the keeper and
    /// the copies merged into it.
    /// </summary>
    public string CopyIds { get; set; } = string.Empty;

    /// <summary>The copy a merge kept; null for a kept-apart decision.</summary>
    public string? KeeperId { get; set; }

    public VocabularyDuplicateDecider DecidedBy { get; set; }

    /// <summary>Why, as the AI or the learner gave it (at most a sentence).</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// A merge's undo record: the keeper's and the removed copies' fields before the merge, as
    /// JSON (docs/sync-all-tables-plan.md, "Duplicate decisions"). Null for a kept-apart decision.
    /// </summary>
    public string? Before { get; set; }

    /// <summary>UTC instant of the decision.</summary>
    public DateTime DecidedAt { get; set; }

    /// <summary>UTC instant the learner undid it, or null while it stands.</summary>
    public DateTime? UndoneAt { get; set; }

    /// <summary>The decided word IDs.</summary>
    public IReadOnlyList<string> CopyIdList =>
        CopyIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
