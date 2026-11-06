namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Library routes: each page's editor (or second page) is pushed one level above the
/// Library root, whose switcher shows Resources, Vocabulary, Skills or Pairs.
/// </summary>
internal static class ApplicationFormRoutes
{
    internal const string ResourceForm = "resource-form";
    internal const string SkillForm = "skill-form";
    internal const string VocabularyWordForm = "vocabulary-word-form";
    internal const string RecentLookups = "recent-lookups";

    internal static bool ShouldPopResourceForm(
        bool formMounted, IReadOnlyList<string> path) =>
        formMounted && path.Count == 1 && path[0] == ResourceForm;

    internal static bool ShouldPopSkillForm(
        bool formOpen, bool formMounted, bool canNavigateBack,
        IReadOnlyList<string> path) =>
        !formOpen && formMounted && canNavigateBack
        && path.Count == 1 && path[0] == SkillForm;

    internal static bool ShouldPopVocabularyWordForm(
        bool formOpen, bool formMounted, bool canNavigateBack,
        IReadOnlyList<string> path) =>
        !formOpen && formMounted && canNavigateBack
        && path.Count == 1 && path[0] == VocabularyWordForm;
}
