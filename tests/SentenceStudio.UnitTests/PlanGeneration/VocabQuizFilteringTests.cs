using Xunit;
using FluentAssertions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Tests the filtering and mode-selection logic used by VocabQuiz.razor.
/// These validate the model-level properties that the quiz page relies on
/// to exclude known words, honor DueOnly, and choose Text vs MultipleChoice.
/// </summary>
public class VocabQuizFilteringTests
{
    // ── Bug A: Known words must be filtered out ──────────────────────

    [Fact]
    public void KnownWord_IsKnownTrue_MustBeFilteredOut()
    {
        // "Sightseeing" scenario: MasteryScore=0.90, ProductionInStreak=3
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.90f,
            ProductionInStreak = 3,
            CurrentStreak = 7
        };

        var item = MakeQuizItem(progress);

        item.IsKnown.Should().BeTrue("MasteryScore >= 0.85 AND ProductionInStreak >= 2 → IsKnown");

        // The quiz filter MUST exclude this word (the old code checked IsCompleted, which was stale)
        var shouldInclude = !item.IsKnown;
        shouldInclude.Should().BeFalse("known words must be excluded from the quiz");
    }

    [Fact]
    public void IsCompleted_IsStale_NotReliableForFiltering()
    {
        // Demonstrates the bug: IsCompleted=false but IsKnown=true
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.90f,
            ProductionInStreak = 3,
            IsCompleted = false // Never updated by the new mastery system
        };

        var item = MakeQuizItem(progress);

        item.IsKnown.Should().BeTrue("computed IsKnown says this word is known");
        progress.IsCompleted.Should().BeFalse("stale persisted field was never updated");

        // If code used IsCompleted, this word would PASS the filter — that's the bug
        var wouldPassOldFilter = !(progress.IsCompleted);
        wouldPassOldFilter.Should().BeTrue("old filter lets known words through — BUG");

        var wouldPassNewFilter = !(item.IsKnown);
        wouldPassNewFilter.Should().BeFalse("new filter correctly excludes known words");
    }

    [Fact]
    public void LearningWord_HighMastery_ButLowProduction_NotKnown()
    {
        // "Beach Towel" scenario: MasteryScore=1.0 but ProductionInStreak=1
        var progress = new VocabularyProgress
        {
            MasteryScore = 1.0f,
            ProductionInStreak = 1,
            CurrentStreak = 7
        };

        var item = MakeQuizItem(progress);

        item.IsKnown.Should().BeFalse("ProductionInStreak < 2 → not Known yet");
        item.IsPromoted.Should().BeTrue("MasteryScore >= 0.50 → promoted to Text mode");
    }

    [Fact]
    public void UnseenWord_NotKnown_IncludedInQuiz()
    {
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.0f,
            ProductionInStreak = 0,
            CurrentStreak = 0,
            TotalAttempts = 0
        };

        var item = MakeQuizItem(progress);

        item.IsKnown.Should().BeFalse();
        var shouldInclude = !item.IsKnown;
        shouldInclude.Should().BeTrue("unseen words must be included in the quiz");
    }

    // ── Bug B: DueOnly filtering ─────────────────────────────────────

    [Fact]
    public void DueOnly_WordDueForReview_Included()
    {
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.40f,
            NextReviewDate = DateTime.Now.AddDays(-1), // Due yesterday
            TotalAttempts = 5
        };

        var isDue = progress.NextReviewDate != null && progress.NextReviewDate <= DateTime.Now;
        isDue.Should().BeTrue("word with past NextReviewDate is due for review");
    }

    [Fact]
    public void DueOnly_WordNotYetDue_Excluded()
    {
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.40f,
            NextReviewDate = DateTime.Now.AddDays(3), // Due in 3 days
            TotalAttempts = 5
        };

        var isDue = progress.NextReviewDate != null && progress.NextReviewDate <= DateTime.Now;
        isDue.Should().BeFalse("word with future NextReviewDate should not appear in DueOnly quiz");
    }

    [Fact]
    public void DueOnly_UnseenWord_NoNextReviewDate_Included()
    {
        // Brand-new words with no review date should still appear (they have no progress)
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.0f,
            NextReviewDate = null,
            TotalAttempts = 0
        };

        var isDueOrUnseen = (progress.NextReviewDate != null && progress.NextReviewDate <= DateTime.Now)
            || progress.TotalAttempts == 0;
        isDueOrUnseen.Should().BeTrue("unseen words should be included even in DueOnly mode");
    }

    [Fact]
    public void IsDueForReview_ComputedProperty_MatchesManualCheck()
    {
        var duePast = new VocabularyProgress { NextReviewDate = DateTime.Now.AddHours(-1) };
        var dueFuture = new VocabularyProgress { NextReviewDate = DateTime.Now.AddDays(2) };
        var noDate = new VocabularyProgress { NextReviewDate = null };

        duePast.IsDueForReview.Should().BeTrue();
        dueFuture.IsDueForReview.Should().BeFalse();
        noDate.IsDueForReview.Should().BeFalse();
    }

    // ── Bug C: Mode selection — promoted words get Text mode ─────────

    [Fact]
    public void ModeSelection_MasteryAbove50Percent_GetsTextMode()
    {
        // "Beach Towel": MasteryScore=1.0 → must get Text mode
        var progress = new VocabularyProgress
        {
            MasteryScore = 1.0f,
            ProductionInStreak = 1,
            CurrentStreak = 7
        };

        var item = MakeQuizItem(progress);

        var mode = (item.IsPromotedInQuiz || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
            ? "Text" : "MultipleChoice";

        mode.Should().Be("Text", "MasteryScore >= 0.50 must result in Text mode");
    }

    [Fact]
    public void ModeSelection_MasteryBelow50Percent_GetsMultipleChoice()
    {
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.30f,
            ProductionInStreak = 0,
            CurrentStreak = 2
        };

        var item = MakeQuizItem(progress);

        var mode = (item.IsPromotedInQuiz || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
            ? "Text" : "MultipleChoice";

        mode.Should().Be("MultipleChoice", "MasteryScore < 0.50 → MultipleChoice");
    }

    [Fact]
    public void ModeSelection_NullProgress_DefaultsToMultipleChoice()
    {
        // If Progress is somehow null, fallback to 0f → MultipleChoice
        var item = new VocabularyQuizItem
        {
            Word = new VocabularyWord
            {
                Id = "test-word",
                TargetLanguageTerm = "test",
                NativeLanguageTerm = "test"
            },
            Progress = null
        };

        var mode = (item.IsPromotedInQuiz || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
            ? "Text" : "MultipleChoice";

        mode.Should().Be("MultipleChoice", "null Progress → fallback to MultipleChoice");
    }

    [Fact]
    public void ModeSelection_QuizLocalPromotion_OverridesToText()
    {
        // Quiz-local: 3 consecutive correct recognition → promoted within quiz
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.20f, // Low global mastery
        };

        var item = MakeQuizItem(progress);
        item.QuizRecognitionStreak = 3; // Promoted in THIS quiz session

        item.IsPromotedInQuiz.Should().BeTrue("3 recognition correct → promoted in quiz");

        var mode = (item.IsPromotedInQuiz || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
            ? "Text" : "MultipleChoice";

        mode.Should().Be("Text", "quiz-local promotion overrides to Text");
    }

    // ── Comprehensive: full pipeline filter simulation ───────────────

    [Fact]
    public void FullFilter_SimulatesQuizLoadVocabulary()
    {
        // Simulate the quiz page's LoadVocabulary filtering logic
        var items = new List<VocabularyQuizItem>
        {
            // Known word (Sightseeing) — MUST be excluded
            MakeQuizItem(new VocabularyProgress
            {
                VocabularyWordId = "sightseeing",
                MasteryScore = 0.90f,
                ProductionInStreak = 3,
                IsCompleted = false // stale!
            }),
            // High mastery but not enough production (Beach Towel) — included, Text mode
            MakeQuizItem(new VocabularyProgress
            {
                VocabularyWordId = "beach-towel",
                MasteryScore = 1.0f,
                ProductionInStreak = 1,
                IsCompleted = false
            }),
            // Learning word — included, MultipleChoice
            MakeQuizItem(new VocabularyProgress
            {
                VocabularyWordId = "restaurant",
                MasteryScore = 0.30f,
                ProductionInStreak = 0,
                IsCompleted = false
            }),
            // Unseen word — included, MultipleChoice
            MakeQuizItem(new VocabularyProgress
            {
                VocabularyWordId = "airport",
                MasteryScore = 0.0f,
                TotalAttempts = 0,
                IsCompleted = false
            })
        };

        // Apply the FIXED filter (IsKnown, not IsCompleted)
        var filtered = items
            .Where(i => !i.IsKnown)
            .Where(i => !(i.Progress?.IsInGracePeriod ?? false))
            .ToList();

        filtered.Should().HaveCount(3, "only the Known word (sightseeing) should be filtered out");
        filtered.Select(i => i.Progress!.VocabularyWordId)
            .Should().NotContain("sightseeing")
            .And.Contain("beach-towel")
            .And.Contain("restaurant")
            .And.Contain("airport");

        // Verify mode selection for beach-towel
        var beachTowel = filtered.First(i => i.Progress!.VocabularyWordId == "beach-towel");
        var btMode = (beachTowel.IsPromotedInQuiz || (beachTowel.Progress?.MasteryScore ?? 0f) >= 0.50f)
            ? "Text" : "MultipleChoice";
        btMode.Should().Be("Text", "Beach Towel has MasteryScore=1.0 → Text mode");
    }

    // ── Helper ────────────────────────────────────────────────────────

    private static VocabularyQuizItem MakeQuizItem(VocabularyProgress progress)
    {
        return new VocabularyQuizItem
        {
            Word = new VocabularyWord
            {
                Id = progress.VocabularyWordId ?? Guid.NewGuid().ToString(),
                TargetLanguageTerm = "test",
                NativeLanguageTerm = "test"
            },
            Progress = progress
        };
    }
}
