using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Proves the generated tool schemas are closed, hold no identity argument, and
/// pass the allow-list.
/// </summary>
public class CoachToolSchemaTests : IDisposable
{
    private readonly CoachToolTestFixture _fixture = new();
    private readonly CoachToolAllowList _allowList = new();

    private IReadOnlyList<AIFunction> CreateTools()
    {
        var planner = new RecordingPlanGenerator(_ => new PlanSkeleton
        {
            Activities = [new PlannedActivity { ActivityType = "Reading", EstimatedMinutes = 5, Priority = 1, Rationale = "r" }],
            TotalMinutes = 5,
            ResourceSelectionReason = "r"
        });

        var factory = new CoachToolFactory(
            _fixture.ProfileTool,
            _fixture.BalanceTool,
            _fixture.VocabularyTool,
            _fixture.ResourceTool,
            new PreviewPracticePlanTool(_fixture.Scope, planner, new DefaultCoachPlanPreviewFailureAdapter(), _fixture.Dates),
            CoachToolTestFixture.CoreOnlyRegistry(),
            CoachToolTestFixture.NullServiceProvider());

        return factory.CreateTools();
    }

    [Fact]
    public void The_tool_set_is_exactly_the_five_read_only_tools()
    {
        var tools = CreateTools();

        tools.Select(t => t.Name).Should().Equal(CoachToolNames.CoreFive,
            "with Sam features disabled, only the core five tools should be built");
        _allowList.Validate(tools).IsValid.Should().BeTrue();
    }

    [Fact]
    public void No_tool_schema_names_an_identity_argument()
    {
        var tools = CreateTools();

        foreach (var tool in tools)
        {
            var names = CoachToolAllowList.CollectPropertyNames(tool.JsonSchema).ToList();

            names.Should().NotContain(n => n.Contains("user", StringComparison.OrdinalIgnoreCase));
            names.Should().NotContain(n => n.Contains("profile", StringComparison.OrdinalIgnoreCase));
            names.Should().NotContain(n => n.Contains("tenant", StringComparison.OrdinalIgnoreCase));
            names.Should().NotContain(n => n.Contains("email", StringComparison.OrdinalIgnoreCase));
            _allowList.ValidateSchema(tool).Should().BeEmpty();
        }
    }

    [Fact]
    public void Every_tool_schema_refuses_an_argument_it_does_not_name()
    {
        var tools = CreateTools();

        foreach (var tool in tools)
        {
            var schema = tool.JsonSchema;
            schema.TryGetProperty("additionalProperties", out var additional).Should().BeTrue(
                $"{tool.Name} must close its argument shape");
            additional.ValueKind.Should().Be(JsonValueKind.False);
        }
    }

    [Fact]
    public void The_preview_schema_names_only_the_approved_constraint_fields()
    {
        var preview = CreateTools().Single(t => t.Name == CoachToolNames.PreviewPracticePlan);

        var names = CoachToolAllowList.CollectPropertyNames(preview.JsonSchema).ToList();

        names.Should().Contain(["availableMinutes", "audioAllowed", "speechAllowed", "typingAllowed",
            "skillEmphasis", "goalTag", "goalHorizonDays", "energyLevel"]);
        names.Should().NotContain("resourceId");
        names.Should().NotContain("planItemIds");
    }

    [Fact]
    public void The_practice_balance_schema_offers_only_three_windows()
    {
        var balance = CreateTools().Single(t => t.Name == CoachToolNames.GetPracticeBalance);

        var json = balance.JsonSchema.GetRawText();

        json.Should().Contain("SevenDays").And.Contain("FourteenDays").And.Contain("ThirtyDays");
        json.Should().NotContain("SixtyDays");
    }

    [Fact]
    public void The_allow_list_refuses_a_tool_that_is_not_approved()
    {
        var tools = CreateTools().ToList();
        tools.Add(AIFunctionFactory.Create(() => "ok", "apply_plan_update", "Writes the plan."));

        var result = _allowList.Validate(tools);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "unknown_tool");
        result.Violations.Should().Contain(v => v.Code == "write_tool");
    }

    [Fact]
    public void The_allow_list_refuses_a_missing_tool()
    {
        var tools = CreateTools().Where(t => t.Name != CoachToolNames.GetVocabularyDueSummary).ToList();

        var result = _allowList.Validate(tools);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "missing_tool");
    }

    [Fact]
    public void The_allow_list_refuses_a_tool_that_accepts_a_user_argument()
    {
        var rogue = AIFunctionFactory.Create(
            (string userProfileId) => userProfileId,
            CoachToolNames.GetPracticeBalance,
            "Reads the balance for a named learner.");

        _allowList.ValidateSchema(rogue).Should().Contain(v => v.Code == "identity_argument");
    }

    public void Dispose() => _fixture.Dispose();
}
