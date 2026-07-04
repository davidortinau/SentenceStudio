using FluentAssertions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Models;

public sealed class VocabQuizPersistentDemonstrationTests
{
    [Fact]
    public void FocusWord_RecognitionDemonstrationsPersistAcrossSessions_BeforeAndAfterThirdCorrect()
    {
        var item = MakeFocusItem(new VocabularyProgress
        {
            QuizRecognitionDemonstrations = 2,
            QuizProductionDemonstrations = 0,
            CurrentStreak = 0,
            MasteryScore = 0.10f
        });

        item.ChooseInteractionMode().Should().Be("MultipleChoice",
            "two persisted recognition demonstrations are still below the focus-word target");

        item.Progress!.QuizRecognitionDemonstrations = 3;

        item.ChooseInteractionMode().Should().Be("Text",
            "the third recognition demonstration in a later session must graduate the word to production");
    }

    [Fact]
    public void FocusWord_RecognitionGraduatesAtExactlyThree_NotFour()
    {
        MakeFocusItem(new VocabularyProgress { QuizRecognitionDemonstrations = 2 })
            .ChooseInteractionMode()
            .Should().Be("MultipleChoice", "two recognition demonstrations are not enough");

        MakeFocusItem(new VocabularyProgress { QuizRecognitionDemonstrations = 3 })
            .ChooseInteractionMode()
            .Should().Be("Text", "three recognition demonstrations are enough");
    }

    [Fact]
    public void FocusWord_RotationRequiresPersistentCountersAndSessionProductionFloor()
    {
        var belowProductionTarget = MakeFocusItem(new VocabularyProgress
        {
            QuizRecognitionDemonstrations = 3,
            QuizProductionDemonstrations = 2
        });
        belowProductionTarget.CaptureKnownWordShortcutBaseline();
        belowProductionTarget.SessionTextCorrect = 1;

        belowProductionTarget.ReadyToRotateOut.Should().BeFalse("production is still below the focus-word target");

        var atProductionTargetWithNoSessionProduction = MakeFocusItem(new VocabularyProgress
        {
            QuizRecognitionDemonstrations = 3,
            QuizProductionDemonstrations = 3
        });
        atProductionTargetWithNoSessionProduction.CaptureKnownWordShortcutBaseline();

        atProductionTargetWithNoSessionProduction.ReadyToRotateOut.Should().BeFalse(
            "banked demonstrations cannot evict a regressed focus word before in-session production practice");

        atProductionTargetWithNoSessionProduction.SessionTextCorrect = 1;

        atProductionTargetWithNoSessionProduction.ReadyToRotateOut.Should().BeTrue(
            "banked demonstrations still require one production answer in this session");

        var belowRecognitionTarget = MakeFocusItem(new VocabularyProgress
        {
            QuizRecognitionDemonstrations = 2,
            QuizProductionDemonstrations = 3
        });
        belowRecognitionTarget.CaptureKnownWordShortcutBaseline();
        belowRecognitionTarget.SessionTextCorrect = 1;

        belowRecognitionTarget.ReadyToRotateOut.Should().BeFalse("recognition and production are both required");
    }

    [Fact]
    public void FocusWord_WithTwoBankedProductionDemonstrations_NeedsOneInSessionProduction()
    {
        var item = MakeFocusItem(new VocabularyProgress
        {
            QuizRecognitionDemonstrations = 3,
            QuizProductionDemonstrations = 2
        });
        item.CaptureKnownWordShortcutBaseline();

        item.Progress!.QuizProductionDemonstrations = 3;

        item.ReadyToRotateOut.Should().BeFalse("the third production demonstration must be earned in this session");

        item.SessionTextCorrect = 1;

        item.ReadyToRotateOut.Should().BeTrue(
            "baseline 2 plus one in-session production reaches the cumulative target and satisfies the session floor");
    }

    [Fact]
    public void FocusWord_WithBankedThreeAndThree_DoesNotRotateBeforeBeingShown()
    {
        var item = MakeFocusItem(new VocabularyProgress
        {
            QuizRecognitionDemonstrations = 3,
            QuizProductionDemonstrations = 3,
            CurrentStreak = 0,
            MasteryScore = 0.10f,
            IsUserDeclared = false
        });
        item.CaptureKnownWordShortcutBaseline();

        item.ReadyToRotateOut.Should().BeFalse(
            "a regressed focus word with lifetime 3/3 still needs production practice in this resumed session");

        item.SessionTextCorrect = 1;

        item.ReadyToRotateOut.Should().BeTrue("one in-session production answer satisfies the floor");
    }

    [Fact]
    public void FocusWord_PendingRecognitionCheckBlocksRotation_EvenAtPersistentTargets()
    {
        var item = MakeFocusItem(new VocabularyProgress
        {
            QuizRecognitionDemonstrations = 3,
            QuizProductionDemonstrations = 3
        });
        item.CaptureKnownWordShortcutBaseline();
        item.SessionTextCorrect = 1;
        item.PendingRecognitionCheck = true;

        item.ReadyToRotateOut.Should().BeFalse(
            "a pending recognition check must be cleared before rotation");
    }

    [Fact]
    public void FocusWord_KnownWordShortcut_FromCurrentStreakSkipsRecognitionAndNeedsOneSessionProduction()
    {
        var item = MakeFocusItem(new VocabularyProgress
        {
            CurrentStreak = 4,
            MasteryScore = 0.47f,
            QuizRecognitionDemonstrations = 0,
            QuizProductionDemonstrations = 0
        });

        item.CaptureKnownWordShortcutBaseline();

        item.UseKnownWordShortcut.Should().BeTrue("CurrentStreak >= 3 qualifies at session start");
        item.ChooseInteractionMode().Should().Be("Text", "known-word shortcut skips the recognition grind");
        item.ReadyToRotateOut.Should().BeFalse("shortcut words still need one production answer this session");

        item.SessionTextCorrect = 1;

        item.ReadyToRotateOut.Should().BeTrue(
            "shortcut words rotate after one production confirmation in the current session");
    }

    [Theory]
    [InlineData(0.85f, 2, 0)]
    [InlineData(0.50f, 0, 0)]
    public void FocusWord_KnownWordShortcut_CanBeTriggeredByIsKnownOrMasteryThreshold(
        float masteryScore,
        int productionInStreak,
        int currentStreak)
    {
        var item = MakeFocusItem(new VocabularyProgress
        {
            MasteryScore = masteryScore,
            ProductionInStreak = productionInStreak,
            CurrentStreak = currentStreak
        });

        item.CaptureKnownWordShortcutBaseline();

        item.UseKnownWordShortcut.Should().BeTrue();
        item.ChooseInteractionMode().Should().Be("Text");
    }

    [Fact]
    public void NonFocusWord_IgnoresPersistentQuizCountersAndUsesLegacyStreakMasteryModeRule()
    {
        var item = MakeNonFocusItem(new VocabularyProgress
        {
            CurrentStreak = 0,
            MasteryScore = 0.10f,
            QuizRecognitionDemonstrations = 3,
            QuizProductionDemonstrations = 3
        });

        item.ChooseInteractionMode().Should().Be("MultipleChoice",
            "non-focus mode selection is still driven by CurrentStreak/MasteryScore, not persistent quiz counters");
    }

    private static VocabularyQuizItem MakeFocusItem(VocabularyProgress progress)
    {
        progress.VocabularyWordId = string.IsNullOrEmpty(progress.VocabularyWordId)
            ? "focus-word"
            : progress.VocabularyWordId;

        return new VocabularyQuizItem
        {
            Word = new VocabularyWord
            {
                Id = progress.VocabularyWordId,
                TargetLanguageTerm = "focus",
                NativeLanguageTerm = "focus"
            },
            Progress = progress,
            RequiresFullSessionDemonstration = true
        };
    }

    private static VocabularyQuizItem MakeNonFocusItem(VocabularyProgress progress)
    {
        progress.VocabularyWordId = string.IsNullOrEmpty(progress.VocabularyWordId)
            ? "non-focus-word"
            : progress.VocabularyWordId;

        return new VocabularyQuizItem
        {
            Word = new VocabularyWord
            {
                Id = progress.VocabularyWordId,
                TargetLanguageTerm = "non-focus",
                NativeLanguageTerm = "non-focus"
            },
            Progress = progress,
            RequiresFullSessionDemonstration = false
        };
    }
}
