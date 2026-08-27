using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Resolves the Sam tools from a fixture, so a factory can build the whole enabled registry.
/// </summary>
/// <remarks>
/// The sweep below is only worth running against the <em>full</em> tool set. A core-only registry
/// would exercise five tools and report a green sweep over a third of the surface, which is the
/// shape of vacuity this file exists to avoid.
/// </remarks>
internal sealed class FixtureToolServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, Func<object>> _factories;
    private readonly List<object> _extras = new();

    public FixtureToolServiceProvider(CoachToolTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        _factories = new Dictionary<Type, Func<object>>
        {
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.VocabularySearchTool)] = () => fixture.VocabularySearchTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.VocabularyWordDetailTool)] = () => fixture.VocabularyWordDetailTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.SkillListTool)] = () => fixture.SkillListTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.SkillDetailTool)] = () => fixture.SkillDetailTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.LearningResourceListTool)] = () => fixture.LearningResourceListTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.LearningResourceDetailTool)] = () => fixture.LearningResourceDetailTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.CurrentProfileSummaryTool)] = () => fixture.CurrentProfileSummaryTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.LearnerSettingsSummaryTool)] = () => fixture.LearnerSettingsSummaryTool,
            [typeof(SentenceStudio.Api.Coach.Tools.SamTools.CurrentPlanSummaryTool)] = () => fixture.CurrentPlanSummaryTool
        };
    }

    /// <summary>Adds a service the seam resolves — a recorder, a sink, an extra observer.</summary>
    public FixtureToolServiceProvider With<T>(T instance) where T : class
    {
        _factories[typeof(T)] = () => instance;
        return this;
    }

    /// <summary>Adds an observer the seam picks up through <c>GetServices</c>.</summary>
    public FixtureToolServiceProvider WithObserver(ICoachToolCallObserver observer)
    {
        _extras.Add(observer);
        return this;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IEnumerable<ICoachToolCallObserver>))
        {
            return _extras.Cast<ICoachToolCallObserver>().ToArray();
        }

        return _factories.TryGetValue(serviceType, out var factory) ? factory() : null;
    }
}

/// <summary>Records every observation the seam publishes.</summary>
internal sealed class RecordingToolCallObserver : ICoachToolCallObserver
{
    public List<CoachToolCallObservation> Observations { get; } = new();

    public ValueTask OnCompletedAsync(
        CoachToolCallObservation observation, CancellationToken cancellationToken)
    {
        Observations.Add(observation);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A thread-safe recorder, for the concurrency cases.</summary>
internal sealed class ConcurrentRecordingObserver : ICoachToolCallObserver
{
    private readonly object _gate = new();
    private readonly List<CoachToolCallObservation> _observations = new();

    public IReadOnlyList<CoachToolCallObservation> Snapshot()
    {
        lock (_gate)
        {
            return _observations.ToArray();
        }
    }

    public ValueTask OnCompletedAsync(
        CoachToolCallObservation observation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _observations.Add(observation);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>An observer that always throws, to prove the seam's guard.</summary>
internal sealed class ThrowingToolCallObserver : ICoachToolCallObserver
{
    public int Calls { get; private set; }

    public ValueTask OnCompletedAsync(
        CoachToolCallObservation observation, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("A subscriber must never be able to break a tool call.");
    }
}

/// <summary>An observer that cancels, which is the guard's hardest case.</summary>
internal sealed class CancellingToolCallObserver : ICoachToolCallObserver
{
    public ValueTask OnCompletedAsync(
        CoachToolCallObservation observation, CancellationToken cancellationToken) =>
        throw new OperationCanceledException("Cancellation must not escape an observer either.");
}

/// <summary>
/// The shared turn-observation seam.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists so that W3's evidence projection and W4's trace summary read <em>one</em>
/// capture of what a turn did. Two collectors would have meant two DTOs, two edits to the same
/// contested factory, and two answers to "what did this turn read". Every test here is ultimately
/// about that: one observation per call, carrying facts both projections can use and nothing
/// either of them would have to redact.
/// </para>
/// <para>
/// The sweeps are written against the <b>full enabled registry</b> and assert their own census. A
/// guard that passes over zero tools is a failure, and a guard that passes over five of fourteen is
/// the same failure wearing a smaller number.
/// </para>
/// </remarks>
public sealed class CoachToolObservationSeamTests : IDisposable
{
    private readonly CoachToolTestFixture _fixture = new();

    public CoachToolObservationSeamTests()
    {
        var user = CoachToolTestFixture.UserA;
        _fixture.SeedProfile(user);
        _fixture.SeedSkill(user);
        _fixture.SeedPlan(user);
        _fixture.SeedResource(user, title: "Resource");

        var word = _fixture.SeedWord("사과", "apple", tags: "food");
        _fixture.SeedProgress(user, word.Id);

        _fixture.SeedCompletion(user, "Reading", minutesSpent: 10, daysAgo: 0);
    }

    public void Dispose() => _fixture.Dispose();

    private static ICoachToolRegistry FullRegistry() =>
        CoachToolServiceCollectionExtensions.BuildValidatedRegistry(new CoachOptions
        {
            SamOverlay = new CoachFeatureSwitch { Enabled = true },
            SamReadTools = new CoachFeatureSwitch { Enabled = true },
            SamWriteTools = new CoachFeatureSwitch { Enabled = false }
        });

    private IReadOnlyList<AIFunction> BuildTools(IServiceProvider provider, ICoachToolRegistry registry) =>
        new CoachToolFactory(
                _fixture.ProfileTool,
                _fixture.BalanceTool,
                _fixture.VocabularyTool,
                _fixture.ResourceTool,
                _fixture.PreviewTool(),
                _fixture.HistorySummaryTool,
                registry,
                provider)
            .CreateTools();

    // ================================================================== full-registry sweep

    /// <summary>
    /// Every enabled read produces exactly one observation, and the sweep proves its own census.
    /// </summary>
    /// <remarks>
    /// The census assertion is the point. "Every tool observed" over an empty or partial tool set is
    /// a sentence that is true and worthless, and it is the exact failure the W2 budget guard was
    /// rejected for. The expected count is derived from the registry, so a tool added tomorrow is
    /// swept without anybody remembering to add it here — and a tool that stops being wrapped fails
    /// this test rather than quietly leaving the trace.
    /// </remarks>
    [Fact]
    public async Task Every_enabled_read_produces_exactly_one_observation()
    {
        var registry = FullRegistry();
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);

        var tools = BuildTools(provider, registry);

        var expected = registry.Enabled
            .Where(r => r.RiskClass == CoachToolRiskClass.Read)
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        expected.Should().NotBeEmpty("a sweep over nothing proves nothing");
        expected.Count.Should().BeGreaterThan(
            5, "the sweep must cover the Sam reads, not only the five core tools");

        var invoked = 0;
        foreach (var tool in tools.Where(t => expected.Contains(t.Name)))
        {
            await InvokeSweepAsync(tool);
            invoked++;
        }

        invoked.Should().Be(
            expected.Count,
            "every enabled read must be present in the built tool set, or the sweep is skipping one");

        observer.Observations.Should().HaveCount(
            expected.Count, "exactly one observation per call, no more and no fewer");

        observer.Observations.Select(o => o.ToolName).Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// No enabled tool produces an argument the mask has no member for.
    /// </summary>
    /// <remarks>
    /// The mask's own non-vacuity guard. <see cref="CoachToolArgumentMask.Unrecognized"/> exists so
    /// a tool that grows a new argument cannot silently fall out of the mask's vocabulary; this is
    /// what makes that flag load-bearing rather than decorative.
    /// </remarks>
    [Fact]
    public async Task No_enabled_tool_produces_an_unrecognized_argument()
    {
        var registry = FullRegistry();
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);

        var tools = BuildTools(provider, registry);
        var reads = registry.Enabled
            .Where(r => r.RiskClass == CoachToolRiskClass.Read)
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var tool in tools.Where(t => reads.Contains(t.Name)))
        {
            await InvokeSweepAsync(tool);
        }

        observer.Observations.Should().NotBeEmpty();
        observer.Observations.Should().OnlyContain(
            o => (o.ArgumentMask & CoachToolArgumentMask.Unrecognized) == 0,
            "an unrecognized argument means the mask has fallen behind the tool set");
    }

    // ============================================================================ ordinals

    /// <summary>Ordinals are 1-based, unique, and dense across the turn.</summary>
    /// <remarks>
    /// One-based because the value is read by humans in a trace and by the model in a summary, and
    /// "the first tool call" reading as call 0 is a footnote nobody remembers. Dense because a gap
    /// would imply a call the trace lost.
    /// </remarks>
    [Fact]
    public async Task Ordinals_are_one_based_and_dense_within_a_turn()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var tools = BuildTools(provider, FullRegistry());

        var profile = tools.Single(t => t.Name == CoachToolNames.GetLearnerProfileSummary);

        for (var i = 0; i < 4; i++)
        {
            await InvokeAsync(profile);
        }

        observer.Observations.Select(o => o.Ordinal).Should().Equal(1, 2, 3, 4);
    }

    /// <summary>One turn's ordinals are shared across every tool in the set, not per tool.</summary>
    [Fact]
    public async Task Ordinals_are_shared_across_the_whole_tool_set()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var tools = BuildTools(provider, FullRegistry());

        await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetLearnerProfileSummary));
        await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetResourceCatalog));
        await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetLearnerProfileSummary));

        observer.Observations.Select(o => o.Ordinal).Should().Equal(
            [1, 2, 3], "the ordinal describes the turn, not the tool");
    }

    /// <summary>A second tool set is a second turn, and starts again at one.</summary>
    [Fact]
    public async Task A_new_tool_set_starts_a_new_turn()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var registry = FullRegistry();

        var first = BuildTools(provider, registry);
        await InvokeAsync(first.Single(t => t.Name == CoachToolNames.GetLearnerProfileSummary));

        var second = BuildTools(provider, registry);
        await InvokeAsync(second.Single(t => t.Name == CoachToolNames.GetLearnerProfileSummary));

        observer.Observations.Select(o => o.Ordinal).Should().Equal(1, 1);
    }

    // ============================================================================== budget

    /// <summary>
    /// A budget-exhausted call produces zero observations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single most important ordering property in this batch. <c>BudgetedAIFunction</c> wraps
    /// the seam from the outside and calls <c>Consume</c> <em>before</em> the inner delegate, so a
    /// refused call never reaches the seam at all. The refusal is counted once at the turn boundary
    /// from <c>CoachToolCallBudget.Used</c>.
    /// </para>
    /// <para>
    /// If the seam ever moved outside the budget wrapper this test fails, and the symptom it
    /// prevents is a trace that reports twenty tool failures for a turn in which one limit was hit.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_budget_exhausted_call_produces_no_observation()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var tools = BuildTools(provider, FullRegistry());

        var (budgeted, budget) = CoachToolCallBudget.Apply(tools, limit: 2);
        var profile = budgeted.Single(t => t.Name == CoachToolNames.GetLearnerProfileSummary);

        await InvokeAsync(profile);
        await InvokeAsync(profile);

        observer.Observations.Should().HaveCount(2, "the two in-budget calls are observed normally");

        var refused = await Assert.ThrowsAsync<CoachToolException>(() => InvokeAsync(profile).AsTask());

        refused.Kind.Should().Be(CoachToolFailureKind.BudgetExhausted);

        // The shipped Consume increments and then checks, so the refused attempt is counted. That
        // is the unowned budget wrapper's semantics and this batch does not change it; what matters
        // here is that the refusal is counted THERE and nowhere else.
        budget.Used.Should().Be(3);

        observer.Observations.Should().HaveCount(
            2,
            "the seam nests inside the budget wrapper, so a refused call never reaches it — "
            + "counting it here would double-count the one refusal the turn boundary already records");

        observer.Observations.Should().NotContain(
            o => o.FailureKind == CoachToolFailureKind.BudgetExhausted);
    }

    // ============================================================================ outcomes

    [Fact]
    public async Task A_successful_read_is_observed_as_succeeded_with_its_scope()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var tools = BuildTools(provider, FullRegistry());

        await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetResourceCatalog));

        var single = observer.Observations.Should().ContainSingle().Subject;

        single.Outcome.Should().Be(CoachToolCallOutcome.Succeeded);
        single.FailureKind.Should().BeNull();
        single.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);

        single.Scope.Should().NotBeNull(
            "the scope is the single capture W3's evidence and W4's trace both project from");
        single.Scope!.Coverage.Should().NotBe(CoachScopeCoverage.Unspecified);
    }

    /// <summary>
    /// The captured scope is the real object, foundation members and all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion that distinguishes a working capture from a plausible one.
    /// <c>AIFunctionFactory</c> marshals the result to JSON before any wrapper sees it, and the
    /// scope's model-facing projection deliberately omits six foundation members —
    /// <c>DefinitionCode</c> among them. A seam that re-read the scope out of the marshalled JSON
    /// would satisfy every other test in this file and hand W3's evidence projection a
    /// <c>DefinitionCode</c> of <c>Unspecified</c> on every turn.
    /// </para>
    /// <para>
    /// So this asserts on a member that <em>only</em> survives if the capture happened before
    /// marshalling.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_captured_scope_carries_members_the_marshalled_projection_omits()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var tools = BuildTools(provider, FullRegistry());

        await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetResourceCatalog));

        var scope = observer.Observations.Should().ContainSingle().Subject.Scope;

        scope.Should().NotBeNull();
        scope!.DefinitionCode.Should().NotBe(
            CoachScopeDefinition.Unspecified,
            "DefinitionCode is [JsonIgnore] on the model-facing projection, so a scope recovered "
            + "from the marshalled JSON would read Unspecified here — W3's evidence shape needs it");
        scope.ClockBasis.Should().NotBe(CoachScopeClockBasis.Unspecified);
        scope.ReferenceMode.Should().NotBe(CoachScopeReferenceMode.Unspecified);
    }

    /// <summary>
    /// The capture converter changes no byte the model reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The converter is attached to the tool factory's serializer options, which is the path every
    /// tool result is marshalled through. It records a reference and delegates; this proves the
    /// delegation is exact rather than merely intended.
    /// </para>
    /// <para>
    /// Worth its own test because the pinned-projection tests serialize with
    /// <c>AIJsonUtilities.DefaultOptions</c> directly and would not notice a converter attached
    /// here — the one place a change to the model's view could have slipped through unpinned.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_capture_converter_emits_the_same_json_it_would_have_without_it()
    {
        var scope = new CoachResultScope
        {
            Coverage = CoachScopeCoverage.WindowBounded,
            Order = CoachScopeOrder.MinutesDescending,
            OrderHonored = true,
            Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.DateWindow,
            AsOfUtc = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc),
            WindowStartDate = new DateOnly(2026, 8, 8),
            WindowEndDate = new DateOnly(2026, 8, 14),
            ReturnedCount = 2,
            MatchedCount = 3,
            WithheldCount = 1,
            WithheldReason = CoachScopeWithheldReason.BelowMinimumEvidence,
            DefinitionCode = CoachScopeDefinition.PracticeWindowBalance
        };

        var plain = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);

        var captured = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        captured.Converters.Add(new CoachResultScopeCaptureConverter(captured));

        JsonSerializer.Serialize(scope, captured)
            .Should().Be(
                JsonSerializer.Serialize(scope, plain),
                "the converter observes the serialization path; it does not participate in it");
    }

    /// <summary>The capture records the scope even when nobody installed a box.</summary>
    /// <remarks>
    /// A tool marshalled outside an observed invocation — anything that serializes a result for its
    /// own reasons — must not fault on a null slot.
    /// </remarks>
    [Fact]
    public void Serializing_a_scope_outside_an_observed_call_is_harmless()
    {
        CoachToolScopeCapture.End();

        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new CoachResultScopeCaptureConverter(options));

        var act = () => JsonSerializer.Serialize(
            new CoachResultScope
            {
                Coverage = CoachScopeCoverage.DerivedProjection,
                Order = CoachScopeOrder.NotApplicable,
                OrderHonored = true,
                Filters = CoachScopeFilters.OwnerScoped,
                AsOfUtc = DateTime.UtcNow,
                ReturnedCount = 0
            },
            options);

        act.Should().NotThrow();
    }

    /// <summary>A bounded refusal is observed as refused, carrying its typed kind.</summary>
    [Fact]
    public async Task A_bounded_refusal_is_observed_with_its_kind()
    {
        var observer = new RecordingToolCallObserver();
        var registration = ReadRegistration();
        var inner = ThrowingFunction(new CoachToolException(
            CoachToolFailureKind.ProfileMissing, registration.Name, "No profile."));

        var observed = new CoachObservedFunction(
            inner, registration, [observer], new CoachToolCallSequence());

        await Assert.ThrowsAsync<CoachToolException>(() =>
            observed.InvokeAsync(new AIFunctionArguments()).AsTask());

        var single = observer.Observations.Should().ContainSingle().Subject;
        single.Outcome.Should().Be(CoachToolCallOutcome.Refused);
        single.FailureKind.Should().Be(CoachToolFailureKind.ProfileMissing);
        single.Scope.Should().BeNull("a refusal produced no answer, so it stated no scope");
    }

    /// <summary>
    /// An untyped throw is observed as faulted, and the exception itself never leaves the seam.
    /// </summary>
    /// <remarks>
    /// The case most likely to carry a provider message with learner text echoed back inside it, so
    /// the observation records only that a fault occurred. <c>FailureKind</c> stays null because an
    /// untyped fault has no closed kind, and inventing one would make a catch-all bucket look like
    /// a diagnosis.
    /// </remarks>
    [Fact]
    public async Task An_untyped_fault_is_observed_without_the_exception()
    {
        var observer = new RecordingToolCallObserver();
        var registration = ReadRegistration();
        var inner = ThrowingFunction(new InvalidOperationException("learner text 사과 echoed back"));

        var observed = new CoachObservedFunction(
            inner, registration, [observer], new CoachToolCallSequence());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            observed.InvokeAsync(new AIFunctionArguments()).AsTask());

        var single = observer.Observations.Should().ContainSingle().Subject;
        single.Outcome.Should().Be(CoachToolCallOutcome.Faulted);
        single.FailureKind.Should().BeNull();
    }

    // ============================================================================== guards

    /// <summary>A throwing observer cannot break the tool call.</summary>
    [Fact]
    public async Task A_throwing_observer_cannot_change_what_the_model_is_told()
    {
        var thrower = new ThrowingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(thrower);
        var tools = BuildTools(provider, FullRegistry());

        var result = await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetResourceCatalog));

        thrower.Calls.Should().Be(1, "the observer really did run");
        result.Should().NotBeNull("and the answer survived it");
    }

    /// <summary>A cancelling observer cannot turn an answer into a cancellation.</summary>
    [Fact]
    public async Task A_cancelling_observer_cannot_cancel_the_tool_call()
    {
        var provider = new FixtureToolServiceProvider(_fixture)
            .WithObserver(new CancellingToolCallObserver());

        var tools = BuildTools(provider, FullRegistry());

        var result = await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetResourceCatalog));

        result.Should().NotBeNull();
    }

    /// <summary>
    /// One bad subscriber does not silence the others.
    /// </summary>
    /// <remarks>
    /// Guarded per observer rather than once around the loop. A single try would let subscriber 1
    /// throwing hide the call from subscriber 2 — which is how a trace ends up missing exactly the
    /// turns something went wrong in.
    /// </remarks>
    [Fact]
    public async Task A_failing_subscriber_does_not_hide_the_call_from_the_others()
    {
        var good = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture)
            .WithObserver(new ThrowingToolCallObserver())
            .WithObserver(good);

        var tools = BuildTools(provider, FullRegistry());
        await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetResourceCatalog));

        good.Observations.Should().ContainSingle();
    }

    // ================================================================== subscriber ordering

    /// <summary>
    /// The opportunity ledger is subscriber 1, ahead of the buffer and anything registered.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed. An explicit list was chosen over a set precisely so this is
    /// testable: "the ledger sees it first" is a property somebody may one day depend on, and an
    /// implicit container order is one nobody can check.
    /// </remarks>
    [Fact]
    public async Task The_opportunity_ledger_is_subscriber_one_and_the_buffer_is_subscriber_two()
    {
        var order = new List<string>();
        var recorder = new OrderRecordingOpportunityRecorder(order);
        var buffer = new CoachTurnObservationBuffer();

        var provider = new FixtureToolServiceProvider(_fixture)
            .With<ICoachOpportunityRecorder>(recorder)
            .With<ICoachTurnObservationSink>(new OrderRecordingSink(buffer, order))
            .WithObserver(new OrderRecordingObserver(order));

        var tools = BuildTools(provider, FullRegistry());

        // A refusal, because subscriber 1 only records refusals — a success would leave it silent
        // and the ordering unobservable.
        var detail = tools.Single(t => t.Name == CoachToolNames.GetSkillDetail);
        await Assert.ThrowsAsync<CoachToolException>(() =>
            detail.InvokeAsync(new AIFunctionArguments { ["skillId"] = "missing-skill" }).AsTask());

        order.Should().Equal("opportunity", "buffer", "registered");
    }

    /// <summary>The buffer collects every outcome, in ordinal order.</summary>
    [Fact]
    public async Task The_buffer_collects_every_outcome_in_order()
    {
        var buffer = new CoachTurnObservationBuffer();
        var provider = new FixtureToolServiceProvider(_fixture)
            .With<ICoachTurnObservationSink>(buffer);

        var tools = BuildTools(provider, FullRegistry());

        await InvokeAsync(tools.Single(t => t.Name == CoachToolNames.GetResourceCatalog));

        var detail = tools.Single(t => t.Name == CoachToolNames.GetSkillDetail);
        await Assert.ThrowsAsync<CoachToolException>(() =>
            detail.InvokeAsync(new AIFunctionArguments { ["skillId"] = "missing-skill" }).AsTask());

        buffer.Observations.Should().HaveCount(
            2, "a turn that refused half its calls is exactly the turn a trace has to explain");

        buffer.Observations.Select(o => o.Ordinal).Should().Equal(1, 2);
        buffer.Observations[0].Outcome.Should().Be(CoachToolCallOutcome.Succeeded);
        buffer.Observations[1].Outcome.Should().Be(CoachToolCallOutcome.Refused);
    }

    /// <summary>With no observers at all, the tools are returned untouched.</summary>
    /// <remarks>
    /// Every existing tool test constructs the factory with a bare provider, so the allow-list
    /// contract and the schema tests must see exactly the functions they saw before the seam
    /// existed.
    /// </remarks>
    [Fact]
    public void With_no_observers_the_tools_are_not_wrapped()
    {
        var tools = BuildTools(new FixtureToolServiceProvider(_fixture), FullRegistry());

        tools.Should().NotBeEmpty();
        tools.Should().NotContain(t => t is CoachObservedFunction);
    }

    // =========================================================================== no leaks

    /// <summary>
    /// The tool name is the registration's, never anything the model supplied.
    /// </summary>
    [Fact]
    public async Task The_tool_name_comes_from_the_registration()
    {
        var observer = new RecordingToolCallObserver();
        var registration = ReadRegistration();

        // An inner function whose own name differs from the registration's. Only a seam that reads
        // the registration can produce the right answer here.
        var inner = AIFunctionFactory.Create(
            () => "ok", "a_name_the_model_invented", "Inner.");

        var observed = new CoachObservedFunction(
            inner, registration, [observer], new CoachToolCallSequence());

        await observed.InvokeAsync(new AIFunctionArguments());

        observer.Observations.Should().ContainSingle()
            .Which.ToolName.Should().Be(registration.Name);
    }

    /// <summary>
    /// The observation carries no free-text member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape rule, checked by reflection so it cannot be argued with. <c>ToolName</c> is the
    /// one string, and it is a build-time constant from the registration — a model that invents a
    /// name cannot widen it.
    /// </para>
    /// <para>
    /// <c>Scope</c> and <c>SubjectCode</c> are the two non-primitive members and both are
    /// deliberate: the scope is the shared capture, in-memory only, and the subject code is a value
    /// type whose only constructor runs the closed-set gate. A new member that is a bare
    /// <c>string</c> or <c>object</c> fails here, which is the intended answer to "let me just add
    /// a field for debugging".
    /// </para>
    /// </remarks>
    [Fact]
    public void The_observation_record_carries_no_free_text()
    {
        var members = typeof(CoachToolCallObservation)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToList();

        members.Should().NotBeEmpty();

        var allowed = new HashSet<Type>
        {
            typeof(int), typeof(int?),
            typeof(CoachToolCallOutcome),
            typeof(CoachToolFailureKind?),
            typeof(CoachToolArgumentMask),
            typeof(CoachResultScope),
            typeof(CoachToolSubjectCode?)
        };

        foreach (var member in members)
        {
            if (member.Name == nameof(CoachToolCallObservation.ToolName))
            {
                member.PropertyType.Should().Be(
                    typeof(string), "the tool name is the one string, and it is a server constant");
                continue;
            }

            allowed.Should().Contain(
                member.PropertyType,
                $"{member.Name} is a member type this record does not permit — a free-text member "
                + "here is a transcript with extra steps");
        }
    }

    /// <summary>
    /// The argument mask records presence and never a value.
    /// </summary>
    /// <remarks>
    /// Driven with values that would be unmistakable if any of them leaked: a search string that
    /// reads like learner text, and an identifier. Only the flags may differ between the two calls.
    /// </remarks>
    [Fact]
    public async Task The_argument_mask_records_presence_and_never_a_value()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var tools = BuildTools(provider, FullRegistry());

        var search = tools.Single(t => t.Name == CoachToolNames.ListUserVocabularies);
        await search.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "사과 the learner typed this",
            ["maxResults"] = 3
        });

        var single = observer.Observations.Should().ContainSingle().Subject;

        single.ArgumentMask.Should().Be(
            CoachToolArgumentMask.Query | CoachToolArgumentMask.MaxResults);

        single.ToString().Should().NotContain("사과");
        single.ToString().Should().NotContain("the learner typed this");
    }

    /// <summary>An absent optional argument is not reported as present.</summary>
    /// <remarks>
    /// A supplied-but-null optional is an argument the model chose not to use. Recording it would
    /// report a default the tool applied as a decision the model made.
    /// </remarks>
    [Fact]
    public async Task An_absent_or_null_argument_is_not_recorded_as_present()
    {
        var observer = new RecordingToolCallObserver();
        var provider = new FixtureToolServiceProvider(_fixture).WithObserver(observer);
        var tools = BuildTools(provider, FullRegistry());

        var search = tools.Single(t => t.Name == CoachToolNames.ListUserVocabularies);
        await search.InvokeAsync(new AIFunctionArguments { ["query"] = null, ["maxResults"] = null });

        observer.Observations.Should().ContainSingle()
            .Which.ArgumentMask.Should().Be(CoachToolArgumentMask.None);
    }

    /// <summary>
    /// A setting name the model invented collapses to the unknown bucket.
    /// </summary>
    /// <remarks>
    /// The subject code is the one model-influenced fact the observation carries, and this is the
    /// failing fixture for it: an invented name must be indistinguishable from an absent one.
    /// </remarks>
    [Theory]
    [InlineData("; DROP TABLE CoachOpportunity; --")]
    [InlineData("a_setting_that_does_not_exist")]
    [InlineData("")]
    [InlineData(null)]
    public void An_invented_setting_name_collapses_to_unknown(string? invented)
    {
        var code = CoachToolSubjectCode.ForPreferenceSetting(invented);

        code.IsKnown.Should().BeFalse();
        code.Value.Should().BeNull("the model's string is discarded, not stored");
        code.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.PreferenceSettingUnknown);
    }

    /// <summary>A server-owned candidate survives the collapse, so the ledger keeps its signal.</summary>
    [Fact]
    public void A_server_owned_setting_name_survives_the_collapse()
    {
        var candidate = SentenceStudio.Api.Coach.Operations.Handlers
            .CoachPreferenceChangeHandler.CandidateNames[0];

        var code = CoachToolSubjectCode.ForPreferenceSetting(candidate);

        code.IsKnown.Should().BeTrue();
        code.Value.Should().Be(candidate);
        code.CapabilityCode.Should().Be(
            CoachOpportunityCapabilityCodes.ForPreferenceSetting(candidate),
            "the code the ledger records must be the one it recorded before the seam was generalized");
    }

    // ====================================================================== concurrency

    /// <summary>
    /// A scoped envelope whose scope is distinguishable per caller.
    /// </summary>
    /// <remarks>
    /// Synthetic on purpose. The question Simon asked is about the capture — whether two flows in
    /// flight can end up holding each other's scope — and answering it through the real tools would
    /// drag in the fixture's single <c>ApplicationDbContext</c>, which EF Core forbids using
    /// concurrently. That produced a flaky failure whose cause was SQLite, not the seam, and a
    /// concurrency test that fails for an unrelated reason is worse than none.
    /// </remarks>
    private sealed record ProbeResult(CoachResultScope Scope) : ICoachScopedResult;

    /// <summary>
    /// An observed function over a synthetic tool, marshalled exactly as production marshals.
    /// </summary>
    /// <remarks>
    /// Built with the same serializer options the factory uses — capture converter included — so
    /// the scope travels the same path it travels in a real call: through the marshaller, where the
    /// converter is the only thing that still sees the object.
    /// </remarks>
    private static CoachObservedFunction ProbeFunction(
        string toolName,
        CoachToolCallSequence sequence,
        IReadOnlyList<ICoachToolCallObserver> observers,
        Func<CoachResultScope> scope,
        Func<Task>? interleave = null)
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new CoachResultScopeCaptureConverter(options));

        Func<Task<ProbeResult>> body = async () =>
        {
            if (interleave is not null)
            {
                await interleave();
            }

            return new ProbeResult(scope());
        };

        var inner = AIFunctionFactory.Create(body, new AIFunctionFactoryOptions
        {
            Name = toolName,
            Description = "Probe.",
            SerializerOptions = options
        });

        return new CoachObservedFunction(
            inner,
            new CoachToolRegistration
            {
                Name = toolName,
                ResultType = typeof(ProbeResult),
                RiskClass = CoachToolRiskClass.Read,
                Description = "Probe."
            },
            observers,
            sequence);
    }

    private static CoachResultScope ScopeWith(
        CoachScopeCoverage coverage, CoachScopeDefinition definition, int returned) => new()
    {
        Coverage = coverage,
        Order = CoachScopeOrder.Unordered,
        OrderHonored = true,
        Filters = CoachScopeFilters.OwnerScoped,
        AsOfUtc = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc),
        ReturnedCount = returned,
        DefinitionCode = definition
    };

    /// <summary>
    /// Two interleaved invocations retain distinct scopes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capture stores the scope in an <see cref="AsyncLocal{T}"/> box installed before the
    /// inner call, and an <see cref="AsyncLocal{T}"/> is per async flow rather than per call — so
    /// "two calls in flight cannot see each other's scope" is a claim about how the runtime flows
    /// context, not something the code states. Worth proving rather than reasoning about.
    /// </para>
    /// <para>
    /// The interleaving is forced, not hoped for: each call parks on a barrier <em>inside</em> the
    /// tool body, so both capture boxes are installed and both inner delegates are mid-flight before
    /// either result is marshalled. That is the exact window in which a shared box would swap the
    /// two scopes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_interleaved_invocations_retain_distinct_scopes()
    {
        var observer = new ConcurrentRecordingObserver();
        var sequence = new CoachToolCallSequence();

        using var bothInside = new Barrier(2);
        Task Park() => Task.Run(() => bothInside.SignalAndWait());

        var first = ProbeFunction(
            "probe_one", sequence, [observer],
            () => ScopeWith(CoachScopeCoverage.WindowBounded, CoachScopeDefinition.PracticeWindowBalance, 1),
            Park);

        var second = ProbeFunction(
            "probe_two", sequence, [observer],
            () => ScopeWith(CoachScopeCoverage.CompleteOwnedSet, CoachScopeDefinition.OwnedResourceList, 2),
            Park);

        await Task.WhenAll(
            first.InvokeAsync(new AIFunctionArguments()).AsTask(),
            second.InvokeAsync(new AIFunctionArguments()).AsTask());

        var observations = observer.Snapshot();
        observations.Should().HaveCount(2);

        var one = observations.Single(o => o.ToolName == "probe_one");
        var two = observations.Single(o => o.ToolName == "probe_two");

        one.Scope.Should().NotBeNull();
        two.Scope.Should().NotBeNull();

        one.Scope!.Coverage.Should().Be(
            CoachScopeCoverage.WindowBounded, "each call keeps the scope its own body produced");
        two.Scope!.Coverage.Should().Be(CoachScopeCoverage.CompleteOwnedSet);

        one.Scope.DefinitionCode.Should().Be(CoachScopeDefinition.PracticeWindowBalance);
        two.Scope.DefinitionCode.Should().Be(CoachScopeDefinition.OwnedResourceList);

        ReferenceEquals(one.Scope, two.Scope).Should().BeFalse(
            "a shared capture box would hand both observations the same object");

        one.Ordinal.Should().NotBe(two.Ordinal);
    }

    /// <summary>
    /// Eight concurrent invocations each keep their own scope, and the ordinals stay unique.
    /// </summary>
    /// <remarks>
    /// The widening of the case above. Every flow goes through one shared
    /// <see cref="CoachToolCallSequence"/> and one converter instance, and every one of them parks
    /// inside its body until all eight are in flight — so a capture that raced shows up either as a
    /// scope belonging to the wrong caller or as a duplicated ordinal. Both are asserted absent, and
    /// the per-caller scope value is what makes "the wrong caller" detectable at all.
    /// </remarks>
    [Fact]
    public async Task Concurrent_invocations_never_lose_or_swap_a_scope()
    {
        const int callers = 8;

        var observer = new ConcurrentRecordingObserver();
        var sequence = new CoachToolCallSequence();

        using var allInside = new Barrier(callers);
        Task Park() => Task.Run(() => allInside.SignalAndWait());

        var functions = Enumerable.Range(0, callers)
            .Select(i => ProbeFunction(
                $"probe_{i}", sequence, [observer],
                () => ScopeWith(
                    CoachScopeCoverage.PageOfOwnedSet, CoachScopeDefinition.OwnedResourceList, i),
                Park))
            .ToList();

        await Task.WhenAll(functions.Select(f => f.InvokeAsync(new AIFunctionArguments()).AsTask()));

        var observations = observer.Snapshot();

        observations.Should().HaveCount(callers);
        observations.Should().OnlyContain(
            o => o.Scope != null, "a raced capture shows up as a call that stated no scope");

        // ReturnedCount is the per-caller marker: caller i produced a scope carrying i, so a swap
        // is visible as a tool name paired with somebody else's number.
        foreach (var observation in observations)
        {
            var expected = int.Parse(observation.ToolName["probe_".Length..]);
            observation.Scope!.ReturnedCount.Should().Be(
                expected, "{0} must hold the scope its own body produced", observation.ToolName);
        }

        observations.Select(o => o.Ordinal).Should().OnlyHaveUniqueItems();
        observations.Select(o => o.Ordinal).OrderBy(o => o).Should().Equal(
            Enumerable.Range(1, callers));
    }

    // ============================================================================ helpers

    private static ValueTask<object?> InvokeAsync(AIFunction tool) =>
        tool.InvokeAsync(new AIFunctionArguments());

    /// <summary>
    /// Invokes one tool for the sweep, supplying the arguments it requires and tolerating a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep's claim is "exactly one observation per call", and that has to hold whatever the
    /// call returned — a read that refuses because the fixture has no such row is still a call the
    /// trace must account for. Swallowing the refusal here is what lets the census cover every
    /// enabled read instead of only the ones a fixture happens to satisfy.
    /// </para>
    /// <para>
    /// The required arguments are supplied because a missing one fails inside the marshaller, which
    /// would still produce one observation but would be testing the harness rather than the tools.
    /// </para>
    /// </remarks>
    private static async Task InvokeSweepAsync(AIFunction tool)
    {
        var arguments = new AIFunctionArguments();

        switch (tool.Name)
        {
            case CoachToolNames.GetPracticeBalance:
                arguments["window"] = CoachPracticeWindow.SevenDays;
                break;
            case CoachToolNames.GetVocabularyWordDetail:
                arguments["wordId"] = "sweep-word";
                break;
            case CoachToolNames.GetSkillDetail:
                arguments["skillId"] = "sweep-skill";
                break;
            case CoachToolNames.GetLearningResourceDetail:
                arguments["resourceId"] = "sweep-resource";
                break;
        }

        try
        {
            await tool.InvokeAsync(arguments);
        }
        catch (CoachToolException)
        {
            // A bounded refusal is a completed call and is observed as one.
        }
    }

    private static CoachToolRegistration ReadRegistration() => new()
    {
        Name = CoachToolNames.GetLearnerProfileSummary,
        ResultType = typeof(object),
        RiskClass = CoachToolRiskClass.Read,
        Description = "A read."
    };

    private static AIFunction ThrowingFunction(Exception toThrow)
    {
        Func<string> throws = () => throw toThrow;
        return AIFunctionFactory.Create(
            throws, CoachToolNames.GetLearnerProfileSummary, "Throws.");
    }

    private sealed class OrderRecordingOpportunityRecorder : ICoachOpportunityRecorder
    {
        private readonly List<string> _order;

        public OrderRecordingOpportunityRecorder(List<string> order) => _order = order;

        public ValueTask RecordAsync(CoachOpportunitySignal signal, CancellationToken cancellationToken = default)
        {
            _order.Add("opportunity");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OrderRecordingSink : ICoachTurnObservationSink
    {
        private readonly ICoachTurnObservationSink _inner;
        private readonly List<string> _order;

        public OrderRecordingSink(ICoachTurnObservationSink inner, List<string> order)
        {
            _inner = inner;
            _order = order;
        }

        public void Add(CoachToolCallObservation observation)
        {
            _order.Add("buffer");
            _inner.Add(observation);
        }

        public void RecordBudget(int used, int limit) => _inner.RecordBudget(used, limit);
    }

    private sealed class OrderRecordingObserver : ICoachToolCallObserver
    {
        private readonly List<string> _order;

        public OrderRecordingObserver(List<string> order) => _order = order;

        public ValueTask OnCompletedAsync(
            CoachToolCallObservation observation, CancellationToken cancellationToken)
        {
            _order.Add("registered");
            return ValueTask.CompletedTask;
        }
    }
}
