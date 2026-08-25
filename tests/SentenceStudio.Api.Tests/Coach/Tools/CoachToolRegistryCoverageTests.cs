using FluentAssertions;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Proves the registry is the single source of truth for embargo coverage, and that it is
/// sealed before anything reads it.
/// </summary>
/// <remarks>
/// The earlier arrangement kept a list of result types by hand next to the registry. Adding a tool
/// and forgetting the list produced a tool whose result was never scanned, and nothing failed. The
/// tests here assert the opposite property: a registration the contract does not recognise stops
/// startup.
/// </remarks>
public class CoachToolRegistryCoverageTests
{
    private static CoachOptions AllReadTools() => new()
    {
        DurableHistory = new CoachFeatureSwitch { Enabled = true },
        SamOverlay = new CoachFeatureSwitch { Enabled = true },
        SamReadTools = new CoachFeatureSwitch { Enabled = true },
        SamWriteTools = new CoachFeatureSwitch { Enabled = false }
    };

    // ---------------------------------------------------------------------
    // Freezing.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_new_registry_is_not_frozen()
        => new CoachToolRegistry(AllReadTools()).IsFrozen.Should().BeFalse();

    [Fact]
    public void Freezing_is_idempotent()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();
        registry.Freeze();

        registry.IsFrozen.Should().BeTrue();
    }

    [Fact]
    public void A_frozen_registry_refuses_a_late_registration()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        var act = () => registry.Register(new CoachToolRegistration
        {
            Name = "get_something_else",
            Description = "A tool that arrived after the gate closed.",
            RiskClass = CoachToolRiskClass.Read,
            ResultType = typeof(SkillListResult),
            EmbargoScope = CoachEmbargoScope.ToolResult
        });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_registry_the_container_resolves_is_already_frozen()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(AllReadTools());

        registry.IsFrozen.Should().BeTrue(
            "the container hands out a registry that nothing can extend at runtime");
    }

    [Fact]
    public void The_registry_the_container_resolves_refuses_a_late_registration()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(AllReadTools());

        // Two guarantees, and the first is the stronger one. ICoachToolRegistry — the type every
        // consumer takes a dependency on — has no Register member at all, so a service cannot
        // extend the tool surface even by mistake. The cast below reaches past that to the
        // concrete type and confirms the door is bolted on that side too.
        typeof(ICoachToolRegistry).GetMethod("Register").Should().BeNull(
            "no consumer should be able to register a tool through the resolved interface");

        var act = () => ((CoachToolRegistry)registry).Register(new CoachToolRegistration
        {
            Name = "get_late_tool",
            Description = "Registered from a service after startup finished.",
            RiskClass = CoachToolRiskClass.Read,
            ResultType = typeof(SkillListResult),
            EmbargoScope = CoachEmbargoScope.ToolResult
        });

        act.Should().Throw<InvalidOperationException>();
    }

    // ---------------------------------------------------------------------
    // Coverage, driven by the registry rather than by a parallel list.
    // ---------------------------------------------------------------------

    [Fact]
    public void The_production_registry_passes_registry_validation()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        var act = () => CoachOutputContract.ValidateRegistry(registry);

        act.Should().NotThrow();
    }

    [Fact]
    public void Every_shipped_registration_declares_an_approved_envelope_at_the_right_scope()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        foreach (var registration in registry.All)
        {
            CoachOutputContract.ApprovedResultEnvelopes
                .Should().ContainKey(registration.ResultType);

            CoachOutputContract.ApprovedResultEnvelopes[registration.ResultType]
                .Should().Be(registration.EmbargoScope,
                    "the declared scope on {0} must match the approved scope for its envelope",
                    registration.Name);
        }
    }

    [Fact]
    public void Validation_before_the_registry_is_frozen_is_refused()
    {
        var registry = new CoachToolRegistry(AllReadTools());

        var result = CoachOutputContract.ScanRegistry(registry);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "registry_not_frozen");
    }

    [Fact]
    public void An_unapproved_result_type_fails_the_scan()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Register(new CoachToolRegistration
        {
            Name = "get_unknown_shape",
            Description = "A tool whose envelope nobody approved.",
            RiskClass = CoachToolRiskClass.Read,
            ResultType = typeof(UnapprovedResult),
            EmbargoScope = CoachEmbargoScope.ToolResult
        });
        registry.Freeze();

        var result = CoachOutputContract.ScanRegistry(registry);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v =>
            v.Code == "unapproved_result_envelope" && v.Message.Contains("get_unknown_shape"));
    }

    [Fact]
    public void An_unapproved_result_type_stops_startup()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Register(new CoachToolRegistration
        {
            Name = "get_unknown_shape",
            Description = "A tool whose envelope nobody approved.",
            RiskClass = CoachToolRiskClass.Read,
            ResultType = typeof(UnapprovedResult),
            EmbargoScope = CoachEmbargoScope.ToolResult
        });
        registry.Freeze();

        var act = () => CoachOutputContract.ValidateRegistry(registry);

        act.Should().Throw<CoachContractViolationException>()
            .WithMessage("*unapproved_result_envelope*");
    }

    [Fact]
    public void A_registration_that_declares_the_wrong_scope_fails_the_scan()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Register(new CoachToolRegistration
        {
            Name = "get_mismatched_scope",
            Description = "An approved envelope declared under the wrong scope.",
            RiskClass = CoachToolRiskClass.Read,

            // SkillListResult is approved, but only under ToolResult. Declaring it as
            // ModelVisible would scan it against the wrong word list.
            ResultType = typeof(SkillListResult),
            EmbargoScope = CoachEmbargoScope.ModelVisible
        });
        registry.Freeze();

        var result = CoachOutputContract.ScanRegistry(registry);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "result_envelope_scope_mismatch");
    }

    [Fact]
    public void A_registration_scoped_to_the_public_client_fails_the_scan()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Register(new CoachToolRegistration
        {
            Name = "get_client_shaped_thing",
            Description = "A tool result is never a client payload.",
            RiskClass = CoachToolRiskClass.Read,
            ResultType = typeof(SkillListResult),
            EmbargoScope = CoachEmbargoScope.PublicClient
        });
        registry.Freeze();

        var result = CoachOutputContract.ScanRegistry(registry);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "tool_scope_not_model_facing");
    }

    [Fact]
    public void An_approved_envelope_that_no_tool_uses_fails_the_scan()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        // An approval for a shape nobody returns is either a deletion somebody did not finish or a
        // tool somebody forgot to register, and both are worth stopping for. The shipped pair does
        // not have one, so the disagreement is supplied here.
        var approvals = new Dictionary<Type, CoachEmbargoScope>(
            CoachOutputContract.ApprovedResultEnvelopes)
        {
            [typeof(UnapprovedResult)] = CoachEmbargoScope.ToolResult
        };

        var result = CoachOutputContract.ScanRegistry(registry, approvals);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "orphaned_result_envelope");
    }

    [Fact]
    public void Coverage_does_not_depend_on_the_sam_feature_flags()
    {
        // The registry holds every tool it knows about in All and filters only Enabled by flag, so
        // the shapes are scanned whether or not the flag is on. That is the property that makes a
        // flag flip safe: turning Sam on in production cannot introduce a shape that startup has
        // not already judged.
        var flagsOff = new CoachToolRegistry(new CoachOptions
        {
            SamOverlay = new CoachFeatureSwitch { Enabled = false },
            SamReadTools = new CoachFeatureSwitch { Enabled = false },
            SamWriteTools = new CoachFeatureSwitch { Enabled = false }
        });
        flagsOff.Freeze();

        var flagsOn = new CoachToolRegistry(AllReadTools());
        flagsOn.Freeze();

        flagsOff.All.Select(r => r.ResultType)
            .Should().BeEquivalentTo(flagsOn.All.Select(r => r.ResultType));

        flagsOff.Enabled.Count.Should().BeLessThan(flagsOn.Enabled.Count,
            "the flag still gates what the model is offered");

        CoachOutputContract.ScanRegistry(flagsOff).IsValid.Should().BeTrue();
        CoachOutputContract.ScanRegistry(flagsOn).IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_core_five_survive_the_sam_registrations()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        var names = registry.All.Select(r => r.Name).ToList();

        names.Should().Contain(CoachToolNames.CoreFive,
            "the Sam overlay adds reads; it does not replace the plan tools");

        // And the core five stay available with the overlay switched off.
        var flagsOff = new CoachToolRegistry(new CoachOptions
        {
            SamOverlay = new CoachFeatureSwitch { Enabled = false },
            SamReadTools = new CoachFeatureSwitch { Enabled = false },
            SamWriteTools = new CoachFeatureSwitch { Enabled = false }
        });
        flagsOff.Enabled.Select(r => r.Name).Should().BeEquivalentTo(CoachToolNames.CoreFive);
    }

    /// <summary>
    /// Every write registration is a proposal that goes through the approval ledger.
    /// </summary>
    /// <remarks>
    /// Phase two could state this simply: nothing was a write. Phase three adds writes, so the
    /// invariant becomes conditional rather than absolute — and the conditional version is the one
    /// worth keeping, because it is what a reviewer actually wants to know. A write registration
    /// that is not named as a proposal would be a tool that acts when the model calls it, which is
    /// the arrangement the ledger exists to prevent.
    /// </remarks>
    [Fact]
    public void Every_registered_write_is_a_proposal()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        foreach (var registration in registry.All)
        {
            if (registration.RiskClass == CoachToolRiskClass.Read)
            {
                continue;
            }

            registration.Name.Should().StartWith(
                CoachToolNames.ProposePrefix,
                $"'{registration.Name}' is a write and must go through the approval ledger");
            registration.RiskClass.Should().BeOneOf(
                CoachToolRiskClass.WriteSoft, CoachToolRiskClass.WriteHard);
        }
    }

    /// <summary>
    /// Nothing that is not a declared write tool carries a write risk class.
    /// </summary>
    [Fact]
    public void No_tool_outside_the_declared_write_set_is_a_write()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        var writes = registry.All
            .Where(r => r.RiskClass != CoachToolRiskClass.Read)
            .Select(r => r.Name);

        writes.Should().BeEquivalentTo(CoachToolNames.AllWrite);
    }

    [Fact]
    public void The_approved_envelope_table_covers_every_shipped_registration()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        var registered = registry.All.Select(r => r.ResultType).Distinct().ToList();

        // Both directions. Missing entries mean an unscanned result; extra entries mean an
        // approval nobody uses.
        CoachOutputContract.ApprovedResultEnvelopes.Keys
            .Should().BeEquivalentTo(registered);
    }

    /// <summary>A result shape the contract has never approved.</summary>
    private sealed record UnapprovedResult(string Note);
}

/// <summary>
/// Proves a turn cannot make an unbounded number of tool calls.
/// </summary>
/// <remarks>
/// The iteration limit was not enough on its own. One assistant message can carry several tool
/// calls, so six iterations can be far more than six calls. The budget counts calls.
/// </remarks>
public class CoachToolCallBudgetTests
{
    [Fact]
    public void The_default_limit_is_the_rfc_ceiling()
    {
        CoachToolCallBudget.MaxCallsPerTurn.Should().Be(20);
        new CoachToolCallBudget().Limit.Should().Be(20);
    }

    [Fact]
    public void A_limit_below_one_is_refused()
    {
        var act = () => new CoachToolCallBudget(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Calls_up_to_the_limit_are_allowed()
    {
        var budget = new CoachToolCallBudget(3);

        budget.Consume("get_skills");
        budget.Consume("get_skills");
        budget.Consume("get_skills");

        budget.Used.Should().Be(3);
        budget.Remaining.Should().Be(0);
    }

    [Fact]
    public void The_call_past_the_limit_is_refused()
    {
        var budget = new CoachToolCallBudget(2);
        budget.Consume("get_skills");
        budget.Consume("get_skills");

        var act = () => budget.Consume("get_skills");

        var failure = act.Should().Throw<CoachToolException>().Which;
        failure.Kind.Should().Be(CoachToolFailureKind.BudgetExhausted);
        failure.Code.Should().Be("tool_budget_exhausted");
    }

    [Fact]
    public void The_twenty_first_call_of_a_default_budget_is_refused()
    {
        var budget = new CoachToolCallBudget();

        for (var i = 0; i < 20; i++)
            budget.Consume("get_skills");

        var act = () => budget.Consume("get_skills");

        act.Should().Throw<CoachToolException>()
            .Which.Code.Should().Be("tool_budget_exhausted");
    }

    [Fact]
    public void Remaining_never_goes_below_zero()
    {
        var budget = new CoachToolCallBudget(1);
        budget.Consume("get_skills");

        try { budget.Consume("get_skills"); } catch (CoachToolException) { }
        try { budget.Consume("get_skills"); } catch (CoachToolException) { }

        budget.Remaining.Should().Be(0);
    }

    [Fact]
    public void Parallel_calls_cannot_race_past_the_limit()
    {
        // A single assistant message can ask for several tools at once, and the harness may run
        // them together. A non-atomic counter would let the last slot be handed out twice.
        var budget = new CoachToolCallBudget(10);
        var granted = 0;

        Parallel.For(0, 200, _ =>
        {
            try
            {
                budget.Consume("get_skills");
                Interlocked.Increment(ref granted);
            }
            catch (CoachToolException)
            {
            }
        });

        granted.Should().Be(10);
    }
}
