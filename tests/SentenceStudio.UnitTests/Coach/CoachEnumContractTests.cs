using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.Plans;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// Proves every bounded coach concept uses a closed enum with a safe default.
/// </summary>
public class CoachEnumContractTests
{
    /// <summary>
    /// The documented fail-closed default of each enum that gates a plan write or a success state.
    /// </summary>
    public static TheoryData<Type, string> FailClosedDefaults => new()
    {
        { typeof(CoachIntentKind), nameof(CoachIntentKind.NoChange) },
        { typeof(CoachAcceptanceState), nameof(CoachAcceptanceState.NotApplicable) },
        { typeof(CoachTurnStatus), nameof(CoachTurnStatus.Failed) },
        { typeof(CoachStopReason), nameof(CoachStopReason.Failed) },
        { typeof(CoachSessionStatus), nameof(CoachSessionStatus.Expired) },
        { typeof(CoachAvailabilityState), nameof(CoachAvailabilityState.Disabled) },
        { typeof(CoachPlanItemChangeKind), nameof(CoachPlanItemChangeKind.Unchanged) },

        // A proposed change is approved from these three. An unset status must not read as
        // applied, an unset risk class must not pick an approval channel, and an unrecognised kind
        // must not name a specific change — all three would render a plausible card describing
        // something that is not true.
        { typeof(CoachWriteStatus), nameof(CoachWriteStatus.Unknown) },
        { typeof(CoachWriteRiskClass), nameof(CoachWriteRiskClass.Unknown) },
        { typeof(CoachWriteChangeKind), nameof(CoachWriteChangeKind.Unknown) },
        { typeof(CoachWriteTargetKind), nameof(CoachWriteTargetKind.None) }
    };

    [Theory]
    [MemberData(nameof(FailClosedDefaults))]
    public void Enum_default_is_the_documented_safe_value(Type enumType, string expectedDefaultName)
    {
        Enum.GetName(enumType, Activator.CreateInstance(enumType)!)
            .Should().Be(expectedDefaultName, "the zero value must never signal a write or a success");
    }

    [Fact]
    public void Every_coach_enum_serializes_as_a_name()
    {
        var offenders = CoachContractTypes.Enums
            .Where(t => t.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType != typeof(JsonStringEnumConverter))
            .Select(t => t.Name)
            .ToList();

        offenders.Should().BeEmpty("a numeric wire value breaks client and log readability");
    }

    [Fact]
    public void No_coach_enum_is_a_flag_set()
    {
        var offenders = CoachContractTypes.Enums
            .Where(t => t.GetCustomAttribute<FlagsAttribute>() is not null)
            .Select(t => t.Name)
            .ToList();

        offenders.Should().BeEmpty("a flag set is an open concept and breaks the closed contract");
    }

    [Fact]
    public void Every_coach_enum_uses_unique_names_and_values()
    {
        foreach (var enumType in CoachContractTypes.Enums)
        {
            Enum.GetNames(enumType).Should().OnlyHaveUniqueItems(enumType.Name);
            Enum.GetValues(enumType).Cast<int>().Should().OnlyHaveUniqueItems(enumType.Name);
        }
    }

    /// <summary>
    /// The strict default. A model that invents an enum member must be refused.
    /// </summary>
    /// <remarks>
    /// This is <b>not</b> in tension with the client wire tolerance added in
    /// <c>CoachWireToleranceTests</c>. Tolerance lives in one client-owned
    /// <see cref="JsonSerializerOptions"/> instance, and System.Text.Json resolves a converter from
    /// the options collection ahead of the one on the type — so the client degrades and every other
    /// parser in the solution, this one included, stays closed. The two are complements: a model
    /// inventing <c>"DeletePlan"</c> is a bug to surface, and a newer server naming a member an old
    /// phone has never seen is a fact to survive.
    /// </remarks>
    [Fact]
    public void An_unknown_enum_name_is_refused()
    {
        var act = () => JsonSerializer.Deserialize<CoachIntentKind>("\"DeletePlan\"");

        act.Should().Throw<JsonException>("the intent set is closed");
    }

    [Fact]
    public void Coach_activity_types_match_the_plan_wire_names()
    {
        var planNames = typeof(PlanActivityTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        var coachNames = Enum.GetNames<CoachPlanActivityType>()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        coachNames.Should().Equal(planNames, "a coach plan item must name the same activity as Today's Plan");
    }

    [Fact]
    public void Constraint_field_names_match_the_constraint_set_members()
    {
        var setMembers = CoachContractTypes.PublicProperties(typeof(CoachConstraintSetDto))
            .Select(p => p.Name)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        var fieldNames = Enum.GetNames<CoachConstraintField>()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        fieldNames.Should().Equal(setMembers, "a receipt names constraint fields, so the two sets must stay the same");
    }

    [Fact]
    public void Constraint_limits_match_the_approved_ranges()
    {
        CoachConstraintLimits.MinAvailableMinutes.Should().Be(3);
        CoachConstraintLimits.MaxAvailableMinutes.Should().Be(90);
        CoachConstraintLimits.MinGoalHorizonDays.Should().Be(1);
        CoachConstraintLimits.MaxGoalHorizonDays.Should().Be(180);
        CoachConstraintLimits.MaxTurnTextLength.Should().Be(500);
        CoachConstraintLimits.MaxClarificationsPerSession.Should().Be(2);
    }
}
