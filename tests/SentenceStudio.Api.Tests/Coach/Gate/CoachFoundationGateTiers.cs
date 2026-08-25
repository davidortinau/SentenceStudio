using System.Reflection;
using FluentAssertions;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// The tier vocabulary from plan §14, as trait values a CI filter can select.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why plain <c>[Trait]</c> and not a custom discoverer.</b> xUnit lets a suite declare its own
/// <c>ITraitAttribute</c> plus a <c>TraitDiscoverer</c>, which reads better at the call site. It
/// also fails <em>silently</em>: a discoverer the runner cannot load contributes no traits, and
/// <c>--filter Tier=2</c> then selects zero tests and reports success. A gate whose filter can
/// return an empty set and call it green is the exact vacuity this workstream exists to prevent, so
/// the built-in attribute wins on the one property that matters here.
/// </para>
/// <para>
/// <see cref="CoachFoundationGateTierCensusTests"/> closes the remaining hole — that a filter
/// matches nothing because nobody carried the trait — by asserting every tier and every acceptance
/// id has at least one carrier in this assembly.
/// </para>
/// </remarks>
internal static class CoachGateTier
{
    internal const string Key = "Tier";

    /// <summary>Tier 1 — semantics and scope. Scope values are correct; no tool reports an order it did not apply.</summary>
    internal const string SemanticsAndScope = "1";

    /// <summary>Tier 2 — claim rules and repair. Each failure code has a passing and a failing fixture.</summary>
    internal const string ClaimRulesAndRepair = "2";

    /// <summary>Tier 3 — agreement. Sam and the owning surface agree on the same question.</summary>
    internal const string Agreement = "3";

    /// <summary>Tier 4 — replay. Recorded results plus traces compose a deterministic answer with no model call.</summary>
    internal const string Replay = "4";

    /// <summary>Tier 5 — capability. Legal matrix, derived availability, handshake merge, limitation rules.</summary>
    internal const string Capability = "5";

    /// <summary>Tier 6 — host parity. Every capability acceptance case passes in both Sam hosts.</summary>
    internal const string HostParity = "6";

    internal static readonly IReadOnlyList<string> All =
        [SemanticsAndScope, ClaimRulesAndRepair, Agreement, Replay, Capability, HostParity];
}

/// <summary>
/// The plan §14.1 acceptance identifiers, and the §14.2 case identifiers, as trait values.
/// </summary>
/// <remarks>
/// These are labels on tests, not a second copy of the matrix. Plan §14 holds the single acceptance
/// matrix; a test tagged <c>AC-F2</c> asserts the bar that §14.1 states for AC-F2 and cites it. If
/// the two ever disagree the plan is right and the test is wrong.
/// </remarks>
internal static class CoachGateCase
{
    internal const string Key = "Acceptance";

    internal const string F1 = "AC-F1";
    internal const string F2 = "AC-F2";
    internal const string F3 = "AC-F3";
    internal const string F4 = "AC-F4";
    internal const string F5 = "AC-F5";
    internal const string F6 = "AC-F6";
    internal const string F7 = "AC-F7";
    internal const string F8 = "AC-F8";

    internal static readonly IReadOnlyList<string> Foundation = [F1, F2, F3, F4, F5, F6, F7, F8];

    /// <summary>The §14.2 case column. One named foundation bar each.</summary>
    internal const string CaseA = "Case-A";

    internal const string CaseB = "Case-B";
    internal const string CaseC = "Case-C";
    internal const string CaseD = "Case-D";

    internal static readonly IReadOnlyList<string> Cases = [CaseA, CaseB, CaseC, CaseD];
}

/// <summary>
/// Which of the twelve §8.2 invariants a test speaks to, and how that invariant is evidenced.
/// </summary>
/// <remarks>
/// Ceremony finding F3. The twelve do not all read zero the same way, and an artifact that presents
/// them as if they did is a false artifact. See <c>CoachStructuralZeroInvariantTests</c> for the
/// census that keeps the three buckets from silently merging.
/// </remarks>
internal static class CoachGateEvidence
{
    internal const string Key = "Evidence";

    /// <summary>A real counter with a real denominator. Reads zero over a positive denominator or it proves nothing.</summary>
    internal const string SoakMeasured = "soak-measured";

    /// <summary>A build-time structural check. Dated by the build that ran it, not by a soak window.</summary>
    internal const string BuildTime = "build-time";

    /// <summary>No code path can produce the event in this build. Evidenced by absence over the current registry.</summary>
    internal const string StructurallyAbsent = "structurally-absent";

    /// <summary>Registered but unreachable in this build. Not a measured zero. Re-arms at C1.</summary>
    internal const string InactiveUntilC1 = "inactive-until-c1";

    internal static readonly IReadOnlyList<string> All =
        [SoakMeasured, BuildTime, StructurallyAbsent, InactiveUntilC1];
}

/// <summary>
/// The gate's own vacuity guard: a filter that matches nothing must not be able to report success.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this defends.</b> Every other test in this folder assumes CI can select it. A typo in a
/// trait value, a tier nobody tagged, or an acceptance id dropped in a refactor all produce the same
/// observable outcome — <c>dotnet test --filter Tier=4</c> runs zero tests and exits zero — and a
/// reviewer reading the transcript sees a green line either way.
/// </para>
/// <para>
/// So the census runs inside the suite, over reflection on this assembly, where an empty set is an
/// assertion failure rather than a passing command.
/// </para>
/// </remarks>
[Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
public sealed class CoachFoundationGateTierCensusTests
{
    private static IReadOnlyList<(MethodInfo Method, IReadOnlyList<(string Name, string Value)> Traits)> TestMethods()
    {
        var methods = new List<(MethodInfo, IReadOnlyList<(string, string)>)>();

        foreach (var type in typeof(CoachFoundationGateTierCensusTests).Assembly.GetTypes())
        {
            var classTraits = TraitsOn(type);

            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var isTest = method.GetCustomAttributes()
                    .Any(attribute => attribute is FactAttribute or TheoryAttribute);

                if (!isTest)
                {
                    continue;
                }

                methods.Add((method, [.. classTraits, .. TraitsOn(method)]));
            }
        }

        return methods;
    }

    /// <summary>
    /// Reads trait pairs from a member's attribute <em>data</em> rather than its instances.
    /// </summary>
    /// <remarks>
    /// xUnit 2's <c>TraitAttribute</c> stores its pair for the discoverer and exposes no properties,
    /// so the constructor arguments are the only readable source. Reading the metadata also means
    /// this census sees exactly what the runner's filter sees.
    /// </remarks>
    private static IReadOnlyList<(string Name, string Value)> TraitsOn(MemberInfo member) =>
    [
        .. member.GetCustomAttributesData()
            .Where(data => data.AttributeType == typeof(TraitAttribute))
            .Where(data => data.ConstructorArguments.Count == 2)
            .Select(data => (
                Name: (string)(data.ConstructorArguments[0].Value ?? string.Empty),
                Value: (string)(data.ConstructorArguments[1].Value ?? string.Empty)))
    ];

    private static HashSet<string> ValuesFor(string key) =>
        TestMethods()
            .SelectMany(entry => entry.Traits)
            .Where(trait => trait.Name == key)
            .Select(trait => trait.Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Carrier count per trait value, counted in <em>test methods</em>.
    /// </summary>
    /// <remarks>
    /// Methods, not runtime cases. A <c>[Theory]</c> with four rows is one carrier here and four
    /// lines in a <c>dotnet test</c> summary, so these numbers are deliberately smaller than what
    /// the CLI prints for the same filter. Counting methods is the right unit for a census: it
    /// measures how many distinct things were written to cover a tier, which is what silently
    /// regresses, rather than how many rows a data source happens to yield.
    /// </remarks>
    private static Dictionary<string, int> CountsBy(string key) =>
        TestMethods()
            .SelectMany(entry => entry.Traits
                .Where(trait => trait.Name == key)
                .Select(trait => trait.Value)
                .Distinct(StringComparer.Ordinal))
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>
    /// Every tier plan §14 defines carries at least one real test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plan §14 defines six evaluation tiers and puts all six "In CI". A tier with no carrier is not
    /// a tier that passes — <c>dotnet test --filter "Tier=4"</c> prints "No test matches" and
    /// <b>exits zero</b>, so in a CI log that scrolls past, an empty tier and a green tier are the
    /// same three characters. This test is the only thing standing between those two states.
    /// </para>
    /// <para>
    /// <b>History, so the next author does not re-litigate it.</b> Tiers 4 and 6 were empty for one
    /// build, on the reasoning that their §14 "New guards" —
    /// <c>FeedbackPreviewTokenReplayTests</c> and <c>SamHostParityTests</c> — belong to later
    /// workstreams. That reasoning was wrong about the consequence: it left two of six gate tiers
    /// unevidenced while the gate reported six rows. The gap is now closed by
    /// <see cref="CoachFoundationGateReplayTests"/> (tier 4) and
    /// <see cref="CoachFoundationGateHostParityTests"/> (tier 6), which assert what those tiers
    /// name at the layer this project can reach. They do not stand in for the later end-to-end
    /// guards, and both say so.
    /// </para>
    /// <para>
    /// <b>What was not done.</b> The tiers were not filled by hanging a tier-4 or tier-6 trait on a
    /// loosely related existing suite. That turns the filter green while proving nothing, which is
    /// the failure this whole file exists to catch — a filter selecting zero tests reads as a pass,
    /// and a filter selecting the wrong tests reads as a better one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_tier_in_the_plan_has_at_least_one_carrier()
    {
        var tagged = ValuesFor(CoachGateTier.Key);

        foreach (var tier in CoachGateTier.All)
        {
            tagged.Should().Contain(
                tier,
                "plan §14 defines tier {0} and puts it in CI. `--filter {1}={0}` selecting nothing "
                + "exits zero and is indistinguishable from a pass, so an uncarried tier is a gate "
                + "row that reports without running",
                tier,
                CoachGateTier.Key);
        }

        tagged.Should().BeEquivalentTo(
            CoachGateTier.All,
            "the carried tiers and the planned tiers are the same set — no tier unevidenced, and "
            + "no tier invented outside §14");
    }

    /// <summary>
    /// Each tier's carrier count is pinned exactly, so a tier cannot quietly empty out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Every_tier_in_the_plan_has_at_least_one_carrier"/> is a floor and
    /// <see cref="Each_populated_tier_carries_more_than_one_test"/> raises that floor to two. Both
    /// are satisfied by a tier that loses most of its coverage, which is the realistic regression:
    /// a rename, a moved file, or a deleted <c>[Trait]</c> takes tier 3 from four tests to two and
    /// nothing notices, because two is still more than one.
    /// </para>
    /// <para>
    /// Pinning the exact number makes that visible. The failure message is a diff, and updating the
    /// number is a one-line, deliberate act by an author who has just read why it moved — which is
    /// the point. This is a change-detector on purpose; the thing being detected is coverage
    /// silently leaving the gate.
    /// </para>
    /// </remarks>
    [Fact]
    public void Each_tier_carries_the_number_of_tests_the_gate_was_reviewed_with()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CoachGateTier.SemanticsAndScope] = 35,
            [CoachGateTier.ClaimRulesAndRepair] = 40,
            [CoachGateTier.Agreement] = 4,
            [CoachGateTier.Replay] = 5,
            [CoachGateTier.Capability] = 12,
            [CoachGateTier.HostParity] = 6
        };

        CountsBy(CoachGateTier.Key).Should().BeEquivalentTo(
            expected,
            "these are the per-tier counts the W9 gate was reviewed with. A drop means coverage "
            + "left the gate without anyone deciding to remove it; a rise means coverage arrived "
            + "without anyone deciding it belonged. Either way the number is updated by hand, by "
            + "an author who has read the diff");
    }

    /// <summary>
    /// Every tier that does carry something carries more than a token.
    /// </summary>
    /// <remarks>
    /// A tier with exactly one test is a tier where <c>--filter Tier=N</c> passing tells you almost
    /// nothing. This is a floor, not a target.
    /// </remarks>
    [Fact]
    public void Each_populated_tier_carries_more_than_one_test()
    {
        var byTier = TestMethods()
            .SelectMany(entry => entry.Traits.Select(trait => (entry.Method, trait)))
            .Where(pair => pair.trait.Name == CoachGateTier.Key)
            .GroupBy(pair => pair.trait.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        byTier.Should().NotBeEmpty();

        foreach (var (tier, count) in byTier)
        {
            count.Should().BeGreaterThan(
                1,
                "tier {0} carries {1} test(s). One test behind a filter is a filter that passes for "
                + "the wrong reason",
                tier,
                count);
        }
    }

    /// <summary>
    /// Every acceptance and evidence value the vocabulary declares is carried by something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A declared filter value with no carrier is worse than no value at all. <c>--filter
    /// Acceptance=Foundation</c> selecting zero tests exits zero and prints "No test matches", and
    /// in a CI log that scrolls past it is indistinguishable from a pass. This caught exactly that:
    /// <c>Foundation</c> and <c>Cases</c> were declared and never applied.
    /// </para>
    /// <para>
    /// Tiers are deliberately exempt and handled by the census above, because tiers 4 and 6 are
    /// empty for a reason that is a fact about the build rather than an oversight.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_declared_acceptance_and_evidence_value_has_a_carrier()
    {
        var acceptance = ValuesFor(CoachGateCase.Key);
        var evidence = ValuesFor(CoachGateEvidence.Key);

        foreach (var value in CoachGateCase.Foundation.Concat(CoachGateCase.Cases))
        {
            acceptance.Should().Contain(
                value,
                "`--filter {0}={1}` must select something. A declared value nobody carries is a "
                + "filter that reads as a pass while running nothing",
                CoachGateCase.Key,
                value);
        }

        foreach (var value in CoachGateEvidence.All)
        {
            evidence.Should().Contain(
                value,
                "`--filter {0}={1}` must select something",
                CoachGateEvidence.Key,
                value);
        }
    }

    [Fact]
    public void No_test_carries_a_tier_the_plan_does_not_define()
    {
        var unknown = TestMethods()
            .SelectMany(entry => entry.Traits.Select(trait => (entry.Method, trait)))
            .Where(pair => pair.trait.Name == CoachGateTier.Key)
            .Where(pair => !CoachGateTier.All.Contains(pair.trait.Value, StringComparer.Ordinal))
            .Select(pair => $"{pair.Method.DeclaringType?.Name}.{pair.Method.Name} -> {pair.trait.Value}")
            .ToList();

        unknown.Should().BeEmpty(
            "a tier value outside plan §14 is selected by no CI filter and reviewed by nobody: {0}",
            string.Join("; ", unknown));
    }

    [Fact]
    public void Every_foundation_acceptance_case_has_at_least_one_carrier()
    {
        var tagged = ValuesFor(CoachGateCase.Key);

        foreach (var id in CoachGateCase.Foundation)
        {
            tagged.Should().Contain(
                id,
                "plan §14.1 lists {0} as a foundation acceptance case. An unrepresented case is an "
                + "unevidenced one, and the gate reads eight rows whether or not eight ran",
                id);
        }
    }

    [Fact]
    public void Every_case_bar_from_the_matrix_has_at_least_one_carrier()
    {
        var tagged = ValuesFor(CoachGateCase.Key);

        foreach (var id in CoachGateCase.Cases)
        {
            tagged.Should().Contain(id, "plan §14.2 states a foundation bar for {0}", id);
        }
    }

    /// <summary>
    /// Every AC-F case and every §14.2 case bar carries the exact number of tests reviewed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two tests above are floors: each bar must have <em>a</em> carrier. A floor is satisfied
    /// by a bar that drops from four carriers to one, which is the realistic regression — the
    /// acceptance suite is where a refactor deletes a method or a trait, and "at least one" keeps
    /// reporting green while the bar quietly thins out.
    /// </para>
    /// <para>
    /// Pinning the exact count per bar closes that. Every value in <see cref="CoachGateCase"/> is
    /// present in this table, so a bar cannot be added to the vocabulary and then left uncounted
    /// either. Counted in test methods, not runtime cases — see <see cref="CountsBy"/> for why the
    /// numbers are smaller than a <c>dotnet test</c> summary for the same filter.
    /// </para>
    /// </remarks>
    [Fact]
    public void Each_acceptance_bar_carries_the_number_of_tests_the_gate_was_reviewed_with()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CoachGateCase.F1] = 3,
            [CoachGateCase.F2] = 4,
            [CoachGateCase.F3] = 3,
            [CoachGateCase.F4] = 2,
            [CoachGateCase.F5] = 3,
            [CoachGateCase.F6] = 2,
            [CoachGateCase.F7] = 3,
            [CoachGateCase.F8] = 2,
            [CoachGateCase.CaseA] = 3,
            [CoachGateCase.CaseB] = 2,
            [CoachGateCase.CaseC] = 5,
            [CoachGateCase.CaseD] = 4
        };

        expected.Keys.Should().BeEquivalentTo(
            CoachGateCase.Foundation.Concat(CoachGateCase.Cases),
            "every declared bar is counted here, so a bar added to the vocabulary cannot slip in "
            + "without a count and a bar removed cannot leave a stale row behind");

        CountsBy(CoachGateCase.Key).Should().BeEquivalentTo(
            expected,
            "these are the per-bar counts the W9 gate was reviewed with. A bar thinning from "
            + "several carriers to one still satisfies 'at least one', so the floor tests above "
            + "cannot see that regression and this one can");
    }

    [Fact]
    public void Every_evidence_kind_has_at_least_one_carrier()
    {
        var tagged = ValuesFor(CoachGateEvidence.Key);

        foreach (var kind in new[]
                 {
                     CoachGateEvidence.SoakMeasured,
                     CoachGateEvidence.BuildTime,
                     CoachGateEvidence.StructurallyAbsent,
                     CoachGateEvidence.InactiveUntilC1
                 })
        {
            tagged.Should().Contain(
                kind,
                "ceremony finding F3 splits the twelve invariants by how they are evidenced. An "
                + "unrepresented kind ({0}) is one the artifact would report in a form nothing "
                + "produced",
                kind);
        }
    }
}
