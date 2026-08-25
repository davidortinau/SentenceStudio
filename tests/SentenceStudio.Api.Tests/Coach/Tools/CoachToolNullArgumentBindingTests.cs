using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Drives every tool through the same JSON argument binding a model uses, with explicit
/// nulls for optional fields.
/// </summary>
/// <remarks>
/// A live harness run logged
/// <c>preview_practice_plan invocation failed. JsonException: Cannot convert null to
/// Boolean. Path $.speechAllowed</c>. The model answered the closed schema by sending
/// <c>null</c> for the fields it had no value for, and the non-nullable
/// <c>bool</c>/<c>enum</c> properties could not bind it. The run still ended in a success
/// message, which is worse than a loud failure: a tool the coach believed it had called had
/// never run. Every optional argument is nullable now, and these tests bind through
/// <see cref="AIFunction.InvokeAsync"/> so a regression shows up as a real binding error.
/// </remarks>
public class CoachToolNullArgumentBindingTests : IDisposable
{
    private readonly CoachToolTestFixture _fixture = new();
    private readonly RecordingPlanGenerator _planner;
    private readonly IReadOnlyList<AIFunction> _tools;

    public CoachToolNullArgumentBindingTests()
    {
        _planner = new RecordingPlanGenerator(_ => SamplePlan());

        _fixture.SeedProfile(CoachToolTestFixture.UserA);

        _tools = new CoachToolFactory(
            _fixture.ProfileTool,
            _fixture.BalanceTool,
            _fixture.VocabularyTool,
            _fixture.ResourceTool,
            new PreviewPracticePlanTool(_fixture.Scope, _planner, new DefaultCoachPlanPreviewFailureAdapter(), _fixture.Dates),
            CoachToolTestFixture.CoreOnlyRegistry(),
            CoachToolTestFixture.NullServiceProvider())
            .CreateTools();
    }

    private static PlanSkeleton SamplePlan() => new()
    {
        Activities =
        [
            new PlannedActivity
            {
                ActivityType = "Reading", EstimatedMinutes = 5, Priority = 1, Rationale = "r"
            }
        ],
        TotalMinutes = 5,
        ResourceSelectionReason = "r"
    };

    private AIFunction Tool(string name) => _tools.Single(t => t.Name == name);

    /// <summary>
    /// Invokes a function exactly as <c>FunctionInvokingChatClient</c> does: every argument
    /// arrives as a <see cref="JsonElement"/> parsed from the model's JSON.
    /// </summary>
    private static async Task<object?> InvokeAsync(AIFunction function, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);

        var arguments = new AIFunctionArguments();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return await function.InvokeAsync(arguments, CancellationToken.None);
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task TheLiveFailureCaseBindsNow()
    {
        // The exact shape from the harness log: every unstated field sent as null.
        const string json = """
            {
              "constraints": {
                "availableMinutes": 10,
                "audioAllowed": null,
                "speechAllowed": null,
                "typingAllowed": null,
                "skillEmphasis": null,
                "goalTag": null,
                "goalHorizonDays": null,
                "energyLevel": null
              }
            }
            """;

        var act = () => InvokeAsync(Tool(CoachToolNames.PreviewPracticePlan), json);

        await act.Should().NotThrowAsync();

        var constraints = _planner.LastRequest!.Constraints!;
        constraints.AvailableMinutes.Should().Be(10);
        constraints.AudioAllowed.Should().BeTrue("a null flag means the learner did not say");
        constraints.SpeechAllowed.Should().BeTrue();
        constraints.TypingAllowed.Should().BeTrue();
        constraints.SkillEmphasis.Should().BeNull();
        constraints.GoalTag.Should().BeNull();
        constraints.GoalHorizonDays.Should().BeNull();
        constraints.EnergyLevel.Should().Be(PlanEnergyLevel.Normal);
    }

    [Fact]
    public async Task MissingOptionalFieldsBindTheSameWayAsExplicitNulls()
    {
        await InvokeAsync(Tool(CoachToolNames.PreviewPracticePlan), """{"constraints":{"availableMinutes":10}}""");

        var constraints = _planner.LastRequest!.Constraints!;
        constraints.AudioAllowed.Should().BeTrue();
        constraints.SpeechAllowed.Should().BeTrue();
        constraints.TypingAllowed.Should().BeTrue();
        constraints.EnergyLevel.Should().Be(PlanEnergyLevel.Normal);
    }

    [Fact]
    public async Task AMixOfFalseAndNullKeepsEveryStatedValue()
    {
        const string json = """
            {
              "constraints": {
                "availableMinutes": 8,
                "audioAllowed": false,
                "speechAllowed": null,
                "typingAllowed": false,
                "skillEmphasis": "Speaking",
                "goalTag": null,
                "goalHorizonDays": 30,
                "energyLevel": "Low"
              }
            }
            """;

        await InvokeAsync(Tool(CoachToolNames.PreviewPracticePlan), json);

        var constraints = _planner.LastRequest!.Constraints!;
        constraints.AvailableMinutes.Should().Be(8);
        constraints.AudioAllowed.Should().BeFalse("a stated false must survive");
        constraints.SpeechAllowed.Should().BeTrue("only the unstated field takes the default");
        constraints.TypingAllowed.Should().BeFalse();
        constraints.SkillEmphasis.Should().Be(PlanSkillEmphasis.Speaking);
        constraints.GoalHorizonDays.Should().Be(30);
        constraints.EnergyLevel.Should().Be(PlanEnergyLevel.Low);
    }

    [Fact]
    public async Task AnAllNullObjectPreviewsTheCurrentPlan()
    {
        const string json = """
            {
              "constraints": {
                "availableMinutes": null,
                "audioAllowed": null,
                "speechAllowed": null,
                "typingAllowed": null,
                "skillEmphasis": null,
                "goalTag": null,
                "goalHorizonDays": null,
                "energyLevel": null
              }
            }
            """;

        var act = () => InvokeAsync(Tool(CoachToolNames.PreviewPracticePlan), json);

        await act.Should().NotThrowAsync();
        _planner.LastRequest!.AllowWrites.Should().BeFalse("a preview still writes nothing");
    }

    [Fact]
    public async Task ANullConstraintsObjectStillPreviews()
    {
        var act = () => InvokeAsync(Tool(CoachToolNames.PreviewPracticePlan), """{"constraints":null}""");

        await act.Should().NotThrowAsync();
        _planner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task AnOutOfRangeValueIsStillATypedToolFailureNotABindingError()
    {
        var act = () => InvokeAsync(
            Tool(CoachToolNames.PreviewPracticePlan), """{"constraints":{"availableMinutes":900}}""");

        (await act.Should().ThrowAsync<CoachToolException>()).Which
            .Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
        _planner.CallCount.Should().Be(0);
    }

    // ---------------------------------------------------------------- other tools

    [Fact]
    public async Task ANullTagCountUsesTheDefault()
    {
        var act = () => InvokeAsync(
            Tool(CoachToolNames.GetVocabularyDueSummary), """{"maxCategoryTags":null}""");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ANullResultCountUsesTheDefault()
    {
        var act = () => InvokeAsync(Tool(CoachToolNames.GetResourceCatalog), """{"maxResults":null}""");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EveryToolBindsAnEmptyArgumentObject()
    {
        foreach (var tool in _tools.Where(t => t.Name != CoachToolNames.GetPracticeBalance))
        {
            var act = () => InvokeAsync(tool, "{}");

            await act.Should().NotThrowAsync($"{tool.Name} must tolerate a model that states nothing");
        }
    }

    [Fact]
    public async Task ANullWindowIsATypedFailureNotABindingError()
    {
        // The window has no safe default, so a null is refused — but as a coach failure the
        // run can report, never as a JsonException from the binder.
        var act = () => InvokeAsync(Tool(CoachToolNames.GetPracticeBalance), """{"window":null}""");

        (await act.Should().ThrowAsync<CoachToolException>()).Which
            .Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
    }

    [Fact]
    public async Task AStatedWindowStillBinds()
    {
        var act = () => InvokeAsync(Tool(CoachToolNames.GetPracticeBalance), """{"window":"FourteenDays"}""");

        await act.Should().NotThrowAsync();
    }

    // ---------------------------------------------------------------- schema

    [Fact]
    public void TheSchemaStillRefusesAnArgumentItDoesNotName()
    {
        foreach (var tool in _tools)
        {
            tool.JsonSchema.TryGetProperty("additionalProperties", out var additional).Should().BeTrue();
            additional.ValueKind.Should().Be(JsonValueKind.False,
                "{0} must stay closed even though its arguments accept null", tool.Name);
        }
    }

    [Fact]
    public void TheSchemaDescribesTheOptionalFlagsAsNullAccepting()
    {
        var schema = Tool(CoachToolNames.PreviewPracticePlan).JsonSchema.GetRawText();

        foreach (var field in new[] { "audioAllowed", "speechAllowed", "typingAllowed", "energyLevel" })
        {
            schema.Should().Contain(field);
        }

        schema.Should().Contain("null", "the optional fields must advertise that null is acceptable");
    }

    [Fact]
    public void NoPreviewArgumentIsANonNullableValueType()
    {
        var offenders = typeof(CoachPlanPreviewArguments)
            .GetProperties()
            .Where(p => p.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) is null)
            .Select(p => p.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "a non-nullable value type cannot bind the explicit null a model sends for an unstated field");
    }

    [Fact]
    public void TheDefaultsAreTheSafePermissiveOnes()
    {
        CoachPlanPreviewArguments.DefaultModalityAllowed.Should().BeTrue(
            "silence must not remove a modality from the plan");
        CoachPlanPreviewArguments.DefaultEnergyLevel.Should().Be(CoachEnergyLevel.Normal);
    }

    public void Dispose() => _fixture.Dispose();
}
