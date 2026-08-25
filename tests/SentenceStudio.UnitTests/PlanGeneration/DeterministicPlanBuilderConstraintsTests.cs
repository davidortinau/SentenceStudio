using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Behavior tests for <see cref="PlanConstraints"/> flowing through
/// <see cref="DeterministicPlanBuilder"/>. Read
/// <c>DeterministicPlanBuilderCharacterizationTests</c> first — it pins the
/// unconstrained baseline these tests deviate from.
/// </summary>
public class DeterministicPlanBuilderConstraintsTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private static readonly HashSet<string> InputTypes = new() { "Reading", "Listening", "VideoWatching" };
    private static readonly HashSet<string> OutputTypes = new() { "Translation", "Cloze", "Writing", "Shadowing" };

    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderConstraintsTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose() { }

    /// <summary>Podcast with a transcript: audio input, text input, and every output candidate.</summary>
    private string SeedAudioAndTranscriptResource(int sessionMinutes = 60, int vocabWordCount = 0)
    {
        _fixture.SeedUserProfile(sessionMinutes);
        var resource = _fixture.SeedResource(
            title: "Constraint Resource",
            mediaType: "Podcast",
            transcript: "Transcript text",
            vocabWordCount: vocabWordCount);
        _fixture.SeedSkill();
        return resource.Id;
    }

    private void SeedDueVocabulary(string resourceId)
    {
        foreach (var wordId in _fixture.GetResourceVocabularyWordIds(resourceId))
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resourceId);
        }
    }

    /// <summary>
    /// Biases the least-recently-used ordering so the named activities lose the
    /// selection race. Completions are dated two and three days back — never
    /// yesterday — so only the recency counter moves and the separate
    /// "not yesterday's activity" filter stays out of the way.
    /// </summary>
    /// <remarks>
    /// Without this bias a modality test can pass by accident: the deterministic
    /// per-day hash tiebreaker might already pick the allowed activity, so
    /// deleting the constraint filter would not fail the test. Biasing makes the
    /// excluded activity the one the planner would otherwise choose.
    /// </remarks>
    private void DisfavorActivities(params string[] activityTypes)
    {
        var today = DateTime.UtcNow.Date;
        foreach (var activityType in activityTypes)
        {
            _fixture.SeedCompletion(today.AddDays(-2), activityType);
            _fixture.SeedCompletion(today.AddDays(-3), activityType);
        }
    }

    private Task<PlanSkeleton?> BuildAsync(PlanConstraints? constraints) =>
        _fixture.CreateBuilder().BuildPlanAsync(new PlanBuildRequest
        {
            UserProfileId = PlanGenerationTestFixture.TestUserId,
            Constraints = constraints
        });

    // ---------------------------------------------------------------- budget

    [Fact]
    public async Task AvailableMinutes_ClampsTheBudgetBelowTheProfilePreference()
    {
        SeedAudioAndTranscriptResource(sessionMinutes: 60);

        var plan = await BuildAsync(new PlanConstraints { AvailableMinutes = 20 });

        plan.Should().NotBeNull();
        plan!.TotalMinutes.Should().BeLessThanOrEqualTo(20,
            "AvailableMinutes is authoritative over PreferredSessionMinutes");
        plan.Activities.Sum(a => a.EstimatedMinutes).Should().Be(plan.TotalMinutes);
    }

    [Fact]
    public async Task AvailableMinutes_IsAuthoritativeAboveTheProfilePreference()
    {
        SeedAudioAndTranscriptResource(sessionMinutes: 10);

        var plan = await BuildAsync(new PlanConstraints { AvailableMinutes = 40 });

        plan.Should().NotBeNull();
        plan!.TotalMinutes.Should().BeGreaterThan(10,
            "a learner who says they have more time gets a larger session than their stored preference");
        plan.TotalMinutes.Should().BeLessThanOrEqualTo(40);
    }

    // -------------------------------------------------------------- modality

    [Fact]
    public async Task AudioDisallowed_ExcludesEveryAudioRequiredActivity()
    {
        SeedAudioAndTranscriptResource();
        // Make the non-audio input the LEAST preferred candidate, so an
        // unfiltered planner would pick Listening.
        DisfavorActivities("Reading");

        var unconstrained = await BuildAsync(null);
        unconstrained!.Activities.Should().Contain(a => a.ActivityType == "Listening",
            "guard: without constraints this scenario selects the audio input");

        var plan = await BuildAsync(new PlanConstraints { AudioAllowed = false });

        plan.Should().NotBeNull();
        plan!.Activities.Should().NotContain(a =>
            a.ActivityType == "Listening" || a.ActivityType == "VideoWatching" || a.ActivityType == "Shadowing");
        plan.Activities.Where(a => InputTypes.Contains(a.ActivityType))
            .Should().ContainSingle()
            .Which.ActivityType.Should().Be("Reading",
                "Reading survives because the resource has a transcript");
    }

    [Fact]
    public async Task SpeechDisallowed_ExcludesShadowingButKeepsOtherOutput()
    {
        SeedAudioAndTranscriptResource();
        // Make Shadowing the candidate an unfiltered planner would pick.
        DisfavorActivities("Translation", "Cloze", "Writing");

        var unconstrained = await BuildAsync(null);
        unconstrained!.Activities.Should().Contain(a => a.ActivityType == "Shadowing",
            "guard: without constraints this scenario selects the speech-required output");

        var plan = await BuildAsync(new PlanConstraints { SpeechAllowed = false });

        plan.Should().NotBeNull();
        plan!.Activities.Should().NotContain(a => a.ActivityType == "Shadowing");
        plan.Activities.Should().Contain(a => OutputTypes.Contains(a.ActivityType),
            "removing the speech-required activity must not remove production practice entirely");
    }

    [Fact]
    public async Task SpeechDisallowed_DoesNotRemoveAudioInput()
    {
        SeedAudioAndTranscriptResource();
        DisfavorActivities("Reading");

        var plan = await BuildAsync(new PlanConstraints { SpeechAllowed = false });

        plan.Should().NotBeNull();
        plan!.Activities.Where(a => InputTypes.Contains(a.ActivityType))
            .Should().ContainSingle()
            .Which.ActivityType.Should().Be("Listening",
                "listening is not speaking — a speech constraint must not touch the input block");
    }

    [Fact]
    public async Task TypingDisallowed_ExcludesWritingButKeepsChoiceBackedOutput()
    {
        SeedAudioAndTranscriptResource();
        // Make Writing the candidate an unfiltered planner would pick.
        DisfavorActivities("Translation", "Cloze", "Shadowing");

        var unconstrained = await BuildAsync(null);
        unconstrained!.Activities.Should().Contain(a => a.ActivityType == "Writing",
            "guard: without constraints this scenario selects the typing-required output");

        var plan = await BuildAsync(new PlanConstraints { TypingAllowed = false });

        plan.Should().NotBeNull();
        plan!.Activities.Should().NotContain(a => a.ActivityType == "Writing");
        plan.Activities.Should().Contain(a => OutputTypes.Contains(a.ActivityType),
            "Cloze, Translation, and Shadowing all have a non-typing response path");
    }

    [Fact]
    public async Task AllModalitiesDisallowed_StillProducesARecognitionOnlyPlan()
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 40, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);
        DisfavorActivities("Reading", "Translation", "Cloze");

        var restrictive = new PlanConstraints
        {
            AudioAllowed = false,
            SpeechAllowed = false,
            TypingAllowed = false
        };

        var unconstrained = await BuildAsync(null);
        unconstrained!.Activities.Should().Contain(a => !PlanActivityModality.IsAllowed(a.ActivityType, restrictive),
            "guard: without constraints this scenario selects at least one modality-blocked activity");

        var plan = await BuildAsync(restrictive);

        plan.Should().NotBeNull();
        plan!.Activities.Should().NotBeEmpty();
        plan.Activities.Should().OnlyContain(a => PlanActivityModality.IsAllowed(a.ActivityType, restrictive));
    }

    // ---------------------------------------------------------- combinations

    [Fact]
    public async Task EightMinutesAndNoAudio_ProducesASingleTextInputBlockWithinBudget()
    {
        SeedAudioAndTranscriptResource(sessionMinutes: 60);
        DisfavorActivities("Reading");

        var plan = await BuildAsync(new PlanConstraints { AvailableMinutes = 8, AudioAllowed = false });

        plan.Should().NotBeNull();
        plan!.TotalMinutes.Should().BeLessThanOrEqualTo(8);
        plan.Activities.Should().ContainSingle();
        plan.Activities[0].ActivityType.Should().Be("Reading",
            "an 8-minute budget affords exactly one block, and audio input is excluded");
    }

    [Fact]
    public async Task EightMinutesAndNoAudio_WithDueVocabulary_KeepsTheReviewBlock()
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 60, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);

        var plan = await BuildAsync(new PlanConstraints { AvailableMinutes = 8, AudioAllowed = false });

        plan.Should().NotBeNull();
        plan!.TotalMinutes.Should().BeLessThanOrEqualTo(8);
        plan.Activities.OrderBy(a => a.Priority).First().ActivityType.Should().Be("VocabularyReview",
            "a tight budget shrinks the session but never drops due review");
    }

    // -------------------------------------------------------------- emphasis

    [Theory]
    [InlineData(PlanSkillEmphasis.Reading, "Reading")]
    [InlineData(PlanSkillEmphasis.Listening, "Listening")]
    public async Task SkillEmphasis_ReweightsTheInputBlock(PlanSkillEmphasis emphasis, string expected)
    {
        SeedAudioAndTranscriptResource();
        // Bias recency AGAINST the emphasized activity: emphasis is the primary
        // sort key, so it must still win.
        DisfavorActivities(expected);

        var plan = await BuildAsync(new PlanConstraints { SkillEmphasis = emphasis });

        plan.Should().NotBeNull();
        plan!.Activities.Where(a => InputTypes.Contains(a.ActivityType))
            .Should().ContainSingle()
            .Which.ActivityType.Should().Be(expected);
    }

    [Theory]
    [InlineData(PlanSkillEmphasis.Speaking, "Shadowing")]
    [InlineData(PlanSkillEmphasis.Vocabulary, "Cloze")]
    public async Task SkillEmphasis_ReweightsTheOutputBlock(PlanSkillEmphasis emphasis, string expected)
    {
        SeedAudioAndTranscriptResource();
        DisfavorActivities(expected);

        var plan = await BuildAsync(new PlanConstraints { SkillEmphasis = emphasis });

        plan.Should().NotBeNull();
        plan!.Activities.Where(a => OutputTypes.Contains(a.ActivityType))
            .Should().ContainSingle()
            .Which.ActivityType.Should().Be(expected);
    }

    [Theory]
    [InlineData(PlanSkillEmphasis.Reading)]
    [InlineData(PlanSkillEmphasis.Listening)]
    [InlineData(PlanSkillEmphasis.Speaking)]
    [InlineData(PlanSkillEmphasis.Writing)]
    [InlineData(PlanSkillEmphasis.Vocabulary)]
    public async Task SkillEmphasis_NeverRemovesDueVocabularyReview(PlanSkillEmphasis emphasis)
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 40, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);

        var plan = await BuildAsync(new PlanConstraints { SkillEmphasis = emphasis });

        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();
        plan.Activities.OrderBy(a => a.Priority).First().ActivityType.Should().Be("VocabularyReview",
            "emphasis is a weighting hint and can never displace due review");
    }

    [Fact]
    public async Task SkillEmphasis_CannotEmptyThePlan()
    {
        SeedAudioAndTranscriptResource();

        // Writing emphasis on a resource whose input candidates are only
        // Listening and Reading: emphasis matches nothing in the input set, so
        // it must reorder nothing away.
        var plan = await BuildAsync(new PlanConstraints { SkillEmphasis = PlanSkillEmphasis.Writing });

        plan.Should().NotBeNull();
        plan!.Activities.Should().Contain(a => InputTypes.Contains(a.ActivityType));
        plan.Activities.Should().Contain(a => OutputTypes.Contains(a.ActivityType));
    }

    // ---------------------------------------------------------------- energy

    [Fact]
    public async Task LowEnergy_ShortensTheSessionWithoutLoweringDifficulty()
    {
        SeedAudioAndTranscriptResource(sessionMinutes: 60);

        var normal = await BuildAsync(new PlanConstraints { EnergyLevel = PlanEnergyLevel.Normal });
        var low = await BuildAsync(new PlanConstraints { EnergyLevel = PlanEnergyLevel.Low });

        normal.Should().NotBeNull();
        low.Should().NotBeNull();

        low!.TotalMinutes.Should().BeLessThan(normal!.TotalMinutes, "low energy shortens the session");
        low.Activities.Should().Contain(a => OutputTypes.Contains(a.ActivityType),
            "low energy must not drop the production block — that would lower the difficulty floor");
        low.Activities.Should().Contain(a => InputTypes.Contains(a.ActivityType));
    }

    [Fact]
    public async Task LowEnergy_UsesShorterBlocksAndSkipsTheLightCloser()
    {
        SeedAudioAndTranscriptResource(sessionMinutes: 60);

        var plan = await BuildAsync(new PlanConstraints { EnergyLevel = PlanEnergyLevel.Low });

        plan.Should().NotBeNull();
        plan!.Activities
            .Where(a => InputTypes.Contains(a.ActivityType) || OutputTypes.Contains(a.ActivityType))
            .Should().OnlyContain(a => a.EstimatedMinutes <= 8);
        plan.Activities.Should().NotContain(a =>
            a.ActivityType == "VocabularyGame" || a.ActivityType == "NumberDrill");
    }

    [Fact]
    public async Task LowEnergy_KeepsDueVocabularyReview()
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 40, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);

        var plan = await BuildAsync(new PlanConstraints { EnergyLevel = PlanEnergyLevel.Low });

        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();
        plan.Activities.Should().Contain(a => a.ActivityType == "VocabularyReview");
    }

    // ------------------------------------------------------------ goal hints

    [Fact]
    public async Task GoalTagAndHorizon_AreMetadataOnly_AndDoNotChangeSelection()
    {
        SeedAudioAndTranscriptResource();

        var baseline = await BuildAsync(new PlanConstraints());
        var withGoal = await BuildAsync(new PlanConstraints
        {
            GoalTag = "travel",
            GoalHorizonDays = 30
        });

        baseline.Should().NotBeNull();
        withGoal.Should().NotBeNull();

        withGoal!.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes))
            .Should().Equal(baseline!.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes)),
                "goal metadata never selects plan items in this lane");
        withGoal.TotalMinutes.Should().Be(baseline.TotalMinutes);
    }

    // ------------------------------------------------------ invalid + no plan

    [Theory]
    [InlineData(2, null)]
    [InlineData(91, null)]
    [InlineData(null, 0)]
    [InlineData(null, 181)]
    public async Task InvalidConstraints_ReturnNoPlanInsteadOfAMalformedOne(int? minutes, int? horizonDays)
    {
        SeedAudioAndTranscriptResource();

        var plan = await BuildAsync(new PlanConstraints
        {
            AvailableMinutes = minutes,
            GoalHorizonDays = horizonDays
        });

        plan.Should().BeNull("out-of-range constraints are rejected before any plan is built");
    }

    [Fact]
    public async Task ConstrainedBuild_WithNoFeasibleBlock_ReturnsNullRatherThanAnEmptyPlan()
    {
        // 3 minutes with no due vocabulary: no input block (needs 8), no output
        // block (needs 8), no closer (needs 5). There is no feasible plan.
        SeedAudioAndTranscriptResource(sessionMinutes: 60);

        var plan = await BuildAsync(new PlanConstraints { AvailableMinutes = 3 });

        plan.Should().BeNull();
    }

    [Fact]
    public async Task ConstrainedBuild_WithThreeMinutesAndDueVocabulary_StillReviews()
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 60, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);

        var plan = await BuildAsync(new PlanConstraints { AvailableMinutes = 3 });

        plan.Should().NotBeNull();
        plan!.Activities.Should().ContainSingle()
            .Which.ActivityType.Should().Be("VocabularyReview");
        plan.TotalMinutes.Should().BeLessThanOrEqualTo(3);
    }

    // -------------------------------------------------------------- ordering

    [Fact]
    public async Task ConstrainedPlans_KeepDeterministicOrderAndValidPriorities()
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 60, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);

        var constraints = new PlanConstraints { AvailableMinutes = 30, AudioAllowed = false };

        var first = await BuildAsync(constraints);
        var second = await BuildAsync(constraints);

        first.Should().NotBeNull();
        second!.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes))
            .Should().Equal(first!.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes)),
                "constrained generation stays deterministic");

        first.Activities.Select(a => a.Priority).Should().BeInAscendingOrder();
        first.Activities.Select(a => a.Priority).Should().OnlyHaveUniqueItems();
        first.Activities.OrderBy(a => a.Priority).First().ActivityType.Should().Be("VocabularyReview");

        var inputPriority = first.Activities.Where(a => InputTypes.Contains(a.ActivityType)).Select(a => a.Priority).ToList();
        var outputPriority = first.Activities.Where(a => OutputTypes.Contains(a.ActivityType)).Select(a => a.Priority).ToList();
        if (inputPriority.Count > 0 && outputPriority.Count > 0)
        {
            inputPriority.Min().Should().BeLessThan(outputPriority.Min(),
                "input still precedes output under constraints");
        }
    }

    // --------------------------------------------------------- null-compat

    [Fact]
    public async Task NullConstraints_ViaRequest_MatchTheLegacyOverload()
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 40, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);

        var legacy = await _fixture.CreateBuilder().BuildPlanAsync(PlanGenerationTestFixture.TestUserId);
        var constrained = await BuildAsync(null);

        constrained.Should().NotBeNull();
        constrained!.TotalMinutes.Should().Be(legacy!.TotalMinutes);
        constrained.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes))
            .Should().Equal(legacy.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes)));
    }

    [Fact]
    public async Task DefaultAllPermissiveConstraints_MatchTheUnconstrainedPlan()
    {
        var resourceId = SeedAudioAndTranscriptResource(sessionMinutes: 40, vocabWordCount: 10);
        SeedDueVocabulary(resourceId);

        var unconstrained = await BuildAsync(null);
        var permissive = await BuildAsync(new PlanConstraints());

        permissive.Should().NotBeNull();
        permissive!.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes))
            .Should().Equal(unconstrained!.Activities.Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes)),
                "a constraints object with every default must be a no-op");
    }

    [Fact]
    public async Task NullRequest_Throws()
    {
        SeedAudioAndTranscriptResource();

        var act = async () => await _fixture.CreateBuilder().BuildPlanAsync((PlanBuildRequest)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
