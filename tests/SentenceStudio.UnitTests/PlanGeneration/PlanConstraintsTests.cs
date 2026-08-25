using Xunit;
using FluentAssertions;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Bounds and shape tests for <see cref="PlanConstraints"/>. These are the
/// only constraint dimensions the model may populate; anything outside the
/// documented ranges must be rejected before it reaches the planner.
/// </summary>
public class PlanConstraintsValidationTests
{
    [Fact]
    public void DefaultConstraints_AreValid_AndPermitEveryModality()
    {
        var constraints = new PlanConstraints();

        constraints.TryValidate(out var errors).Should().BeTrue();
        errors.Should().BeEmpty();
        constraints.AudioAllowed.Should().BeTrue();
        constraints.SpeechAllowed.Should().BeTrue();
        constraints.TypingAllowed.Should().BeTrue();
        constraints.EnergyLevel.Should().Be(PlanEnergyLevel.Normal);
        constraints.AvailableMinutes.Should().BeNull();
        constraints.SkillEmphasis.Should().BeNull();
        constraints.GoalTag.Should().BeNull();
        constraints.GoalHorizonDays.Should().BeNull();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(45)]
    [InlineData(90)]
    public void AvailableMinutes_WithinBounds_IsValid(int minutes)
    {
        new PlanConstraints { AvailableMinutes = minutes }
            .TryValidate(out var errors).Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(91)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public void AvailableMinutes_OutsideBounds_IsRejected(int minutes)
    {
        new PlanConstraints { AvailableMinutes = minutes }
            .TryValidate(out var errors).Should().BeFalse();
        errors.Should().ContainSingle()
            .Which.Should().Contain(nameof(PlanConstraints.AvailableMinutes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(180)]
    public void GoalHorizonDays_WithinBounds_IsValid(int days)
    {
        new PlanConstraints { GoalHorizonDays = days }
            .TryValidate(out var errors).Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(181)]
    public void GoalHorizonDays_OutsideBounds_IsRejected(int days)
    {
        new PlanConstraints { GoalHorizonDays = days }
            .TryValidate(out var errors).Should().BeFalse();
        errors.Should().ContainSingle()
            .Which.Should().Contain(nameof(PlanConstraints.GoalHorizonDays));
    }

    [Fact]
    public void UndefinedEnumValues_AreRejected()
    {
        new PlanConstraints { SkillEmphasis = (PlanSkillEmphasis)99 }
            .TryValidate(out var emphasisErrors).Should().BeFalse();
        emphasisErrors.Should().ContainSingle()
            .Which.Should().Contain(nameof(PlanConstraints.SkillEmphasis));

        new PlanConstraints { EnergyLevel = (PlanEnergyLevel)42 }
            .TryValidate(out var energyErrors).Should().BeFalse();
        energyErrors.Should().ContainSingle()
            .Which.Should().Contain(nameof(PlanConstraints.EnergyLevel));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GoalTag_BlankButNotNull_IsRejected(string tag)
    {
        new PlanConstraints { GoalTag = tag }
            .TryValidate(out var errors).Should().BeFalse();
        errors.Should().ContainSingle()
            .Which.Should().Contain(nameof(PlanConstraints.GoalTag));
    }

    [Fact]
    public void MultipleInvalidFields_AreAllReported()
    {
        new PlanConstraints { AvailableMinutes = 200, GoalHorizonDays = 400 }
            .TryValidate(out var errors).Should().BeFalse();
        errors.Should().HaveCount(2);
    }

    [Fact]
    public void HasAnyProductionModality_IsFalseOnlyWhenSpeechAndTypingBothDisallowed()
    {
        new PlanConstraints().HasAnyProductionModality.Should().BeTrue();
        new PlanConstraints { TypingAllowed = false }.HasAnyProductionModality.Should().BeTrue();
        new PlanConstraints { SpeechAllowed = false }.HasAnyProductionModality.Should().BeTrue();
        new PlanConstraints { SpeechAllowed = false, TypingAllowed = false }
            .HasAnyProductionModality.Should().BeFalse();
    }

    [Fact]
    public void PlanBuildRequest_DefaultsToWritesAllowedAndNoConstraints()
    {
        var request = new PlanBuildRequest { UserProfileId = "user-1" };

        request.AllowWrites.Should().BeTrue();
        request.Constraints.Should().BeNull();
    }

    [Fact]
    public void PlanBuildRequest_Preview_SuppressesWrites()
    {
        var request = PlanBuildRequest.Preview("user-1", new PlanConstraints { AvailableMinutes = 10 });

        request.AllowWrites.Should().BeFalse();
        request.UserProfileId.Should().Be("user-1");
        request.Constraints!.AvailableMinutes.Should().Be(10);
    }
}

/// <summary>
/// Pins the explicit activity modality classification. These assertions are the
/// contract the Learning Coach relies on: a learner who says "no audio" must
/// never be handed an activity whose only target-language channel is audio,
/// and must never lose an activity they could still complete.
/// </summary>
public class PlanActivityModalityTests
{
    [Theory]
    [InlineData(PlanActivityType.Listening)]
    [InlineData(PlanActivityType.VideoWatching)]
    [InlineData(PlanActivityType.Shadowing)]
    public void AudioRequiredActivities_AreExcludedWhenAudioDisallowed(PlanActivityType activityType)
    {
        PlanActivityModality.RequiresAudio(activityType).Should().BeTrue();
        PlanActivityModality.IsAllowed(activityType, new PlanConstraints { AudioAllowed = false })
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(PlanActivityType.VocabularyReview)]
    [InlineData(PlanActivityType.Reading)]
    [InlineData(PlanActivityType.Cloze)]
    [InlineData(PlanActivityType.Translation)]
    [InlineData(PlanActivityType.Writing)]
    [InlineData(PlanActivityType.VocabularyGame)]
    [InlineData(PlanActivityType.NumberDrill)]
    [InlineData(PlanActivityType.SceneDescription)]
    [InlineData(PlanActivityType.Conversation)]
    public void NonAudioActivities_SurviveAudioDisallowed(PlanActivityType activityType)
    {
        PlanActivityModality.RequiresAudio(activityType).Should().BeFalse();
        PlanActivityModality.IsAllowed(activityType, new PlanConstraints { AudioAllowed = false })
            .Should().BeTrue();
    }

    [Fact]
    public void ShadowingIsTheOnlySpeechRequiredActivity()
    {
        foreach (var activityType in Enum.GetValues<PlanActivityType>())
        {
            PlanActivityModality.RequiresSpeech(activityType)
                .Should().Be(activityType == PlanActivityType.Shadowing,
                    $"{activityType} speech requirement must match the documented classification");
        }
    }

    [Theory]
    [InlineData(PlanActivityType.Writing)]
    [InlineData(PlanActivityType.SceneDescription)]
    [InlineData(PlanActivityType.Conversation)]
    public void FreeTextOnlyActivities_AreExcludedWhenTypingDisallowed(PlanActivityType activityType)
    {
        PlanActivityModality.RequiresTyping(activityType).Should().BeTrue();
        PlanActivityModality.IsAllowed(activityType, new PlanConstraints { TypingAllowed = false })
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(PlanActivityType.Cloze)]
    [InlineData(PlanActivityType.Translation)]
    [InlineData(PlanActivityType.VocabularyReview)]
    public void ActivitiesWithANonTypingResponsePath_SurviveTypingDisallowed(PlanActivityType activityType)
    {
        // Cloze and Translation both ship a MultipleChoice/blocks toggle, and
        // VocabQuiz defaults to MultipleChoice — none of them are typing-only,
        // so excluding them would shrink the session for no pedagogical reason.
        PlanActivityModality.RequiresTyping(activityType).Should().BeFalse();
        PlanActivityModality.IsAllowed(activityType, new PlanConstraints { TypingAllowed = false })
            .Should().BeTrue();
    }

    [Fact]
    public void NullConstraints_PermitEveryActivityType()
    {
        foreach (var activityType in Enum.GetValues<PlanActivityType>())
        {
            PlanActivityModality.IsAllowed(activityType, null).Should().BeTrue();
        }
    }

    [Fact]
    public void UnknownActivityName_FailsOpen()
    {
        PlanActivityModality
            .IsAllowed("SomeFutureActivity", new PlanConstraints { AudioAllowed = false })
            .Should().BeTrue("an unclassified activity must not silently vanish from every constrained plan");
    }

    [Fact]
    public void StringOverload_MatchesEnumOverload_ForEveryActivityType()
    {
        var constraints = new PlanConstraints { AudioAllowed = false, SpeechAllowed = false, TypingAllowed = false };

        foreach (var activityType in Enum.GetValues<PlanActivityType>())
        {
            PlanActivityModality.IsAllowed(activityType.ToString(), constraints)
                .Should().Be(PlanActivityModality.IsAllowed(activityType, constraints));
        }
    }

    [Theory]
    [InlineData(PlanSkillEmphasis.Listening, PlanActivityType.Listening)]
    [InlineData(PlanSkillEmphasis.Speaking, PlanActivityType.Shadowing)]
    [InlineData(PlanSkillEmphasis.Reading, PlanActivityType.Reading)]
    [InlineData(PlanSkillEmphasis.Writing, PlanActivityType.Writing)]
    [InlineData(PlanSkillEmphasis.Vocabulary, PlanActivityType.VocabularyReview)]
    public void EmphasisMatchesItsCoreActivity(PlanSkillEmphasis emphasis, PlanActivityType activityType)
    {
        PlanActivityModality.MatchesEmphasis(activityType, emphasis).Should().BeTrue();
    }

    [Fact]
    public void EmphasisNeverMatchesEveryActivity()
    {
        foreach (var emphasis in Enum.GetValues<PlanSkillEmphasis>())
        {
            Enum.GetValues<PlanActivityType>()
                .Any(a => !PlanActivityModality.MatchesEmphasis(a, emphasis))
                .Should().BeTrue($"{emphasis} must be a weighting hint, not a filter that matches everything");
        }
    }
}
