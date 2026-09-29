using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

public sealed record VocabQuizBatchSelection(
    IReadOnlyList<VocabularyQuizItem> Batch, bool AllMastered);

public sealed record VocabQuizAnswerOption(VocabularyWord Word, string Text);

/// <summary>
/// The source Vocab Quiz's non-focus selection, multiple-choice answers, and
/// isolated-answer grading. Callers must supply words already scoped to the
/// active owner; native adapters additionally verify that scope against storage.
/// </summary>
public static class VocabQuizSourcePolicy
{
    public const int BatchSize = 20;

    public static VocabQuizBatchSelection SelectNonFocusBatch(
        string userId, bool dueOnly, IReadOnlyList<VocabularyQuizItem> candidates,
        DateTime localNow)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Quiz selection requires an active owner.");
        ArgumentNullException.ThrowIfNull(candidates);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in candidates)
        {
            if (item is null || item.Word is null
                || !FocusVocabularySelection.IsUsableVocabularyWord(item.Word)
                || !seenIds.Add(item.Word.Id)
                || item.Progress is { } progress
                    && (!string.Equals(progress.UserId, userId, StringComparison.Ordinal)
                        || !string.Equals(progress.VocabularyWordId, item.Word.Id, StringComparison.Ordinal)))
            {
                throw new NotSupportedException(
                    "Quiz candidates must be distinct, usable words with owner-matched progress.");
            }
        }

        var allMastered = candidates.Count > 0 && candidates.All(item => item.IsKnown);
        var eligible = candidates.Where(item =>
            !item.IsKnown && !(item.Progress?.IsInGracePeriod ?? false));

        if (dueOnly)
        {
            // NextReviewDate is stored in the same local frame as DateTime.Now
            // (VocabularyProgress.IsDueForReview and the source Razor quiz).
            eligible = eligible.Where(item =>
                (item.Progress?.TotalAttempts ?? 0) == 0
                || item.Progress?.NextReviewDate is { } nextReview
                    && nextReview <= localNow);
        }

        return new VocabQuizBatchSelection(
            eligible.OrderBy(item => item.Progress?.MasteryScore ?? 0f)
                .ThenBy(_ => Guid.NewGuid())
                .Take(BatchSize)
                .ToList(),
            allMastered);
    }

    /// <summary>
    /// Sample up to three unique, real answer-side distractors from the full
    /// reachable scope, then shuffle them with the correct answer. A one-option
    /// result retains Razor's legacy fallback; native refuses to display it.
    /// </summary>
    public static IReadOnlyList<VocabQuizAnswerOption> CreateMultipleChoiceOptions(
        VocabularyWord question, IReadOnlyList<VocabularyWord> reachableWords,
        bool promptUsesNativeLanguage, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(reachableWords);
        if (!FocusVocabularySelection.IsUsableVocabularyWord(question)
            || !reachableWords.Any(word => word is not null
                && string.Equals(word.Id, question.Id, StringComparison.Ordinal)
                && string.Equals(word.TargetLanguageTerm, question.TargetLanguageTerm, StringComparison.Ordinal)
                && string.Equals(word.NativeLanguageTerm, question.NativeLanguageTerm, StringComparison.Ordinal)))
        {
            throw new NotSupportedException("A quiz question must belong to the reachable vocabulary.");
        }

        var correct = AnswerText(question, promptUsesNativeLanguage);
        var seenAnswers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            correct.Trim()
        };
        var seenWords = new HashSet<string>(StringComparer.Ordinal);
        var distractors = new List<VocabQuizAnswerOption>();
        foreach (var word in reachableWords)
        {
            if (word is null || !FocusVocabularySelection.IsUsableVocabularyWord(word)
                || !seenWords.Add(word.Id)
                || string.Equals(word.Id, question.Id, StringComparison.Ordinal))
                continue;

            var answer = AnswerText(word, promptUsesNativeLanguage);
            if (seenAnswers.Add(answer.Trim()))
                distractors.Add(new VocabQuizAnswerOption(word, answer));
        }

        var rng = random ?? Random.Shared;
        Shuffle(distractors, rng);
        var options = distractors.Take(3).ToList();
        options.Add(new VocabQuizAnswerOption(question, correct));
        Shuffle(options, rng);
        return options;
    }

    public static FuzzyMatchResult EvaluateAnswer(
        string mode, string answer, string expectedAnswer)
    {
        if (mode is not ("Text" or "MultipleChoice"))
            throw new NotSupportedException(
                "Only isolated MultipleChoice and Text answers can be graded without sentence context.");
        if (string.IsNullOrWhiteSpace(expectedAnswer)
            || !expectedAnswer.Any(char.IsLetterOrDigit))
            throw new NotSupportedException("A quiz answer must have a linguistic expected term.");
        if (string.IsNullOrWhiteSpace(answer))
            return new FuzzyMatchResult();

        return mode switch
        {
            "Text" => answer.Any(char.IsLetterOrDigit)
                ? FuzzyMatcher.Evaluate(answer, expectedAnswer)
                : new FuzzyMatchResult(),
            "MultipleChoice" => GradeMultipleChoice(answer, expectedAnswer),
            _ => throw new NotSupportedException("Unsupported quiz mode.")
        };
    }

    private static FuzzyMatchResult GradeMultipleChoice(string answer, string expectedAnswer)
    {
        var correct = string.Equals(answer.Trim(), expectedAnswer.Trim(),
            StringComparison.OrdinalIgnoreCase);
        return new FuzzyMatchResult
        {
            IsCorrect = correct,
            MatchType = correct ? "Exact" : string.Empty
        };
    }

    private static string AnswerText(VocabularyWord word, bool promptUsesNativeLanguage) =>
        promptUsesNativeLanguage ? word.TargetLanguageTerm! : word.NativeLanguageTerm!;

    private static void Shuffle<T>(IList<T> options, Random random)
    {
        for (var i = options.Count - 1; i > 0; i--)
        {
            var other = random.Next(i + 1);
            (options[i], options[other]) = (options[other], options[i]);
        }
    }
}
