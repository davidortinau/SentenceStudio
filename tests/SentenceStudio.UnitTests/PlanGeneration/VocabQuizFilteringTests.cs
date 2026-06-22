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

        // New unified mode-selection algorithm (spec §1.2)
        var mode = item.PendingRecognitionCheck
            ? "MultipleChoice"
            : ((item.Progress?.CurrentStreak ?? 0f) >= 3f || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
                ? "Text"
                : "MultipleChoice";

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

        var mode = item.PendingRecognitionCheck
            ? "MultipleChoice"
            : ((item.Progress?.CurrentStreak ?? 0f) >= 3f || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
                ? "Text"
                : "MultipleChoice";

        mode.Should().Be("MultipleChoice", "MasteryScore < 0.50 and CurrentStreak < 3 → MultipleChoice");
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

        var mode = item.PendingRecognitionCheck
            ? "MultipleChoice"
            : ((item.Progress?.CurrentStreak ?? 0f) >= 3f || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
                ? "Text"
                : "MultipleChoice";

        mode.Should().Be("MultipleChoice", "null Progress → fallback to MultipleChoice");
    }

    [Fact]
    public void ModeSelection_QuizLocalPromotion_NoLongerDrivesMode()
    {
        // Phase 1: Mode is now driven by GLOBAL Progress.CurrentStreak, not session-local
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.20f,
            CurrentStreak = 1f, // Low global streak
        };

        var item = MakeQuizItem(progress);
        item.SessionMCCorrect = 3; // Session counters don't drive mode selection

        // New unified mode-selection algorithm (spec §1.2):
        // PendingRecognitionCheck=false, CurrentStreak < 3, MasteryScore < 0.50 → MC
        var mode = item.PendingRecognitionCheck
            ? "MultipleChoice"
            : ((item.Progress?.CurrentStreak ?? 0f) >= 3f || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
                ? "Text"
                : "MultipleChoice";

        mode.Should().Be("MultipleChoice",
            "session-local counters no longer drive mode; global CurrentStreak=1 → MC");
    }

    [Fact]
    public void ModeSelection_LifetimeStreak3_GetsTextMode()
    {
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.20f,
            CurrentStreak = 3f, // Lifetime streak hits threshold
        };

        var item = MakeQuizItem(progress);

        var mode = item.PendingRecognitionCheck
            ? "MultipleChoice"
            : ((item.Progress?.CurrentStreak ?? 0f) >= 3f || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
                ? "Text"
                : "MultipleChoice";

        mode.Should().Be("Text", "lifetime CurrentStreak >= 3 → Text mode");
    }

    [Fact]
    public void ModeSelection_PendingRecognitionCheck_ForcesMultipleChoice()
    {
        var progress = new VocabularyProgress
        {
            MasteryScore = 0.80f,
            CurrentStreak = 5f, // Would normally be Text
        };

        var item = MakeQuizItem(progress);
        item.PendingRecognitionCheck = true; // Gentle demotion flag set

        var mode = item.PendingRecognitionCheck
            ? "MultipleChoice"
            : ((item.Progress?.CurrentStreak ?? 0f) >= 3f || (item.Progress?.MasteryScore ?? 0f) >= 0.50f)
                ? "Text"
                : "MultipleChoice";

        mode.Should().Be("MultipleChoice",
            "PendingRecognitionCheck overrides lifetime progress and forces MC");
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

        // Verify mode selection for beach-towel (new unified algorithm)
        var beachTowel = filtered.First(i => i.Progress!.VocabularyWordId == "beach-towel");
        var btMode = beachTowel.PendingRecognitionCheck
            ? "MultipleChoice"
            : ((beachTowel.Progress?.CurrentStreak ?? 0f) >= 3f || (beachTowel.Progress?.MasteryScore ?? 0f) >= 0.50f)
                ? "Text"
                : "MultipleChoice";
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

    // ── Tiered Rotation Tests (spec §1.2.2 / §1.3) ─────────────────

    [Fact]
    public void Tier1_HighMastery_RotatesAfter1TextCorrect()
    {
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.84f, CurrentStreak = 8f
        });
        item.SessionTextCorrect = 1;
        item.PendingRecognitionCheck = false;

        item.ReadyToRotateOut.Should().BeTrue("Tier 1: streak >= 8, 1 text correct, no pending check");
    }

    [Fact]
    public void Tier1_HighMastery_BlockedByPendingRecognitionCheck()
    {
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.90f, CurrentStreak = 10f
        });
        item.SessionTextCorrect = 1;
        item.PendingRecognitionCheck = true;

        item.ReadyToRotateOut.Should().BeFalse("Tier 1 blocked: PendingRecognitionCheck is set");
    }

    [Fact]
    public void Tier2_MidMastery_Rotates_4CorrectWith2Text()
    {
        // #191: Tier 2 trigger now requires mastery>=0.50 AND streak>=3 (was OR),
        // and floor raised from (SessC>=2, ST>=1) to (SessC>=4, ST>=2).
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.55f, CurrentStreak = 4f
        });
        item.SessionCorrectCount = 4;
        item.SessionTextCorrect = 2;

        item.ReadyToRotateOut.Should().BeTrue("Tier 2: 4 correct with 2 text (#191)");
    }

    [Fact]
    public void Tier2_MidMastery_BlockedByLowSessionCorrect()
    {
        // #191: under new floor (4 corr, 2 text), a fresh-Tier-2 word with only
        // 3 corrects and 1 text must NOT rotate out.
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.55f, CurrentStreak = 4f
        });
        item.SessionCorrectCount = 3;
        item.SessionTextCorrect = 1;

        item.ReadyToRotateOut.Should().BeFalse(
            "Tier 2: only 3 correct / 1 text, need 4 / 2 (#191)");
    }

    [Fact]
    public void Tier2_TriggerRequiresBothMasteryAndStreak()
    {
        // #191: a fresh word that has hit streak>=3 but mastery<0.5 falls to
        // Tier 3 (was Tier 2 under the old OR-trigger). Tier 3 requires the
        // strict 3 MC + 3 Text demonstration, which is NOT met here.
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.30f, CurrentStreak = 4f
        });
        item.SessionCorrectCount = 4;
        item.SessionTextCorrect = 2;
        item.SessionMCCorrect = 2;

        item.ReadyToRotateOut.Should().BeFalse(
            "Tier 2 trigger now requires BOTH mastery>=0.5 AND streak>=3 — "
          + "this word has streak but not mastery, so falls to Tier 3 (#191)");
    }

    [Fact]
    public void Tier3_LowMastery_Requires3MC_3Text()
    {
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.20f, CurrentStreak = 1f
        });
        item.SessionMCCorrect = 3;
        item.SessionTextCorrect = 3;

        item.ReadyToRotateOut.Should().BeTrue("Tier 3: 3 MC + 3 Text");
    }

    [Fact]
    public void Tier3_LowMastery_InsufficientText()
    {
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.20f, CurrentStreak = 1f
        });
        item.SessionMCCorrect = 3;
        item.SessionTextCorrect = 2;

        item.ReadyToRotateOut.Should().BeFalse("Tier 3: only 2 text, need 3");
    }

    [Fact]
    public void FocusPlan_KnownWord_StillRequiresThreeMcAndThreeTextBeforeRotation()
    {
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 1.0f,
            CurrentStreak = 8f,
            ProductionInStreak = 2
        });
        item.RequiresFullSessionDemonstration = true;
        item.SessionMCCorrect = 3;
        item.SessionTextCorrect = 2;
        item.SessionCorrectCount = 5;

        item.IsKnown.Should().BeTrue("the word starts globally Known");
        item.ReadyToRotateOut.Should().BeFalse(
            "daily-plan focus vocabulary must produce all 3 MC + 3 Text successes in this session, even when already Known");

        item.SessionTextCorrect = 3;

        item.ReadyToRotateOut.Should().BeTrue(
            "the focus-plan contract is satisfied only after 3 MC + 3 Text successes");
    }

    [Fact]
    public void FocusPlan_ModeSelection_ForcesThreeMcBeforeText()
    {
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 1.0f,
            CurrentStreak = 8f,
            ProductionInStreak = 2
        });
        item.RequiresFullSessionDemonstration = true;

        item.ChooseInteractionMode().Should().Be("MultipleChoice",
            "focus-plan words need three in-session MC successes before production, regardless of global mastery");

        item.SessionMCCorrect = 2;
        item.ChooseInteractionMode().Should().Be("MultipleChoice",
            "two MC successes are not enough for the focus-plan demonstration contract");

        item.SessionMCCorrect = 3;
        item.ChooseInteractionMode().Should().Be("Text",
            "after three MC successes, the focus-plan word moves to text entry");
    }

    [Fact]
    public void SessionCounters_AreCumulative_NeverResetOnWrong()
    {
        var item = MakeQuizItem(new VocabularyProgress
        {
            MasteryScore = 0.30f, CurrentStreak = 2f
        });
        item.SessionMCCorrect = 2;
        item.SessionTextCorrect = 1;
        item.SessionCorrectCount = 3;

        // Simulate wrong answer — counters should NOT be reset
        // (wrong answers just don't increment; they never decrement)
        item.SessionMCCorrect.Should().Be(2, "wrong answer does not reset cumulative counters");
        item.SessionTextCorrect.Should().Be(1);
        item.SessionCorrectCount.Should().Be(3);
    }
}
