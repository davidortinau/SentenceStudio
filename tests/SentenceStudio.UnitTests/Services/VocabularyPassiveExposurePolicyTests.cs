using FluentAssertions;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class VocabularyPassiveExposurePolicyTests
{
    [Theory]
    [InlineData(LexicalUnitType.Word, true, null)]
    [InlineData(LexicalUnitType.Unknown, false, null)]
    [InlineData(LexicalUnitType.Phrase, true, "PhraseCascade:VocabularyQuiz")]
    [InlineData(LexicalUnitType.Phrase, false, "PhraseCascade:VocabularyQuiz:Incorrect")]
    [InlineData(LexicalUnitType.Sentence, true, "PhraseCascade:VocabularyQuiz")]
    [InlineData(LexicalUnitType.Sentence, false, "PhraseCascade:VocabularyQuiz:Incorrect")]
    public void ConstituentCascadeActivity_UsesSourceTypeAndCorrectness(
        LexicalUnitType lexicalType, bool wasCorrect, string? expected)
    {
        var attempt = new VocabularyAttempt
        {
            Activity = "VocabularyQuiz",
            WasCorrect = wasCorrect
        };

        VocabularyProgressService.CreateConstituentCascadeActivity(
            lexicalType, attempt).Should().Be(expected);
    }

    [Fact]
    public void PassiveExposureHelpers_ChangeOnlyExposureAndCreatePassiveContext()
    {
        var practicedAt = DateTime.Now.AddDays(-4);
        var exposedAt = DateTime.Now;
        var progress = new VocabularyProgress
        {
            Id = "owned-progress",
            MasteryScore = 0.7f,
            CurrentStreak = 3f,
            ProductionInStreak = 1,
            TotalAttempts = 5,
            LastPracticedAt = practicedAt,
            ExposureCount = 2
        };

        var context = VocabularyProgressService.CreatePassiveExposureContext(
            progress.Id, "PhraseCascade:VocabularyQuiz:Incorrect", exposedAt);
        VocabularyProgressService.ApplyPassiveExposure(progress, exposedAt);

        progress.ExposureCount.Should().Be(3);
        progress.LastExposedAt.Should().Be(exposedAt);
        progress.UpdatedAt.Should().Be(exposedAt);
        progress.LastPracticedAt.Should().Be(practicedAt);
        progress.TotalAttempts.Should().Be(5);
        progress.MasteryScore.Should().Be(0.7f);
        progress.CurrentStreak.Should().Be(3f);
        progress.ProductionInStreak.Should().Be(1);
        context.VocabularyProgressId.Should().Be(progress.Id);
        context.Activity.Should().Be("PhraseCascade:VocabularyQuiz:Incorrect");
        context.InputMode.Should().Be("Passive");
        context.ContextType.Should().Be("Exposure");
        context.WasCorrect.Should().BeTrue();
        context.DifficultyScore.Should().Be(0f);
        context.CorrectAnswersInContext.Should().Be(0);
        context.LearnedAt.Should().Be(exposedAt);
    }
}
