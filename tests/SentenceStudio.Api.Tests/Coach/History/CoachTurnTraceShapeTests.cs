using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// What a stored turn trace is allowed to contain.
/// </summary>
/// <remarks>
/// <para>
/// The trace is written into a protected column on a learner's conversation. Its whole claim is
/// that it holds no content — so the claim is checked against the type declaration rather than
/// against the code that fills it, because the filler is what changes.
/// </para>
/// <para>
/// The rule this enforces is the one that decays first: somebody adds a <c>string Detail</c> "just
/// for debugging", it ships, and a year later the column holds a query the learner typed. The
/// reflection sweep fails the build on the day that member is added.
/// </para>
/// </remarks>
public sealed class CoachTurnTraceShapeTests
{
    private static IReadOnlyList<PropertyInfo> Members(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToList();

    /// <summary>
    /// Every trace member is a closed code, a count, a flag, or the registered tool name.
    /// </summary>
    /// <remarks>
    /// The census assertion at the end is what keeps this non-vacuous: a sweep that examined zero
    /// members would pass silently, and a record whose members were all removed would too.
    /// </remarks>
    [Fact]
    public void The_trace_entry_carries_only_closed_codes_counts_and_the_tool_name()
    {
        var members = Members(typeof(CoachTurnTraceEntry));

        members.Should().HaveCount(
            13, "the trace shape is fixed; a new member is a deliberate decision, not a diff");

        var permitted = new HashSet<Type>
        {
            typeof(int), typeof(int?), typeof(bool),
            typeof(CoachToolCallOutcome),
            typeof(CoachToolFailureKind?),
            typeof(CoachToolArgumentMask),
            typeof(CoachScopeCoverage),
            typeof(CoachScopeDefinition),
            typeof(CoachScopeWithheldReason)
        };

        foreach (var member in members)
        {
            if (member.Name == nameof(CoachTurnTraceEntry.ToolName))
            {
                member.PropertyType.Should().Be(typeof(string));
                continue;
            }

            permitted.Should().Contain(
                member.PropertyType,
                $"{member.Name} is not a closed code, a count, or a flag — a member like that in a "
                + "protected column is a transcript with extra steps");
        }
    }

    /// <summary>The tool name is the only string anywhere in the trace.</summary>
    /// <remarks>
    /// Walked transitively, so a member whose <em>type</em> holds a string cannot smuggle one in.
    /// The scope is the specific thing being kept out: it rides the in-memory observation and stops
    /// at the projection.
    /// </remarks>
    [Fact]
    public void No_type_reachable_from_the_trace_can_hold_free_text()
    {
        var seen = new HashSet<Type>();
        var offenders = new List<string>();
        var queue = new Queue<Type>([typeof(CoachTurnTraceSummary)]);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var member in Members(type))
            {
                var memberType = member.PropertyType;

                if (memberType == typeof(string))
                {
                    if (member.Name != nameof(CoachTurnTraceEntry.ToolName))
                    {
                        offenders.Add($"{type.Name}.{member.Name}");
                    }

                    continue;
                }

                if (memberType == typeof(object))
                {
                    offenders.Add($"{type.Name}.{member.Name}");
                    continue;
                }

                foreach (var reachable in Reachable(memberType))
                {
                    queue.Enqueue(reachable);
                }
            }
        }

        seen.Should().Contain(
            typeof(CoachTurnTraceEntry), "the walk must actually have reached the entries");

        offenders.Should().BeEmpty();
    }

    /// <summary>The scope type is not reachable from anything that persists.</summary>
    /// <remarks>
    /// The structural half of no-leak boundary 4. Projecting closed codes out of the scope is a
    /// choice the projection makes; this is what stops a later change from persisting the object
    /// instead, which would put its six foundation members and its whole future shape into a
    /// protected column nobody versioned.
    /// </remarks>
    [Fact]
    public void The_result_scope_is_not_reachable_from_the_stored_outcome_trace()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([typeof(CoachTurnTraceSummary)]);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var member in Members(type))
            {
                foreach (var reachable in Reachable(member.PropertyType))
                {
                    queue.Enqueue(reachable);
                }
            }
        }

        seen.Should().NotContain(typeof(CoachResultScope));
        seen.Should().NotContain(typeof(CoachToolCallObservation));
        seen.Should().NotContain(typeof(CoachToolSubjectCode));
    }

    private static IEnumerable<Type> Reachable(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset) || underlying == typeof(decimal)
            || underlying == typeof(Guid))
        {
            yield break;
        }

        if (underlying.IsGenericType)
        {
            foreach (var argument in underlying.GetGenericArguments())
            {
                yield return argument;
            }

            yield break;
        }

        if (underlying.IsArray && underlying.GetElementType() is { } element)
        {
            yield return element;
            yield break;
        }

        yield return underlying;
    }
}

/// <summary>
/// What the projection keeps, and what it drops.
/// </summary>
public sealed class CoachTurnTraceProjectionTests
{
    private static CoachResultScope Scope() => new()
    {
        Coverage = CoachScopeCoverage.WindowBounded,
        Order = CoachScopeOrder.MinutesDescending,
        OrderHonored = true,
        Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.DateWindow,
        AsOfUtc = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc),
        WindowStartDate = new DateOnly(2026, 8, 8),
        WindowEndDate = new DateOnly(2026, 8, 14),
        ReturnedCount = 7,
        MatchedCount = 13,
        WithheldCount = 6,
        WithheldReason = CoachScopeWithheldReason.BelowMinimumEvidence,
        Truncated = false,
        DefinitionCode = CoachScopeDefinition.PracticeWindowBalance
    };

    private static CoachToolCallObservation Observation(
        CoachToolCallOutcome outcome = CoachToolCallOutcome.Succeeded,
        CoachResultScope? scope = null,
        CoachToolFailureKind? failure = null,
        int ordinal = 1) =>
        new(CoachToolNames.GetPracticeBalance,
            ordinal,
            outcome,
            failure,
            CoachToolArgumentMask.Window,
            42,
            scope,
            CoachToolSubjectCode.ForPreferenceSetting("session_minutes"));

    [Fact]
    public void A_successful_call_projects_its_scope_codes_and_counts()
    {
        var entry = CoachTurnTraceProjection.Project(Observation(scope: Scope()));

        entry.Ordinal.Should().Be(1);
        entry.ToolName.Should().Be(CoachToolNames.GetPracticeBalance);
        entry.Outcome.Should().Be(CoachToolCallOutcome.Succeeded);
        entry.ArgumentMask.Should().Be(CoachToolArgumentMask.Window);
        entry.ElapsedMs.Should().Be(42);

        entry.Coverage.Should().Be(CoachScopeCoverage.WindowBounded);
        entry.DefinitionCode.Should().Be(CoachScopeDefinition.PracticeWindowBalance);
        entry.WithheldReason.Should().Be(CoachScopeWithheldReason.BelowMinimumEvidence);
        entry.MatchedCount.Should().Be(13);
        entry.ReturnedCount.Should().Be(7);
        entry.WithheldCount.Should().Be(6);
        entry.Truncated.Should().BeFalse();
    }

    /// <summary>
    /// A call that stated no scope projects the explicit "not stated" codes.
    /// </summary>
    /// <remarks>
    /// <c>Unspecified</c> is a real answer here rather than a gap: it says the call never got far
    /// enough to describe what it looked at. Defaulting to a plausible coverage instead would make
    /// a refused call read like a successful one that found nothing.
    /// </remarks>
    [Theory]
    [InlineData(CoachToolCallOutcome.Refused)]
    [InlineData(CoachToolCallOutcome.Faulted)]
    public void A_call_without_a_scope_projects_the_unspecified_codes(CoachToolCallOutcome outcome)
    {
        var entry = CoachTurnTraceProjection.Project(
            Observation(outcome, scope: null, failure: CoachToolFailureKind.ProfileMissing));

        entry.Coverage.Should().Be(CoachScopeCoverage.Unspecified);
        entry.DefinitionCode.Should().Be(CoachScopeDefinition.Unspecified);
        entry.WithheldReason.Should().Be(CoachScopeWithheldReason.None);
        entry.MatchedCount.Should().BeNull();
        entry.ReturnedCount.Should().BeNull();
        entry.WithheldCount.Should().BeNull();
        entry.Outcome.Should().Be(outcome);
    }

    /// <summary>The subject code is not carried into the trace.</summary>
    /// <remarks>
    /// It is closed and bounded, and it is already recorded where it belongs — the opportunity
    /// ledger. A second copy here would be the same fact in two places with two retention rules.
    /// </remarks>
    [Fact]
    public void The_subject_code_is_recorded_in_the_ledger_and_not_in_the_trace()
    {
        var observation = Observation(scope: Scope());
        observation.SubjectCode.Should().NotBeNull("the observation carries it");

        var json = JsonSerializer.Serialize(CoachTurnTraceProjection.Project(observation));

        json.Should().NotContain("session_minutes");
        json.Should().NotContain("SubjectCode");
    }

    /// <summary>
    /// An unobserved turn projects nothing; an observed turn that called nothing projects an empty
    /// trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This assertion was previously the other way round for the empty buffer, and that was the
    /// defect.</b> It read <c>Project(new CoachTurnObservationBuffer())</c> as null, on the
    /// reasoning that "a turn that used no tools has nothing to explain". It has a great deal to
    /// explain: it is the one shape that proves positively that nothing was read, and it is the
    /// population <c>CoachFabricatedCheckRule</c> and
    /// <c>CoachUnverifiedLearnerStateClaimRule</c> exist to catch. Both bail on a null trace
    /// because "no trace is no evidence of absence" — correct reasoning that the projection then
    /// defeated by handing them the same null it hands a turn nobody watched.
    /// </para>
    /// <para>
    /// The change is strictly stronger, not weaker: the null case is unchanged and still asserted
    /// here, and the empty case moved from "says nothing" to "says nothing was read". Nothing is
    /// fabricated to say it — no call, and whatever budget the buffer actually holds.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unobserved_turn_projects_no_trace_and_an_idle_observed_turn_projects_an_empty_one()
    {
        CoachTurnTraceProjection.Project((ICoachTurnObservationBuffer?)null).Should().BeNull(
            "no buffer is no observation, and unknown must never read as proven");

        var idle = CoachTurnTraceProjection.Project(new CoachTurnObservationBuffer());

        idle.Should().NotBeNull(
            "a buffer that was present and recorded zero calls is a recorded turn stating that "
            + "nothing was read");
        idle!.Calls.Should().BeEmpty("and it says so without inventing a call to say it with");
        idle.BudgetUsed.Should().BeNull("no budget was recorded, and none is fabricated");
        idle.BudgetLimit.Should().BeNull();
    }

    /// <summary>The budget rides the summary once, not as an entry.</summary>
    /// <remarks>
    /// The structural form of "record it at the turn boundary, not as a fake tool observation". A
    /// synthetic entry would report a limit as a tool that failed, and would make the call count
    /// disagree with the number of calls that happened.
    /// </remarks>
    [Fact]
    public void The_budget_is_recorded_once_on_the_summary_and_never_as_a_call()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(Observation(scope: Scope()));
        buffer.Add(Observation(scope: Scope(), ordinal: 2));
        buffer.RecordBudget(used: 3, limit: 20);

        var summary = CoachTurnTraceProjection.Project(buffer);

        summary.Should().NotBeNull();
        summary!.BudgetUsed.Should().Be(3);
        summary.BudgetLimit.Should().Be(20);

        summary.Calls.Should().HaveCount(
            2, "the third budget slot was a refusal the seam never saw, so it is not a call");

        summary.Calls.Should().NotContain(
            c => c.FailureKind == CoachToolFailureKind.BudgetExhausted);
    }

    /// <summary>An unrecorded budget is null rather than guessed from the call count.</summary>
    [Fact]
    public void An_unrecorded_budget_is_null()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(Observation(scope: Scope()));

        var summary = CoachTurnTraceProjection.Project(buffer);

        summary!.BudgetUsed.Should().BeNull(
            "the call count is not the budget; a refusal is counted there and not here");
        summary.BudgetLimit.Should().BeNull();
    }

    /// <summary>
    /// Every observation outcome projects, and the sweep proves its own census.
    /// </summary>
    /// <remarks>
    /// Derived from the enum, so an outcome added later is swept without anybody remembering — and
    /// a projection that threw on it fails here rather than at the turn boundary of a live turn.
    /// </remarks>
    [Fact]
    public void Every_declared_outcome_projects()
    {
        var outcomes = Enum.GetValues<CoachToolCallOutcome>();
        outcomes.Should().HaveCountGreaterThan(1);

        var projected = 0;
        foreach (var outcome in outcomes)
        {
            var entry = CoachTurnTraceProjection.Project(
                Observation(outcome, scope: outcome == CoachToolCallOutcome.Succeeded ? Scope() : null));

            entry.Outcome.Should().Be(outcome);
            projected++;
        }

        projected.Should().Be(outcomes.Length);
    }
}
