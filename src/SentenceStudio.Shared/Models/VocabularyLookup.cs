namespace SentenceStudio.Shared.Models;

/// <summary>Which way a lookup translated (stored as its name).</summary>
public enum VocabularyLookupDirection
{
    /// <summary>The looked-up text is in the target language; the translation is native.</summary>
    ToNative,
    /// <summary>The looked-up text is in the native language; the translation is target.</summary>
    ToTarget
}

/// <summary>
/// Where a lookup's translation came from; the direction follows from it (not stored).
/// </summary>
public enum VocabularyLookupOrigin
{
    /// <summary>The learner's word matched by its target term: the translation is its native term.</summary>
    OwnedTargetTerm,
    /// <summary>The learner's word matched by its native term: the translation is its target term.</summary>
    OwnedNativeTerm,
    /// <summary>
    /// The Translate prompt, which always translates into English. Its direction is known only
    /// for a learner whose native language is English (target text to native English).
    /// </summary>
    TranslatedToEnglish
}

/// <summary>Where a lookup was made (stored as text so every client reads it).</summary>
public static class VocabularyLookupActivity
{
    public const string Writing = "Writing";
    public const string Reading = "Reading";
}

/// <summary>
/// A word or phrase the learner looked up in Writing or Reading, with its translation
/// (Captain 2026-11-04, decision 4 B: a dedicated lookup table). One row per learner, language,
/// direction and normalized text: its ID comes from those (<see cref="Services.SyncIds.VocabularyLookup"/>),
/// so a repeat lookup updates the row, and two offline devices make the same row. It syncs to
/// the owner's devices; when two copies meet, the one looked up most recently wins and the
/// earliest first-lookup time is kept (docs/sync-all-tables-plan.md, "Lookup history").
/// A lookup is not vocabulary: "Add to vocabulary" creates or links a real word.
/// </summary>
public class VocabularyLookup
{
    public string Id { get; set; } = string.Empty;

    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>The text as last looked up (trimmed).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The translation from the last lookup.</summary>
    public string Translation { get; set; } = string.Empty;

    /// <summary>The language being learned (for example "Korean").</summary>
    public string Language { get; set; } = string.Empty;

    public VocabularyLookupDirection Direction { get; set; }

    /// <summary><see cref="VocabularyLookupActivity"/> of the last lookup.</summary>
    public string Activity { get; set; } = string.Empty;

    /// <summary>The learner's resource open during the last lookup, if any (a plain reference).</summary>
    public string? ResourceId { get; set; }

    /// <summary>UTC instant of the first lookup.</summary>
    public DateTime FirstLookedUpAt { get; set; }

    /// <summary>UTC instant of the last lookup; the merge rule compares it.</summary>
    public DateTime LastLookedUpAt { get; set; }

    /// <summary>The target-language term (the looked-up text or its translation).</summary>
    public string TargetTerm => Direction == VocabularyLookupDirection.ToNative ? Text : Translation;

    /// <summary>The native-language term (the looked-up text or its translation).</summary>
    public string NativeTerm => Direction == VocabularyLookupDirection.ToNative ? Translation : Text;
}
