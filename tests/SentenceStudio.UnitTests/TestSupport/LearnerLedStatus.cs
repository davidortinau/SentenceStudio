using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests;

/// <summary>
/// The Known rule for tests that do not vary the learner's mode: Learner-led, the default every
/// profile has (docs/known-words-plan.md). Tests of Practice-led call the rule with that mode.
/// </summary>
internal static class LearnerLedStatus
{
    public static bool IsKnown(this VocabularyProgress progress) =>
        VocabularyKnownRule.IsKnown(progress, LearningMode.LearnerLed);

    public static bool IsLearning(this VocabularyProgress progress) =>
        VocabularyKnownRule.IsLearning(progress, LearningMode.LearnerLed);

    public static bool IsUnknown(this VocabularyProgress progress) =>
        VocabularyKnownRule.IsUnknown(progress, LearningMode.LearnerLed);

    public static LearningStatus Status(this VocabularyProgress progress) =>
        VocabularyKnownRule.Status(progress, LearningMode.LearnerLed);
}
