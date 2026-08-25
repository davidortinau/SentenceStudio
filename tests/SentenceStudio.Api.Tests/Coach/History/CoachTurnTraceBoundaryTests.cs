using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The one string a stored trace may carry, and the boundary that keeps it the only one.
/// </summary>
/// <remarks>
/// <para>
/// <c>ToolName</c> is exempt from "the trace holds no free text" for exactly one reason: the value
/// provably comes from a build-time <c>CoachToolRegistration</c>. Before this batch that was a fact
/// about the call graph — the seam happens to read the registration — rather than a fact about the
/// boundary. A call graph is precisely the guarantee that stops being true without anybody editing
/// the file claiming it, and the column it writes into is protected and long-lived.
/// </para>
/// <para>
/// So membership is checked at the boundary and on the member, and both are asserted here: the
/// projection normalizes, and a directly constructed entry cannot get past the record either.
/// </para>
/// </remarks>
public sealed class CoachTurnTraceBoundaryTests
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    private static ICoachToolRegistry Registry { get; } = new CoachToolRegistry(new CoachOptions
    {
        SamOverlay = new CoachFeatureSwitch { Enabled = true },
        SamReadTools = new CoachFeatureSwitch { Enabled = true },
        SamWriteTools = new CoachFeatureSwitch { Enabled = true }
    });

    // =========================================================== full-registry sweep

    /// <summary>
    /// Every name in the frozen registry projects through unchanged.
    /// </summary>
    /// <remarks>
    /// The sweep that stops the validator becoming a censor. A membership check written against a
    /// stale list would quietly replace a perfectly real tool's name with the stand-in, and the
    /// trace would look exactly as it does when a smuggled name is caught — so the two failures have
    /// to be told apart by test rather than by inspection.
    /// </remarks>
    [Fact]
    public void Every_registered_tool_name_survives_the_projection_unchanged()
    {
        var names = Registry.All.Select(r => r.Name).ToList();

        names.Should().HaveCountGreaterThan(
            20, "the sweep must cover the real registry, not a handful of core tools");

        foreach (var name in names)
        {
            CoachTurnTraceProjection.Project(Observation(name)).ToolName.Should().Be(
                name,
                "{0} is registered; collapsing it would put a hole in the trace that looks exactly "
                + "like a smuggled name being caught",
                name);
        }
    }

    /// <summary>
    /// The validator's frozen set is the registry's, not a hand-maintained copy.
    /// </summary>
    /// <remarks>
    /// Two lists of tool names already exist — <c>CoachToolNames.AllRegistered</c> and the registry
    /// — and a third would be the one that drifts. Asserted against <c>All</c> rather than
    /// <c>Enabled</c>: feature flags decide what a learner may call, not what a name means.
    /// </remarks>
    [Fact]
    public void The_validator_is_frozen_against_the_registrys_full_set()
    {
        RegisteredNames().Should().BeEquivalentTo(
            Registry.All.Select(r => r.Name),
            "validating against Enabled would collapse a real tool's name on any deployment whose "
            + "flag happened to be off");
    }

    // ================================================================ non-members collapse

    /// <summary>
    /// A name the registry does not contain collapses, and the entry keeps its place.
    /// </summary>
    [Theory]
    [InlineData("get_practice_balance_v2")]
    [InlineData("GET_PRACTICE_BALANCE")]
    [InlineData("propose_delete_everything")]
    [InlineData("../../etc/passwd")]
    [InlineData("what is 사과 in English")]
    [InlineData("")]
    public void An_unregistered_name_collapses_to_the_server_constant(string smuggled)
    {
        var entry = CoachTurnTraceProjection.Project(Observation(smuggled, ordinal: 4));

        entry.ToolName.Should().Be(CoachToolNames.Unregistered);
        entry.Ordinal.Should().Be(4, "the entry and its ordinal are retained; only the name is replaced");
    }

    /// <summary>The raw input never reaches the serialized trace.</summary>
    /// <remarks>
    /// The end-to-end form. A membership check that normalized only a display path would still have
    /// written the raw value into the protected column, which is the thing that actually matters.
    /// </remarks>
    [Fact]
    public void The_raw_unregistered_input_is_absent_from_the_serialized_trace()
    {
        const string Smuggled = "what is 사과 in English";

        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(Observation(Smuggled));

        var json = JsonSerializer.Serialize(CoachTurnTraceProjection.Project(buffer), OutcomeJson);

        json.Should().NotContain("사과");
        json.Should().NotContain(Smuggled);
        json.Should().Contain(CoachToolNames.Unregistered, "the call is still recorded as having happened");
    }

    /// <summary>
    /// A directly constructed entry cannot bypass the boundary.
    /// </summary>
    /// <remarks>
    /// The record is public and positional, so the projection is not the only road in. Every
    /// construction path is covered here — primary constructor, <c>with</c> expression, and the
    /// deserializer — because a boundary only the intended caller honours is a convention, and a
    /// convention is not what the string exception was granted on.
    /// </remarks>
    [Fact]
    public void A_directly_constructed_entry_cannot_bypass_the_boundary()
    {
        var direct = new CoachTurnTraceEntry(
            1, "smuggled_tool", CoachToolCallOutcome.Succeeded, null,
            CoachToolArgumentMask.None, 1, CoachScopeCoverage.Unspecified,
            CoachScopeDefinition.Unspecified, CoachScopeWithheldReason.None, null, null, null, false);

        direct.ToolName.Should().Be(CoachToolNames.Unregistered, "primary constructor");

        var mutated = direct with { ToolName = "still_smuggled" };
        mutated.ToolName.Should().Be(CoachToolNames.Unregistered, "with expression");

        var deserialized = JsonSerializer.Deserialize<CoachTurnTraceEntry>(
            JsonSerializer.Serialize(direct with { ToolName = CoachToolNames.GetSkillList }, OutcomeJson)
                .Replace(CoachToolNames.GetSkillList, "smuggled_on_the_wire", StringComparison.Ordinal),
            OutcomeJson);

        deserialized!.ToolName.Should().Be(CoachToolNames.Unregistered, "deserialization");
    }

    // ========================================================== the constant itself

    /// <summary>
    /// The stand-in is not a tool, cannot be registered, and cannot read as a write.
    /// </summary>
    [Fact]
    public void The_stand_in_constant_is_not_a_tool_and_never_becomes_one()
    {
        Registry.IsRegistered(CoachToolNames.Unregistered).Should().BeFalse();
        Registry.All.Should().NotContain(r => r.Name == CoachToolNames.Unregistered);

        CoachToolNames.All.Should().NotContain(CoachToolNames.Unregistered);
        CoachToolNames.AllRegistered.Should().NotContain(CoachToolNames.Unregistered);
        CoachToolNames.AllWrite.Should().NotContain(CoachToolNames.Unregistered);

        CoachToolNames.Unregistered.Should().NotStartWith(
            CoachToolNames.ProposePrefix,
            "a collapsed name must never read as a write-intent tool");
    }

    // ============================================================ transitive only-string

    /// <summary>
    /// <c>ToolName</c> is the only string reachable from the stored trace, transitively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walked through member <em>types</em>, not just member declarations, so a member whose type
    /// holds a string cannot smuggle one in one level down. The sibling test in
    /// <c>CoachTurnTraceShapeTests</c> walks the same graph for a different reason; this one exists
    /// to state the exemption as a single named member and to prove the walk reached something.
    /// </para>
    /// <para>
    /// The census assertions at the end are what keep it from passing vacuously: a walk that
    /// visited nothing, or that never reached the entry, would otherwise report no offenders.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_registered_tool_name_is_the_only_string_transitively_reachable_from_the_trace()
    {
        var seen = new HashSet<Type>();
        var strings = new List<string>();
        var queue = new Queue<Type>([typeof(CoachStoredTurnOutcome).Assembly
            .GetType("SentenceStudio.Api.Coach.Persistence.History.CoachTurnTraceSummary", throwOnError: true)!]);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var member in type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract"))
            {
                var memberType = Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType;

                if (memberType == typeof(string) || memberType == typeof(object))
                {
                    strings.Add($"{type.Name}.{member.Name}");
                    continue;
                }

                if (memberType.IsPrimitive || memberType.IsEnum || memberType == typeof(DateTime)
                    || memberType == typeof(DateOnly) || memberType == typeof(decimal)
                    || memberType == typeof(Guid))
                {
                    continue;
                }

                if (memberType.IsGenericType)
                {
                    foreach (var argument in memberType.GetGenericArguments())
                    {
                        queue.Enqueue(argument);
                    }

                    continue;
                }

                if (memberType.IsArray && memberType.GetElementType() is { } element)
                {
                    queue.Enqueue(element);
                    continue;
                }

                queue.Enqueue(memberType);
            }
        }

        seen.Should().Contain(typeof(CoachTurnTraceEntry), "the walk must have reached the entries");
        seen.Should().HaveCountGreaterThan(1, "a walk that visited one type proves nothing");

        strings.Should().BeEquivalentTo(
            [$"{nameof(CoachTurnTraceEntry)}.{nameof(CoachTurnTraceEntry.ToolName)}"],
            "the registered tool name is the sole string exception, and it is registry-validated");
    }

    // ============================================================================ helpers

    private static IReadOnlyCollection<string> RegisteredNames()
    {
        var property = typeof(CoachTurnTraceProjection).Assembly
            .GetType("SentenceStudio.Api.Coach.Tools.Observation.CoachTurnTraceToolName", throwOnError: true)!
            .GetProperty("RegisteredNames", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (IReadOnlyCollection<string>)property.GetValue(null)!;
    }

    private static CoachToolCallObservation Observation(string toolName, int ordinal = 1) =>
        new(toolName,
            ordinal,
            CoachToolCallOutcome.Succeeded,
            null,
            CoachToolArgumentMask.None,
            3,
            null);
}
