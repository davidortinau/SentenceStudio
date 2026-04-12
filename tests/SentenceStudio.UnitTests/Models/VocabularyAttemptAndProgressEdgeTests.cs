using Xunit;
using FluentAssertions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Models;

/// <summary>
/// Tests for VocabularyAttempt.Phase computed property.
/// Ensures the (InputMode, ContextType) → LearningPhase mapping
/// is correct across all combinations.
/// </summary>
public class VocabularyAttemptPhaseTests
{
    [Theory]
    [InlineData("MultipleChoice", null, LearningPhase.Recognition)]
    [InlineData("MultipleChoice", "Isolated", LearningPhase.Recognition)]
    [InlineData("MultipleChoice", "Sentence", LearningPhase.Recognition)]
    [InlineData("MultipleChoice", "Conjugated", LearningPhase.Recognition)]
    public void MultipleChoice_AlwaysRecognitionPhase(string inputMode, string? contextType, LearningPhase expected)
    {
        var attempt = new VocabularyAttempt
        {
            InputMode = inputMode,
            ContextType = contextType
        };
        attempt.Phase.Should().Be(expected);
    }

    [Theory]
    [InlineData("Text", "Isolated", LearningPhase.Production)]
    [InlineData("Text", "Sentence", LearningPhase.Production)]
    [InlineData("Text", null, LearningPhase.Production)]
    public void Text_WithIsolatedOrSentence_IsProduction(string inputMode, string? contextType, LearningPhase expected)
    {
        var attempt = new VocabularyAttempt { InputMode = inputMode, ContextType = contextType };
        attempt.Phase.Should().Be(expected);
    }

    [Fact]
    public void Text_WithConjugated_IsApplication()
    {
        var attempt = new VocabularyAttempt { InputMode = "Text", ContextType = "Conjugated" };
        attempt.Phase.Should().Be(LearningPhase.Application);
    }

    [Fact]
    public void Voice_IsProduction()
    {
        var attempt = new VocabularyAttempt { InputMode = "Voice", ContextType = "Isolated" };
        attempt.Phase.Should().Be(LearningPhase.Production);
    }

    [Fact]
    public void UnknownInputMode_DefaultsToRecognition()
    {
        var attempt = new VocabularyAttempt { InputMode = "Drawing", ContextType = "Isolated" };
        attempt.Phase.Should().Be(LearningPhase.Recognition);
    }

    [Fact]
    public void EmptyInputMode_DefaultsToRecognition()
    {
        var attempt = new VocabularyAttempt { InputMode = "", ContextType = null };
        attempt.Phase.Should().Be(LearningPhase.Recognition);
    }
}

/// <summary>
/// Additional edge-case tests for VocabularyProgress model
/// beyond the existing VocabularyProgressTests class.
/// </summary>
public class VocabularyProgressEdgeCaseTests
{
    #region IsKnown Boundary Tests

    [Fact]
    public void IsKnown_ExactBoundary_MasteryAt085_ProductionAt2_IsKnown()
    {
        var p = new VocabularyProgress { MasteryScore = 0.85f, ProductionInStreak = 2 };
        p.IsKnown.Should().BeTrue("exact boundary values should satisfy IsKnown");
    }

    [Fact]
    public void IsKnown_JustBelowMastery_IsNotKnown()
    {
        var p = new VocabularyProgress { MasteryScore = 0.849f, ProductionInStreak = 5 };
        p.IsKnown.Should().BeFalse("0.849 < 0.85 threshold");
    }

    [Fact]
    public void IsKnown_JustBelowProduction_IsNotKnown()
    {
        var p = new VocabularyProgress { MasteryScore = 1.0f, ProductionInStreak = 1 };
        p.IsKnown.Should().BeFalse("needs at least 2 production in streak");
    }

    [Fact]
    public void IsKnown_ZeroMastery_HighProduction_IsNotKnown()
    {
        var p = new VocabularyProgress { MasteryScore = 0f, ProductionInStreak = 10 };
        p.IsKnown.Should().BeFalse("zero mastery is not known regardless of production");
    }

    #endregion

    #region EffectiveStreak Calculation

    [Fact]
    public void EffectiveStreak_AllRecognition()
    {
        var p = new VocabularyProgress { CurrentStreak = 5, ProductionInStreak = 0 };
        p.EffectiveStreak.Should().Be(5.0f);
    }

    [Fact]
    public void EffectiveStreak_MixedMode()
    {
        var p = new VocabularyProgress { CurrentStreak = 5, ProductionInStreak = 3 };
        p.EffectiveStreak.Should().Be(6.5f); // 5 + 3*0.5
    }

    [Fact]
    public void EffectiveStreak_AllProduction()
    {
        // Every attempt was production (Text/Voice)
        var p = new VocabularyProgress { CurrentStreak = 4, ProductionInStreak = 4 };
        p.EffectiveStreak.Should().Be(6.0f); // 4 + 4*0.5
    }

    #endregion

    #region StreakToKnown and ProductionNeededForKnown

    [Fact]
    public void StreakToKnown_NewWord_Returns6()
    {
        var p = new VocabularyProgress();
        p.StreakToKnown.Should().Be(6, "need EffectiveStreak >= 6 for mastery ~0.857");
    }

    [Fact]
    public void StreakToKnown_PartialProgress()
    {
        var p = new VocabularyProgress { CurrentStreak = 3, ProductionInStreak = 1 };
        // EffectiveStreak = 3 + 0.5 = 3.5 → 6 - 3.5 = 2.5, ceil = 3
        p.StreakToKnown.Should().Be(3);
    }

    [Fact]
    public void StreakToKnown_AlreadyKnown_Returns0()
    {
        var p = new VocabularyProgress
        {
            MasteryScore = 0.90f,
            CurrentStreak = 8,
            ProductionInStreak = 3
        };
        // EffectiveStreak = 8 + 1.5 = 9.5 → 6 - 9.5 < 0, clamped to 0
        p.StreakToKnown.Should().Be(0);
    }

    [Fact]
    public void ProductionNeededForKnown_NoProductionYet()
    {
        var p = new VocabularyProgress { ProductionInStreak = 0 };
        p.ProductionNeededForKnown.Should().Be(2);
    }

    [Fact]
    public void ProductionNeededForKnown_OneProduction()
    {
        var p = new VocabularyProgress { ProductionInStreak = 1 };
        p.ProductionNeededForKnown.Should().Be(1);
    }

    [Fact]
    public void ProductionNeededForKnown_AlreadyMet()
    {
        var p = new VocabularyProgress { ProductionInStreak = 5 };
        p.ProductionNeededForKnown.Should().Be(0);
    }

    #endregion

    #region Status with UserDeclared

    [Fact]
    public void Status_UserDeclaredFamiliar_ReturnsFamiliar()
    {
        var p = new VocabularyProgress
        {
            IsUserDeclared = true,
            VerificationState = VerificationStatus.Pending,
            MasteryScore = 0.3f
        };
        p.Status.Should().Be(LearningStatus.Familiar);
        p.IsFamiliar.Should().BeTrue();
    }

    [Fact]
    public void Status_UserDeclaredButVerified_UsesAlgorithmicStatus()
    {
        var p = new VocabularyProgress
        {
            IsUserDeclared = true,
            VerificationState = VerificationStatus.Confirmed,
            MasteryScore = 0.3f
        };
        // Not Pending → falls through to algorithmic status
        p.Status.Should().Be(LearningStatus.Learning);
        p.IsFamiliar.Should().BeFalse();
    }

    [Fact]
    public void IsInGracePeriod_RecentDeclaration_IsTrue()
    {
        var p = new VocabularyProgress
        {
            IsUserDeclared = true,
            VerificationState = VerificationStatus.Pending,
            UserDeclaredAt = DateTime.Now.AddDays(-7)
        };
        p.IsInGracePeriod.Should().BeTrue("declared 7 days ago, within 14-day grace");
    }

    [Fact]
    public void IsInGracePeriod_OldDeclaration_IsFalse()
    {
        var p = new VocabularyProgress
        {
            IsUserDeclared = true,
            VerificationState = VerificationStatus.Pending,
            UserDeclaredAt = DateTime.Now.AddDays(-15)
        };
        p.IsInGracePeriod.Should().BeFalse("declared 15 days ago, past 14-day grace");
    }

    #endregion

    #region IsDueForReview Edge Cases

    [Fact]
    public void IsDueForReview_ExactlyNow_IsDue()
    {
        // NextReviewDate set to "now" — should be due (<=)
        var p = new VocabularyProgress { NextReviewDate = DateTime.Now };
        p.IsDueForReview.Should().BeTrue("review at exactly now should be due");
    }

    [Fact]
    public void IsDueForReview_OneSecondInFuture_NotDue()
    {
        var p = new VocabularyProgress { NextReviewDate = DateTime.Now.AddSeconds(30) };
        p.IsDueForReview.Should().BeFalse("30 seconds in future is not yet due");
    }

    #endregion

    #region IsLearning and IsUnknown

    [Fact]
    public void IsLearning_WithSomeMastery_NotKnown()
    {
        var p = new VocabularyProgress { MasteryScore = 0.5f, ProductionInStreak = 0 };
        p.IsLearning.Should().BeTrue();
        p.IsUnknown.Should().BeFalse();
    }

    [Fact]
    public void IsUnknown_ZeroMastery()
    {
        var p = new VocabularyProgress { MasteryScore = 0f };
        p.IsUnknown.Should().BeTrue();
        p.IsLearning.Should().BeFalse();
    }

    [Fact]
    public void KnownWord_IsNotLearning()
    {
        var p = new VocabularyProgress { MasteryScore = 0.90f, ProductionInStreak = 3 };
        p.IsKnown.Should().BeTrue();
        p.IsLearning.Should().BeFalse();
    }

    #endregion
}
