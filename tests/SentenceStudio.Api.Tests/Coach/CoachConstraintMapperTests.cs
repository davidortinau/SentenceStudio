using System.Reflection;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The choke point between what the model says and what the planner is asked to do.
/// </summary>
public class CoachConstraintMapperTests
{
    private readonly CoachConstraintMapper _mapper = new();

    [Fact]
    public void TheTurnIntentCarriesNoIdentityItemOrCommandFields()
    {
        // Structural guard. If someone adds a user id, a plan item id, or a free-form command
        // to the intent, the model gains a way to point the write somewhere else.
        var forbidden = new[]
        {
            "user", "profile", "tenant", "account", "email",
            "item", "planitem", "resource", "vocab", "word",
            "sql", "command", "script", "route", "url", "endpoint"
        };

        var names = typeof(CoachTurnIntent).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(CoachConstraintDeltaIntent).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name)
            .ToArray();

        // One documented exemption. The vocabulary focus description is the learner's own
        // wording — "active verbs" — not a selector: a controlled registry decides what it may
        // mean, and the server runs the query. The model still cannot name a word, an id, a
        // count, or a tag, which is what this guard exists to prevent.
        var exempt = new[] { "VocabularyFocusDescription", "ClearVocabularyFocus" };

        names.Where(n => !exempt.Contains(n, StringComparer.Ordinal))
            .Should().NotContain(n => forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));

        // The exemption stays a bounded description and never becomes a set of identifiers.
        typeof(CoachConstraintDeltaIntent).GetProperty("VocabularyFocusDescription")!
            .PropertyType.Should().Be<string>();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(91)]
    [InlineData(-5)]
    [InlineData(0)]
    public void MinutesOutsideTheAllowedRange_AreRejected(int minutes)
    {
        var result = _mapper.FromIntent(new CoachConstraintDeltaIntent { AvailableMinutes = minutes });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(181)]
    public void GoalHorizonOutsideTheAllowedRange_IsRejected(int days)
    {
        _mapper.FromIntent(new CoachConstraintDeltaIntent { GoalHorizonDays = days })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void SettingAndClearingTheSameFieldIsRejected()
    {
        _mapper.FromIntent(new CoachConstraintDeltaIntent
        {
            SkillEmphasis = CoachSkillEmphasis.Speaking,
            ClearSkillEmphasis = true
        }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void AnOverlongGoalTagIsRejected()
    {
        _mapper.FromIntent(new CoachConstraintDeltaIntent
        {
            GoalTag = new string('t', CoachConstraintLimits.MaxGoalTagLength + 1)
        }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ChangedFieldsListsExactlyWhatMoved()
    {
        var result = _mapper.FromIntent(new CoachConstraintDeltaIntent
        {
            AvailableMinutes = 12,
            AudioAllowed = false
        });

        result.IsValid.Should().BeTrue();
        result.Delta!.ChangedFields.Should().BeEquivalentTo(new[]
        {
            CoachConstraintField.AvailableMinutes,
            CoachConstraintField.AudioAllowed
        });
    }

    [Fact]
    public void AnEmptyDeltaIsValidButChangesNothing()
    {
        var result = _mapper.FromIntent(new CoachConstraintDeltaIntent());

        result.IsValid.Should().BeTrue();
        result.HasChange.Should().BeFalse();
    }

    [Fact]
    public void ApplyOnlyMovesTheNamedFields()
    {
        var current = CoachConstraintMapper.Default(20);

        var next = _mapper.Apply(current, new CoachConstraintDeltaDto
        {
            AvailableMinutes = 8,
            ChangedFields = [CoachConstraintField.AvailableMinutes]
        });

        next.AvailableMinutes.Should().Be(8);
        next.AudioAllowed.Should().Be(current.AudioAllowed);
        next.SpeechAllowed.Should().Be(current.SpeechAllowed);
        next.TypingAllowed.Should().Be(current.TypingAllowed);
        next.EnergyLevel.Should().Be(current.EnergyLevel);
    }

    [Fact]
    public void ClearingAFieldRemovesItRatherThanKeepingTheOldValue()
    {
        var current = CoachConstraintMapper.Default(20);
        var withEmphasis = _mapper.Apply(current, new CoachConstraintDeltaDto
        {
            SkillEmphasis = CoachSkillEmphasis.Listening,
            ChangedFields = [CoachConstraintField.SkillEmphasis]
        });

        var cleared = _mapper.Apply(withEmphasis, new CoachConstraintDeltaDto
        {
            ClearSkillEmphasis = true,
            ChangedFields = [CoachConstraintField.SkillEmphasis]
        });

        withEmphasis.SkillEmphasis.Should().Be(CoachSkillEmphasis.Listening);
        cleared.SkillEmphasis.Should().BeNull();
    }

    [Fact]
    public void ThePlannerValueTypeMirrorsTheConstraintSet()
    {
        var constraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 15,
            AudioAllowed = false,
            SpeechAllowed = false,
            TypingAllowed = true,
            SkillEmphasis = CoachSkillEmphasis.Reading,
            GoalTag = "travel",
            GoalHorizonDays = 30,
            EnergyLevel = CoachEnergyLevel.Low
        };

        var plan = _mapper.ToPlanConstraints(constraints);

        plan.AvailableMinutes.Should().Be(15);
        plan.AudioAllowed.Should().BeFalse();
        plan.SpeechAllowed.Should().BeFalse();
        plan.TypingAllowed.Should().BeTrue();
        plan.SkillEmphasis.Should().Be(PlanSkillEmphasis.Reading);
        plan.GoalTag.Should().Be("travel");
        plan.GoalHorizonDays.Should().Be(30);
        plan.EnergyLevel.Should().Be(PlanEnergyLevel.Low);
        plan.TryValidate(out _).Should().BeTrue();
    }

    [Fact]
    public void DefaultConstraintsAreClampedIntoTheAllowedRange()
    {
        CoachConstraintMapper.Default(0).AvailableMinutes.Should().BeInRange(
            CoachConstraintLimits.MinAvailableMinutes, CoachConstraintLimits.MaxAvailableMinutes);

        CoachConstraintMapper.Default(10_000).AvailableMinutes.Should().Be(
            CoachConstraintLimits.MaxAvailableMinutes);
    }

    [Fact]
    public void ClientSuppliedDeltasGetTheSameValidationAsModelDeltas()
    {
        _mapper.FromClient(new CoachConstraintDeltaDto { AvailableMinutes = 900 })
            .IsValid.Should().BeFalse();

        _mapper.FromClient(null).IsValid.Should().BeFalse();
    }

    [Fact]
    public void TheSummaryNamesFieldsOnlyAndNeverValues()
    {
        var summary = CoachConstraintMapper.Summarize(new CoachConstraintDeltaDto
        {
            AvailableMinutes = 8,
            GoalTag = "job interview in Seoul",
            ChangedFields = [CoachConstraintField.AvailableMinutes, CoachConstraintField.GoalTag]
        });

        summary.Should().Contain("AvailableMinutes");
        summary.Should().Contain("GoalTag");
        summary.Should().NotContain("Seoul", "a receipt summary must not echo learner-supplied text");
        summary.Should().NotContain("8");
    }
}
