using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// v1 English-only <see cref="IPlanCopyProvider"/>. Hand-rolled strings so we
/// don't block on the resx lane. Replaced by a real IStringLocalizer-backed
/// implementation in <c>narrative-localization-resx</c>.
/// </summary>
public sealed class EnglishPlanCopyProvider : IPlanCopyProvider
{
    public (string Title, string Description) GetItemCopy(
        PlanActivityType activityType,
        int? vocabDueCount,
        string? resourceTitle,
        string? skillName)
    {
        return activityType switch
        {
            PlanActivityType.VocabularyReview => (
                Title: vocabDueCount is > 0 ? $"Review {vocabDueCount} vocabulary words" : "Vocabulary review",
                Description: "Refresh words due for spaced repetition."),
            PlanActivityType.Reading => (
                Title: resourceTitle is not null ? $"Read: {resourceTitle}" : "Reading practice",
                Description: "Read the resource for comprehension."),
            PlanActivityType.Listening => (
                Title: resourceTitle is not null ? $"Listen: {resourceTitle}" : "Listening practice",
                Description: "Listen and answer comprehension cues."),
            PlanActivityType.VideoWatching => (
                Title: resourceTitle is not null ? $"Watch: {resourceTitle}" : "Video practice",
                Description: "Watch the resource with active focus."),
            PlanActivityType.Shadowing => (
                Title: resourceTitle is not null ? $"Shadow: {resourceTitle}" : "Shadowing practice",
                Description: "Speak along to build pronunciation."),
            PlanActivityType.Translation => (
                Title: "Translation",
                Description: "Translate sentences using today's vocabulary."),
            PlanActivityType.Cloze => (
                Title: "Cloze practice",
                Description: "Fill in the missing words."),
            PlanActivityType.Writing => (
                Title: "Writing",
                Description: "Write sentences using today's words."),
            PlanActivityType.Conversation => (
                Title: skillName is not null ? $"Conversation: {skillName}" : "Conversation practice",
                Description: "Hold a guided conversation."),
            PlanActivityType.VocabularyGame => (
                Title: "Vocabulary game",
                Description: "Reinforce vocabulary through play."),
            PlanActivityType.SceneDescription => (
                Title: resourceTitle is not null ? $"Describe scene: {resourceTitle}" : "Scene description",
                Description: "Describe what you see using target language."),
            PlanActivityType.NumberDrill => (
                Title: "Number drill",
                Description: "Reinforce numeric recall."),
            _ => (
                Title: activityType.ToString(),
                Description: "Daily practice."),
        };
    }

    public string GetSelectionReason(string reasonKeyOrText) => reasonKeyOrText;

    public string GetRationale(string rationaleText) => rationaleText;

    public string GetFocusArea(string focusAreaKey) => focusAreaKey;
}
