namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Library routes: the Library root is a list; each row pushes its page (Resources,
/// Vocabulary, Skills, Minimal Pairs), and the page's editor is pushed above it.
/// </summary>
internal static class ApplicationFormRoutes
{
    internal const string LibraryResources = "library-resources";
    internal const string ResourceForm = "resource-form";
    internal const string Skills = "skills";
    internal const string SkillForm = "skill-form";
    internal const string Vocabulary = "vocabulary";
    internal const string VocabularyWordForm = "vocabulary-word-form";
    internal const string RecentLookups = "recent-lookups";
    internal const string VocabularyDuplicates = "vocabulary-duplicates";
    internal const string MinimalPairs = "minimal-pairs";
    internal const string MinimalPairsChild = "minimal-pairs-child";

    /// <summary>
    /// The resource form is pushed above the Resources page, or straight above the
    /// Library root when Study opens a resource; it is always the top route.
    /// </summary>
    internal static bool ShouldPopResourceForm(
        bool formMounted, IReadOnlyList<string> path) =>
        formMounted && path.Count > 0 && path[^1] == ResourceForm;

    internal static bool ShouldPopSkillForm(
        bool formOpen, bool formMounted, bool canNavigateBack,
        IReadOnlyList<string> path) =>
        !formOpen && formMounted && canNavigateBack
        && path.Count == 2 && path[0] == Skills && path[1] == SkillForm;

    /// <summary>
    /// The word editor sits above the Vocabulary list, or above the duplicate page when its Edit
    /// opened the word; it is always the top route.
    /// </summary>
    internal static bool ShouldPopVocabularyWordForm(
        bool formOpen, bool formMounted, bool canNavigateBack,
        IReadOnlyList<string> path) =>
        !formOpen && formMounted && canNavigateBack
        && path.Count >= 2 && path[0] == Vocabulary
        && path[^1] == VocabularyWordForm;
}
