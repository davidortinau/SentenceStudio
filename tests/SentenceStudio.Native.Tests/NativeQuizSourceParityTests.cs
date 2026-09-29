using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizSourceParityTests
{
    private const string Owner = "quiz-owner";

    [Fact]
    public void NonFocusDueSelection_ExcludesKnownGraceFutureAndAttemptedWithoutDate()
    {
        var now = DateTime.Now;
        var neverAttempted = Item("new", review: now.AddDays(3));
        var expiredGrace = Item("expired-grace", mastery: 0.1f, grace: true);
        expiredGrace.Progress!.UserDeclaredAt = now.AddDays(-15);
        var dueAtBoundary = Item("due-now", mastery: 0.15f, attempts: 1, review: now);
        var overdue = Item("overdue", mastery: 0.2f, attempts: 2, review: now.AddDays(-1));
        var future = Item("future", mastery: 0.3f, attempts: 2, review: now.AddDays(1));
        var noDate = Item("no-date", mastery: 0.4f, attempts: 2);
        var known = Item("known", mastery: 0.9f, attempts: 8,
            review: now.AddDays(-1), known: true);
        var grace = Item("grace", grace: true);
        var candidates = new[] {
            grace, known, noDate, future, overdue, dueAtBoundary, expiredGrace, neverAttempted
        };

        var due = VocabQuizSourcePolicy.SelectNonFocusBatch(Owner, true, candidates, now);
        Assert.Equal(new[] { "new", "expired-grace", "due-now", "overdue" },
            due.Batch.Select(item => item.Word.Id));
        Assert.False(due.AllMastered);

        var all = VocabQuizSourcePolicy.SelectNonFocusBatch(Owner, false, candidates, now);
        Assert.Equal(new[] {
            "new", "expired-grace", "due-now", "overdue", "future", "no-date"
        },
            all.Batch.Select(item => item.Word.Id));
        Assert.False(all.AllMastered);
    }

    [Fact]
    public void KnownBatchIsMastered_ButGraceOrNotDueAloneIsNot()
    {
        var now = DateTime.Now;
        var mastered = VocabQuizSourcePolicy.SelectNonFocusBatch(
            Owner, false, [Item("known", mastery: 0.9f, known: true)], now);
        Assert.True(mastered.AllMastered);
        Assert.Empty(mastered.Batch);

        var grace = VocabQuizSourcePolicy.SelectNonFocusBatch(
            Owner, false, [Item("familiar", grace: true)], now);
        Assert.False(grace.AllMastered);
        Assert.Empty(grace.Batch);

        var future = VocabQuizSourcePolicy.SelectNonFocusBatch(
            Owner, true, [Item("future", attempts: 1, review: now.AddDays(1))], now);
        Assert.False(future.AllMastered);
        Assert.Empty(future.Batch);

        var noWords = VocabQuizSourcePolicy.SelectNonFocusBatch(Owner, false, [], now);
        Assert.False(noWords.AllMastered);
        Assert.Empty(noWords.Batch);
    }

    [Fact]
    public void FocusWordsKeepRequestedOrderAndIncludeKnownAndGrace()
    {
        var known = Item("known", mastery: 0.9f, known: true);
        var grace = Item("grace", grace: true);
        var newWord = Item("new");
        var reachable = new[] { known.Word, newWord.Word, grace.Word };

        var focused = FocusVocabularySelection.SelectFocusWords(
            reachable, ["grace", "known", "new"]);
        Assert.Equal(new[] { "grace", "known", "new" }, focused.Select(word => word.Id));
        Assert.Equal(new[] { "new" },
            VocabQuizSourcePolicy.SelectNonFocusBatch(
                Owner, false, [grace, known, newWord], DateTime.Now)
                .Batch.Select(item => item.Word.Id));
        Assert.Single(FocusVocabularySelection.SelectFocusWords(reachable, ["new", "other-owner"]));
    }

    [Fact]
    public void NonFocusBatchCapsAtTwentyAndOrdersByLowestMasteryFirst()
    {
        var candidates = Enumerable.Range(0, 23)
            .Reverse()
            .Select(index => Item($"word-{index:D2}", mastery: index / 100f))
            .ToArray();

        var selected = VocabQuizSourcePolicy.SelectNonFocusBatch(
            Owner, false, candidates, DateTime.Now);
        Assert.Equal(VocabQuizSourcePolicy.BatchSize, selected.Batch.Count);
        Assert.Equal("word-00", selected.Batch[0].Word.Id);
        Assert.Equal("word-19", selected.Batch[^1].Word.Id);
    }

    [Fact]
    public void CandidatesWithWrongProgressOwnerWordOrDuplicateIdAreRejected()
    {
        var valid = Item("owned");
        var foreign = Item("foreign");
        foreign.Progress!.UserId = "other-owner";
        Assert.Throws<NotSupportedException>(() =>
            VocabQuizSourcePolicy.SelectNonFocusBatch(
                Owner, false, [valid, foreign], DateTime.Now));

        foreign.Progress.UserId = Owner;
        foreign.Progress.VocabularyWordId = "another-word";
        Assert.Throws<NotSupportedException>(() =>
            VocabQuizSourcePolicy.SelectNonFocusBatch(
                Owner, false, [foreign], DateTime.Now));
        Assert.Throws<NotSupportedException>(() =>
            VocabQuizSourcePolicy.SelectNonFocusBatch(
                Owner, false, [valid, valid], DateTime.Now));
        Assert.Throws<UnauthorizedAccessException>(() =>
            VocabQuizSourcePolicy.SelectNonFocusBatch(
                "", false, [valid], DateTime.Now));
    }

    [Fact]
    public void ChoicePolicyExcludesDuplicateAnswerCaseAndWhitespace_AndShufflesCorrect()
    {
        var question = Word("question", "bonjour", "HELLO");
        var reachable = new[]
        {
            question,
            Word("other", "salut", "other"),
            Word("duplicate-other", "bonne", " OTHER "),
            Word("later", "demain", "later"),
            Word("yes", "oui", "yes"),
            Word("duplicate-correct", "bonjour", "hello")
        };

        var last = VocabQuizSourcePolicy.CreateMultipleChoiceOptions(
            question, reachable, false, new LastIndexRandom());
        Assert.Equal(4, last.Count);
        Assert.Equal("question", last[^1].Word.Id);
        Assert.Equal(new[] { "other", "later", "yes", "question" },
            last.Select(option => option.Word.Id));
        Assert.Equal(4, last.Select(option => option.Text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var first = VocabQuizSourcePolicy.CreateMultipleChoiceOptions(
            question, reachable, false, new FirstIndexRandom());
        Assert.Equal("question", first[2].Word.Id);
        Assert.Equal(1, first.Count(option =>
            string.Equals(option.Text.Trim(), "HELLO", StringComparison.OrdinalIgnoreCase)));

        var reverse = VocabQuizSourcePolicy.CreateMultipleChoiceOptions(
            question, reachable, true, new LastIndexRandom());
        Assert.Equal(4, reverse.Count);
        Assert.Equal("bonjour", reverse[^1].Text);
        Assert.DoesNotContain(reverse, option => option.Word.Id == "duplicate-correct");
    }

    [Theory]
    [InlineData("Text", "leftover", "remaining/leftover", true)]
    [InlineData("Text", "  take  ", "take (a photo)", true)]
    [InlineData("Text", "\u1100\u1161", "\uAC00", true)]
    [InlineData("Text", "buy", "day", false)]
    [InlineData("Text", "?!", "hello", false)]
    [InlineData("MultipleChoice", " HELLO ", "hello", true)]
    [InlineData("MultipleChoice", "helo", "hello", false)]
    [InlineData("MultipleChoice", "leftover", "remaining/leftover", false)]
    public void SharedGradingMatchesSourceModes(
        string mode, string answer, string expected, bool correct)
    {
        var result = VocabQuizSourcePolicy.EvaluateAnswer(mode, answer, expected);
        Assert.Equal(correct, result.IsCorrect);
        if (mode == "MultipleChoice" && correct)
            Assert.Equal("Exact", result.MatchType);
    }

    [Fact]
    public void AlternativeTextAnswerReturnsFullForm_ButSentenceUseNeedsAWordAndAiContext()
    {
        var accepted = VocabQuizSourcePolicy.EvaluateAnswer(
            "Text", "leftover", "remaining/leftover");
        Assert.True(accepted.IsCorrect);
        Assert.Equal("remaining/leftover", accepted.CompleteForm);
        Assert.Throws<NotSupportedException>(() =>
            VocabQuizSourcePolicy.EvaluateAnswer("Sentence", "I used it.", "word"));
        Assert.Throws<NotSupportedException>(() =>
            VocabQuizSourcePolicy.EvaluateAnswer("Sentence", "", "word"));
        Assert.Throws<NotSupportedException>(() =>
            VocabQuizSourcePolicy.EvaluateAnswer("Text", "???", "..."));
    }

    private static VocabularyQuizItem Item(
        string id, float mastery = 0f, int attempts = 0, DateTime? review = null,
        bool known = false, bool grace = false) => new()
    {
        Word = Word(id, $"target-{id}", $"native-{id}"),
        Progress = new VocabularyProgress
        {
            VocabularyWordId = id,
            UserId = Owner,
            MasteryScore = mastery,
            TotalAttempts = attempts,
            ProductionInStreak = known ? 2 : 0,
            NextReviewDate = review,
            IsUserDeclared = grace,
            UserDeclaredAt = grace ? DateTime.Now.AddDays(-1) : null,
            VerificationState = grace ? VerificationStatus.Pending : VerificationStatus.None
        }
    };

    private static VocabularyWord Word(string id, string target, string native) => new()
    {
        Id = id, TargetLanguageTerm = target, NativeLanguageTerm = native
    };

    private sealed class LastIndexRandom : Random
    {
        public override int Next(int maxValue) => maxValue - 1;
    }

    private sealed class FirstIndexRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }
}
