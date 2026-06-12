// REGRESSION TESTS — "I was correct" override on a wrong text answer must NOT
// leave the word in PendingRecognitionCheck state, otherwise the SAME word
// cycles back as a forced MultipleChoice with the debug pane reading
// "Recognition check (wrong text answer earlier)". Captain's report 2026-06-12:
//
//   "I answered a text entry vocab quiz that was marked incorrect due to a
//    typo, so I marked it 'I was correct' twice. The next time that word
//    appeared it reverted back to multiple choice telling me I'd been
//    penalized. That should not have happened!"
//
// Root cause: VocabQuiz.razor's SubmitAnswer set PendingRecognitionCheck=true
// on the wrong-text branch (line 1227) as a "gentle demotion". OverrideAsCorrect
// only cleared PendingRecognitionCheck in the MultipleChoice branch; the Text
// branch left the flag set, so the next time the word appeared in the same
// session, the mode-selector forced MultipleChoice.
//
// Fix: clear PendingRecognitionCheck unconditionally inside
// VocabularyQuizItem.ApplyOverrideAsCorrect, which is the shared production
// method called by both VocabQuiz.razor's OverrideAsCorrect and these tests.
//
// If this test fails, the override regression is back. DO NOT DELETE OR WEAKEN.

using FluentAssertions;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.UnitTests.Integration;

public class VocabQuizOverrideRegressionTests
{
    /// <summary>
    /// Mirror of VocabQuiz.razor's mode-selector (lines 829-839):
    ///   PendingRecognitionCheck → MultipleChoice
    ///   CurrentStreak >= 3 OR MasteryScore >= 0.50 → Text
    ///   else → MultipleChoice
    /// </summary>
    private static string ChooseQuizModeForTurn(VocabularyQuizItem item)
    {
        if (item.PendingRecognitionCheck) return "MultipleChoice";
        var streak = item.Progress?.CurrentStreak ?? 0f;
        var mastery = item.Progress?.MasteryScore ?? 0f;
        return (streak >= 3f || mastery >= 0.50f) ? "Text" : "MultipleChoice";
    }

    /// <summary>
    /// Mirror the state mutations that VocabQuiz.razor SubmitAnswer performs
    /// for a wrong text answer. See VocabQuiz.razor lines 1223-1232.
    /// </summary>
    private static void ApplyWrongTextAnswer(VocabularyQuizItem item)
    {
        // Wrong text answer → gentle demotion: force MC next appearance.
        item.PendingRecognitionCheck = true;
        // Wrong answer does not increment any session-correct counter.
    }

    [Fact]
    public void Override_AfterWrongTextAnswer_ClearsPendingRecognitionCheck()
    {
        // Captain's scenario: word was already known well enough for Text mode
        // (MasteryScore = 0.6 puts the mode-selector into Text), user typoed,
        // grader scored wrong (sets PendingRecognitionCheck), user overrides
        // via "I was correct".
        var item = new VocabularyQuizItem
        {
            Word = new VocabularyWord { Id = "w1", TargetLanguageTerm = "사과" },
            Progress = new VocabularyProgress
            {
                VocabularyWordId = "w1",
                UserId = "captain",
                MasteryScore = 0.60f,
                CurrentStreak = 3f
            }
        };

        // Turn 1 — Text mode (mastery >= 0.50), wrong answer.
        ChooseQuizModeForTurn(item).Should().Be("Text", "starting mastery puts the user in Text mode");
        ApplyWrongTextAnswer(item);

        item.PendingRecognitionCheck.Should().BeTrue(
            "wrong text answer sets the gentle-demotion flag");

        // User overrides — calls the PRODUCTION method on VocabularyQuizItem.
        item.ApplyOverrideAsCorrect(userMode: "Text");

        // Post-condition: the flag must be cleared. Next-turn mode selection
        // for the SAME word must NOT force MultipleChoice.
        item.PendingRecognitionCheck.Should().BeFalse(
            "override means the system was wrong to set the demotion — flag must be cleared");

        ChooseQuizModeForTurn(item).Should().Be("Text",
            "after override, the same word must continue in Text mode — NOT revert to "
          + "MultipleChoice as a 'recognition check' for a wrong answer that the user "
          + "self-corrected. See vocab quiz override bug, 2026-06-12.");
    }

    [Fact]
    public void Override_AfterTwoConsecutiveWrongTextAnswers_StillClearsPendingRecognitionCheck()
    {
        // Captain explicitly said he overrode "twice" — make sure repeated
        // wrong-then-override cycles still leave the flag cleared.
        var item = new VocabularyQuizItem
        {
            Word = new VocabularyWord { Id = "w1", TargetLanguageTerm = "사과" },
            Progress = new VocabularyProgress
            {
                VocabularyWordId = "w1",
                UserId = "captain",
                MasteryScore = 0.60f,
                CurrentStreak = 3f
            }
        };

        // Cycle 1: wrong → override.
        ApplyWrongTextAnswer(item);
        item.ApplyOverrideAsCorrect(userMode: "Text");
        item.PendingRecognitionCheck.Should().BeFalse();

        // Cycle 2: wrong → override.
        ApplyWrongTextAnswer(item);
        item.ApplyOverrideAsCorrect(userMode: "Text");
        item.PendingRecognitionCheck.Should().BeFalse();

        // Next mode pick still respects mastery, not the prior wrong attempts.
        ChooseQuizModeForTurn(item).Should().Be("Text");

        // Session counters reflect two overrides.
        item.SessionCorrectCount.Should().Be(2);
        item.SessionTextCorrect.Should().Be(2);
    }

    [Fact]
    public void Override_AfterWrongMultipleChoiceAnswer_StillClearsPendingRecognitionCheck()
    {
        // Belt-and-suspenders for the pre-existing MC branch behavior: when in
        // MC mode, overriding must still clear the flag (the production code
        // already did this; this test pins the contract).
        var item = new VocabularyQuizItem
        {
            Word = new VocabularyWord { Id = "w1", TargetLanguageTerm = "사과" },
            Progress = new VocabularyProgress
            {
                VocabularyWordId = "w1",
                UserId = "captain"
            },
            PendingRecognitionCheck = true // simulate prior wrong text answer earlier in session
        };

        item.ApplyOverrideAsCorrect(userMode: "MultipleChoice");

        item.PendingRecognitionCheck.Should().BeFalse();
        item.SessionMCCorrect.Should().Be(1);
    }
}
