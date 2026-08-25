using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The budget pair W4 deliberately does not fill, and the ways it must not be read.
/// </summary>
/// <remarks>
/// <para>
/// <b>W4 stores null on purpose.</b> The shape and the read path are complete; the values live
/// inside the agent arms, which this batch does not own and must not edit. So every trace W4 writes
/// carries <c>BudgetUsed = null, BudgetLimit = null</c>, and the binding is W6's.
/// </para>
/// <para>
/// <b>Why that needs tests rather than a comment.</b> A nullable pair nobody fills is exactly the
/// shape somebody later reads as a number — <c>BudgetUsed ?? 0</c> in a dashboard, or
/// "<c>used &lt; limit</c> so the turn had headroom" in a heuristic. Both read "we did not record
/// this" as "we recorded that nothing happened", and the second one would report every W4 turn as
/// having run comfortably inside a budget that was never measured. The tests below pin the null,
/// pin the absence of any inference, and pin the fact that nothing in production calls the recorder
/// yet — so W6 wiring it up is a visible change rather than a quiet one.
/// </para>
/// </remarks>
public sealed class CoachTurnTraceBudgetTests
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    /// <summary>A non-empty W4 trace carries the null pair.</summary>
    /// <remarks>
    /// Non-empty on purpose. An empty buffer projects no trace at all, so asserting the null pair on
    /// one would prove nothing about the shape W4 actually writes.
    /// </remarks>
    [Fact]
    public void A_non_empty_trace_carries_a_null_budget_pair()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(Observation(1));
        buffer.Add(Observation(2));

        var trace = CoachTurnTraceProjection.Project(buffer);

        trace.Should().NotBeNull();
        trace!.Calls.Should().HaveCount(2, "the trace is non-empty, so the null pair is not vacuous");
        trace.BudgetUsed.Should().BeNull(
            "nothing recorded a budget on this buffer. Post-W6 the arms do record one, but the pair "
            + "must still read null rather than zero when it was never supplied \u2014 a turn whose "
            + "budget is unknown is not a turn that spent nothing");
        trace.BudgetLimit.Should().BeNull();
    }

    /// <summary>
    /// A recorded budget reaches the projection. The other half of the pair above.
    /// </summary>
    /// <remarks>
    /// Added by W6 alongside the Amendment A1 wiring. Without it the null-pair test could pass on a
    /// projection that dropped the budget entirely, and the distinction the honesty rules need —
    /// ran out of budget, versus stopped voluntarily — would be silently unavailable.
    /// </remarks>
    [Fact]
    public void A_recorded_budget_reaches_the_projection()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(Observation(1));
        buffer.RecordBudget(used: 4, limit: 6);

        var trace = CoachTurnTraceProjection.Project(buffer);

        trace!.BudgetUsed.Should().Be(4);
        trace.BudgetLimit.Should().Be(6);
        trace.Calls.Should().HaveCount(
            1,
            "recording a budget is not a tool call; a synthetic entry here would inflate every "
            + "count the trace reports");
    }

    /// <summary>The null pair survives serialization and the section-scoped read.</summary>
    /// <remarks>
    /// The round trip is where a null would most plausibly become a zero: a serializer default, a
    /// non-nullable member on a later revision, or a reader that coalesced. Reading it back through
    /// the real outcome reader is what proves none of those happened.
    /// </remarks>
    [Fact]
    public void The_null_budget_pair_round_trips_as_null_and_never_as_zero()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(Observation(1));

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(null, CoachTurnTraceProjection.Project(buffer)), OutcomeJson);

        var trace = CoachConversationService.ReadOutcome(payload, 2)!.Trace;

        trace.Should().NotBeNull();
        trace!.BudgetUsed.Should().BeNull();
        trace.BudgetLimit.Should().BeNull();
        trace.BudgetUsed.Should().NotBe(0, "unrecorded is not zero-used");
        trace.BudgetLimit.Should().NotBe(0, "unrecorded is not a zero cap");
    }

    /// <summary>
    /// The call count is not quietly substituted for the budget.
    /// </summary>
    /// <remarks>
    /// The nearest available number, and the wrong one: a budget refusal is raised by the outer
    /// wrapper before the observation seam runs, so it is counted against the budget and never
    /// appears as a call. The two legitimately differ on exactly the turns worth looking at.
    /// </remarks>
    [Fact]
    public void The_call_count_is_not_inferred_as_the_budget_used()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(Observation(1));
        buffer.Add(Observation(2));
        buffer.Add(Observation(3));

        var trace = CoachTurnTraceProjection.Project(buffer)!;

        trace.Calls.Should().HaveCount(3);
        trace.BudgetUsed.Should().BeNull("three calls is not a budget reading");
    }

    /// <summary>
    /// Nothing on the summary turns the null pair into an exhausted-or-not verdict.
    /// </summary>
    /// <remarks>
    /// Structural rather than behavioural: the summary exposes the two nullable numbers and nothing
    /// derived from them, so there is no member a caller can read that would answer "was the budget
    /// exhausted" from values that were never recorded. A boolean added here would be the whole
    /// defect in one property.
    /// </remarks>
    [Fact]
    public void The_summary_exposes_no_derived_exhausted_verdict()
    {
        var members = typeof(CoachTurnTraceSummary)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToList();

        members.Select(m => m.Name).Should().BeEquivalentTo(
            [
                nameof(CoachTurnTraceSummary.Calls),
                nameof(CoachTurnTraceSummary.BudgetUsed),
                nameof(CoachTurnTraceSummary.BudgetLimit)
            ],
            "a derived member would answer a question the recorded values cannot support");

        typeof(CoachTurnTraceSummary).GetProperty(nameof(CoachTurnTraceSummary.BudgetUsed))!
            .PropertyType.Should().Be(typeof(int?), "nullable is the whole point; int would read as zero");
        typeof(CoachTurnTraceSummary).GetProperty(nameof(CoachTurnTraceSummary.BudgetLimit))!
            .PropertyType.Should().Be(typeof(int?));
    }

    /// <summary>
    /// <c>RecordBudget</c> is called from the two agent arms and nowhere else. Amendment A1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This assertion was inverted by W6, deliberately and once.</b> W4 shipped it as "zero
    /// production callers", with the note that a caller appearing means "the amendment needs
    /// revisiting rather than the tests being edited". W6 is that revisit: the honesty rules need
    /// to distinguish a turn that stopped because it ran out of budget from a turn that decided it
    /// had enough, and the budget object only exists in the agent arms.
    /// </para>
    /// <para>
    /// So the guard is narrowed rather than deleted. The list below is exact: exactly two callers,
    /// both at a turn boundary, both in files whose whole job is to run one turn. A third caller —
    /// a tool, a service, a projection — would mean something other than the turn boundary is
    /// claiming to know the budget, and that is the case the original guard was really protecting
    /// against.
    /// </para>
    /// <para>
    /// The scan fails loudly when it cannot find the tree, so it cannot pass by scanning nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void RecordBudget_is_called_only_from_the_two_agent_arms()
    {
        var source = SourceRoot();

        var declarations = new[]
        {
            Path.Combine("Coach", "Tools", "Observation", "ICoachTurnObservationBuffer.cs")
        };

        var callers = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || declarations.Any(d => file.EndsWith(d, StringComparison.Ordinal)))
            {
                continue;
            }

            if (ContainsCall(File.ReadAllText(file)))
            {
                callers.Add(Path.GetRelativePath(source, file));
            }
        }

        callers.Should().BeEquivalentTo(
            [
                Path.Combine("SentenceStudio.Api", "Coach", "Agents", "BaselineLearningCoach.cs"),
                Path.Combine("SentenceStudio.Api", "Coach", "Agents", "HarnessLearningCoach.cs")
            ],
            "Amendment A1 authorises exactly these two call sites, one per arm, at the turn "
            + "boundary. Any other caller is something that does not own the budget claiming to "
            + "know it");
    }

    /// <summary>
    /// True when <paramref name="source"/> invokes the recorder outside a comment.
    /// </summary>
    /// <remarks>
    /// Comments are stripped first. The summary's own remarks name
    /// <c>ICoachTurnObservationBuffer.RecordBudget</c> in prose to explain why the pair is null,
    /// and a scan that counted that as a caller would fail on the documentation of the very rule it
    /// exists to enforce. An invocation needs the parenthesis; a cross-reference does not have one.
    /// </remarks>
    private static bool ContainsCall(string source)
    {
        var code = string.Join(
            '\n',
            source
                .Split('\n')
                .Select(line =>
                {
                    var comment = line.IndexOf("//", StringComparison.Ordinal);
                    return comment >= 0 ? line[..comment] : line;
                }));

        return code.Contains("RecordBudget(", StringComparison.Ordinal);
    }

    /// <summary>The scan target exists, so the scan above cannot pass over an empty tree.</summary>
    [Fact]
    public void The_production_source_tree_the_scan_reads_is_real()
    {
        var source = SourceRoot();

        Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Take(50)
            .Should().HaveCount(50, "the scan must be reading the shipped tree");

        File.Exists(Path.Combine(
                source, "SentenceStudio.Api", "Coach", "Tools", "Observation",
                "ICoachTurnObservationBuffer.cs"))
            .Should().BeTrue("the declaration the scan excludes must actually be where it thinks");
    }

    /// <summary>
    /// The repository's <c>src/</c> directory, found by walking up from the test binary.
    /// </summary>
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(candidate)
                && Directory.Exists(Path.Combine(candidate, "SentenceStudio.Api")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The production source tree was not found above " + AppContext.BaseDirectory
            + ". This scan must fail loudly rather than pass over nothing.");
    }

    private static CoachToolCallObservation Observation(int ordinal) =>
        new(CoachToolNames.GetPracticeBalance,
            ordinal,
            CoachToolCallOutcome.Succeeded,
            null,
            CoachToolArgumentMask.Window,
            5,
            null);
}
