using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>N1: Proves the startup validator eagerly resolves the registry and stops the host on drift.</summary>
public class CoachToolRegistryStartupValidatorTests
{
    [Fact]
    public async Task Valid_registry_starts_without_error()
    {
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IOptions<CoachOptions>>(
                    Options.Create(AllReadToolsOptions()));
                services.AddSingleton<ICoachToolRegistry>(sp =>
                    CoachToolServiceCollectionExtensions.BuildValidatedRegistry(
                        sp.GetRequiredService<IOptions<CoachOptions>>().Value));
                // The startup validator now also matrix-validates the capability manifest, and it
                // resolves the *served* singleton on purpose: validating one instance while serving
                // another would make the guard describe an object nobody uses.
                services.AddSingleton<SentenceStudio.Api.Coach.Capabilities.ICoachCapabilityManifest>(sp =>
                    new SentenceStudio.Api.Coach.Capabilities.CoachCapabilityManifest(
                        sp.GetRequiredService<ICoachToolRegistry>()));
                services.AddHostedService<CoachToolRegistryStartupValidator>();
            })
            .Build();

        var act = () => host.StartAsync();

        await act.Should().NotThrowAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Invalid_registry_stops_the_host_at_startup()
    {
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                // Register a factory that builds an invalid registry (unapproved result type)
                services.AddSingleton<ICoachToolRegistry>(sp =>
                {
                    var options = AllReadToolsOptions();
                    var registry = new CoachToolRegistry(options);
                    registry.Register(new CoachToolRegistration
                    {
                        Name = "get_bogus_drift",
                        Description = "A tool with an unapproved envelope.",
                        RiskClass = CoachToolRiskClass.Read,
                        ResultType = typeof(BogusResult),
                        EmbargoScope = CoachEmbargoScope.ToolResult
                    });
                    registry.Freeze();
                    CoachOutputContract.ValidateRegistry(registry); // throws
                    return registry;
                });
                // The startup validator now also matrix-validates the capability manifest, and it
                // resolves the *served* singleton on purpose: validating one instance while serving
                // another would make the guard describe an object nobody uses.
                services.AddSingleton<SentenceStudio.Api.Coach.Capabilities.ICoachCapabilityManifest>(sp =>
                    new SentenceStudio.Api.Coach.Capabilities.CoachCapabilityManifest(
                        sp.GetRequiredService<ICoachToolRegistry>()));
                services.AddHostedService<CoachToolRegistryStartupValidator>();
            })
            .Build();

        var act = () => host.StartAsync();

        await act.Should().ThrowAsync<CoachContractViolationException>();
    }

    private static CoachOptions AllReadToolsOptions() => new()
    {
        DurableHistory = new CoachFeatureSwitch { Enabled = true },
        SamOverlay = new CoachFeatureSwitch { Enabled = true },
        SamReadTools = new CoachFeatureSwitch { Enabled = true },
        SamWriteTools = new CoachFeatureSwitch { Enabled = false }
    };

    private sealed record BogusResult(string Oops);
}

/// <summary>N2: Exercises the actual BudgetedAIFunction wrapper produced by CoachToolCallBudget.Apply.</summary>
public class BudgetedAIFunctionWrapperTests
{
    private static AIFunction CreateFake(string name = "get_fake", Func<Task<object?>>? invoke = null)
    {
        invoke ??= () => Task.FromResult<object?>("ok");
        return AIFunctionFactory.Create(
            (CancellationToken _) => invoke(),
            new AIFunctionFactoryOptions { Name = name, Description = "A test tool." });
    }

    [Fact]
    public async Task Calls_one_through_twenty_delegate_to_the_inner_function()
    {
        var callCount = 0;
        var inner = CreateFake(invoke: () => { Interlocked.Increment(ref callCount); return Task.FromResult<object?>("ok"); });
        var (tools, budget) = CoachToolCallBudget.Apply([inner]);

        for (var i = 0; i < 20; i++)
            await tools[0].InvokeAsync(new AIFunctionArguments());

        callCount.Should().Be(20);
        budget.Used.Should().Be(20);
        budget.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task Call_twenty_one_throws_before_inner_function_executes()
    {
        var callCount = 0;
        var inner = CreateFake(invoke: () => { Interlocked.Increment(ref callCount); return Task.FromResult<object?>("ok"); });
        var (tools, _) = CoachToolCallBudget.Apply([inner]);

        for (var i = 0; i < 20; i++)
            await tools[0].InvokeAsync(new AIFunctionArguments());

        callCount.Should().Be(20);

        var act = () => tools[0].InvokeAsync(new AIFunctionArguments()).AsTask();

        var ex = (await act.Should().ThrowAsync<CoachToolException>()).Which;
        ex.Kind.Should().Be(CoachToolFailureKind.BudgetExhausted);

        // The inner function was never called for the 21st invocation
        callCount.Should().Be(20);
    }

    [Fact]
    public void Wrapper_preserves_name_and_description()
    {
        var inner = CreateFake(name: "get_skill_list");
        var (tools, _) = CoachToolCallBudget.Apply([inner]);

        tools[0].Name.Should().Be("get_skill_list");
        tools[0].Description.Should().Be("A test tool.");
    }

    [Fact]
    public void Wrapper_preserves_json_schema_metadata()
    {
        var inner = CreateFake(name: "get_learning_resource_list");
        var (tools, _) = CoachToolCallBudget.Apply([inner]);

        // The schema from the inner function is passed through the delegating wrapper
        tools[0].JsonSchema.Should().NotBeNull();
        tools[0].JsonSchema.Should().BeEquivalentTo(inner.JsonSchema);
    }

    [Fact]
    public async Task Concurrent_invocations_cannot_exceed_budget()
    {
        var callCount = 0;
        var inner = CreateFake(invoke: async () =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(1); // simulate async work
            return (object?)"ok";
        });
        var (tools, budget) = CoachToolCallBudget.Apply([inner]);

        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(async () =>
            {
                try { await tools[0].InvokeAsync(new AIFunctionArguments()); return true; }
                catch (CoachToolException) { return false; }
            }))
            .ToList();

        var results = await Task.WhenAll(tasks);
        var succeeded = results.Count(r => r);

        succeeded.Should().Be(20);
        callCount.Should().Be(20);
        budget.Used.Should().BeGreaterThanOrEqualTo(20);
    }

    [Fact]
    public async Task Multiple_tools_in_set_share_the_same_budget()
    {
        var inner1 = CreateFake(name: "tool_a");
        var inner2 = CreateFake(name: "tool_b");
        var (tools, budget) = CoachToolCallBudget.Apply([inner1, inner2], limit: 5);

        await tools[0].InvokeAsync(new AIFunctionArguments());
        await tools[0].InvokeAsync(new AIFunctionArguments());
        await tools[1].InvokeAsync(new AIFunctionArguments());
        await tools[1].InvokeAsync(new AIFunctionArguments());
        await tools[1].InvokeAsync(new AIFunctionArguments());

        budget.Used.Should().Be(5);

        var act = () => tools[0].InvokeAsync(new AIFunctionArguments()).AsTask();
        await act.Should().ThrowAsync<CoachToolException>();
    }
}

/// <summary>N3: Proves LearningResourceListTool computes HasTranscript in the SQL projection.</summary>
public class LearningResourceListTranscriptProjectionTests
{
    [Fact]
    public async Task HasTranscript_is_true_when_transcript_is_non_empty()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedResource(CoachToolTestFixture.UserA, transcript: "Real transcript content");

        var result = await fixture.LearningResourceListTool.GetAsync();

        result.Resources.Should().ContainSingle().Which.HasTranscript.Should().BeTrue();
    }

    [Fact]
    public async Task HasTranscript_is_false_when_transcript_is_null()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedResource(CoachToolTestFixture.UserA, transcript: null);

        var result = await fixture.LearningResourceListTool.GetAsync();

        result.Resources.Should().ContainSingle().Which.HasTranscript.Should().BeFalse();
    }

    [Fact]
    public async Task HasTranscript_is_false_when_transcript_is_empty_string()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedResource(CoachToolTestFixture.UserA, transcript: string.Empty);

        var result = await fixture.LearningResourceListTool.GetAsync();

        result.Resources.Should().ContainSingle().Which.HasTranscript.Should().BeFalse();
    }

    [Fact]
    public async Task Transcript_body_is_not_serialized_in_result()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        const string LongTranscript = "A very long transcript body that must never appear in the serialized result";
        fixture.SeedResource(CoachToolTestFixture.UserA, transcript: LongTranscript);

        var result = await fixture.LearningResourceListTool.GetAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        json.Should().NotContain(LongTranscript);
        result.Resources.Should().ContainSingle().Which.HasTranscript.Should().BeTrue();
    }
}
