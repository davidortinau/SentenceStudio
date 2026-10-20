namespace SentenceStudio.Native.iOS.App;

internal static class ApplicationFormRoutes
{
    internal const string ResourceForm = "resource-form";
    internal const string Skills = "skills";
    internal const string SkillForm = "skill-form";
    internal const string Vocabulary = "vocabulary";
    internal const string VocabularyWordForm = "vocabulary-word-form";

    internal static bool ShouldPopResourceForm(
        bool formMounted, IReadOnlyList<string> path) =>
        formMounted && path.Count == 1 && path[0] == ResourceForm;

    internal static bool ShouldPopSkillForm(
        bool formOpen, bool formMounted, bool canNavigateBack,
        IReadOnlyList<string> path) =>
        !formOpen && formMounted && canNavigateBack
        && path.Count == 2 && path[0] == Skills && path[1] == SkillForm;

    internal static bool ShouldPopVocabularyWordForm(
        bool formOpen, bool formMounted, bool canNavigateBack,
        IReadOnlyList<string> path) =>
        !formOpen && formMounted && canNavigateBack
        && path.Count == 2 && path[0] == Vocabulary
        && path[1] == VocabularyWordForm;
}
