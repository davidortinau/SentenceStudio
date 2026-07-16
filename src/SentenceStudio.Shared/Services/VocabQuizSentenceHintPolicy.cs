using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

public enum VocabQuizSentenceHintTransition
{
    Toggle,
    AnswerFeedback,
    LoadCurrentItem,
    Restart,
    Resume,
    DirectionChange,
    OpenFullscreen,
    SentenceShortcut
}

/// <summary>
/// Pure eligibility and state rules for target-language Vocab Quiz sentence hints.
/// </summary>
public static class VocabQuizSentenceHintPolicy
{
    public const int MaxHintsPerTurn = 3;

    public static IReadOnlyList<VocabQuizSentenceHint> GetHintsForTurn(
        bool promptUsesNativeLanguage,
        IReadOnlyList<VocabQuizSentenceHint>? prefetchedHints)
    {
        if (promptUsesNativeLanguage || prefetchedHints is null || prefetchedHints.Count == 0)
            return Array.Empty<VocabQuizSentenceHint>();

        if (prefetchedHints.Count <= MaxHintsPerTurn
            && prefetchedHints.All(hint => !string.IsNullOrWhiteSpace(hint.TargetSentence)))
        {
            return prefetchedHints;
        }

        return prefetchedHints
            .Where(hint => !string.IsNullOrWhiteSpace(hint.TargetSentence))
            .Take(MaxHintsPerTurn)
            .ToArray();
    }

    public static bool ShouldShowButton(
        bool promptUsesNativeLanguage,
        IReadOnlyList<VocabQuizSentenceHint>? prefetchedHints)
        => GetHintsForTurn(promptUsesNativeLanguage, prefetchedHints).Count > 0;

    public static bool GetExpandedState(
        bool isExpanded,
        bool isEligible,
        VocabQuizSentenceHintTransition transition)
    {
        if (!isEligible)
            return false;

        return transition switch
        {
            VocabQuizSentenceHintTransition.Toggle => !isExpanded,
            VocabQuizSentenceHintTransition.AnswerFeedback => isExpanded,
            _ => false
        };
    }
}
