using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Tests.Coach.Capabilities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// Tier 6, Host parity. Plan §14: <em>"Every capability acceptance case passes in both Sam
/// hosts."</em>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Tier 6 previously had no carrier, and
/// <c>dotnet test --filter "Tier=6"</c> printed "No test matches" and <b>exited zero</b>. The
/// census now requires every plan tier to carry a real test. This closes tier 6 with the assertion
/// the tier names, rather than by hanging the trait on an existing suite that would turn the filter
/// green without testing parity.
/// </para>
/// <para>
/// <b>What "both hosts" can mean at this layer, and what it cannot.</b> Sam runs in two hosts — the
/// native (MAUI/Blazor Hybrid) host and the web (server-side Blazor) host. Neither host identity
/// reaches the capability layer: a host influences resolution through exactly one channel, the
/// <see cref="CoachClientCapabilityHandshake"/>, which carries a <c>Version</c> and a set of
/// <c>Codes</c> and nothing else. Host parity is therefore precisely the statement that
/// <b>resolution is a function of the advertised codes alone</b>, and in particular does not vary
/// with the handshake version — the one field that genuinely differs between two hosts, because
/// the native head and the web head ship on independent cadences and are routinely on different
/// handshake versions at the same moment.
/// </para>
/// <para>
/// That framing is what makes this test non-trivial. It is not asserting that a pure function is
/// pure; it is asserting that the version skew which <em>will</em> exist in production cannot make
/// one host more capable than the other for the same advertised code set. A resolver that quietly
/// gated a capability on a minimum handshake version would ship a capability to one host and
/// withhold it from the other, and every §14.1 capability case would still pass on whichever host
/// the author happened to test.
/// </para>
/// <para>
/// <b>Coverage.</b> The four §14.1 capability acceptance cases — AC-F1, AC-F2, AC-F3, AC-F5 — are
/// each run against both host-shaped handshakes. Their single-host truth is owned by
/// <see cref="CoachFoundationGateAcceptanceTests"/>; this file asserts only that the outcome does
/// not move between hosts, and does not restate their bars.
/// </para>
/// <para>
/// The wider §14 "New guard" <c>SamHostParityTests</c> — end-to-end parity across the two rendered
/// hosts — is a later workstream's UI-level deliverable. This file is the API-layer half and does
/// not stand in for it.
/// </para>
/// <para>Fixture rule (§14): synthetic handshakes and synthetic registrations throughout.</para>
/// </remarks>
public sealed class CoachFoundationGateHostParityTests
{
    private const string ThemeCapability = CoachCapabilityDeclarations.ThemeMetadataCapabilityName;

    /// <summary>
    /// The handshake the native head sends: the current minimum it was built against.
    /// </summary>
    private const int NativeHostHandshakeVersion = CoachClientCapabilityHandshake.MinimumSupportedVersion;

    /// <summary>
    /// The handshake a web head that shipped later sends. Deliberately far ahead of the native
    /// head, because a one-apart skew could pass by accident against an off-by-one comparison.
    /// </summary>
    private const int WebHostHandshakeVersion = CoachClientCapabilityHandshake.MinimumSupportedVersion + 7;

    public static TheoryData<string, int> Hosts => new()
    {
        { "native", NativeHostHandshakeVersion },
        { "web", WebHostHandshakeVersion }
    };

    // ─────────────────────────────────────────────────────────────────────────
    // The four §14.1 capability cases, each in both hosts.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-F1 in both hosts. A declared capability at stage, advertised, resolves <c>Present</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    [Trait(CoachGateTier.Key, CoachGateTier.HostParity)]
    [Trait(CoachGateCase.Key, CoachGateCase.F1)]
    public void AC_F1_resolves_present_in_both_hosts(string host, int handshakeVersion)
    {
        var resolver = ResolverWithTheme();

        var availability = resolver.Resolve(
            ThemeCapability,
            CoachCapabilityStage.Presentation,
            CapabilityFixtures.Handshake(handshakeVersion, CoachClientCapabilityCode.ThemeMetadata));

        availability.Should().Be(
            CoachCapabilityAvailability.Present,
            "AC-F1 must hold in the {0} host. The advertised code set is identical to the other "
            + "host's; only the handshake version differs, and a version is not a capability",
            host);
    }

    /// <summary>
    /// AC-F2 in both hosts. Nothing advertised, so the capability is not present here.
    /// </summary>
    /// <remarks>
    /// The §14.1 bar continues into repair — <c>CapabilityAbsent</c> repairs to
    /// <c>PresentOnAnotherSurface</c> and the answer names <c>/settings</c> — and that is asserted
    /// once, in the acceptance suite. Restating it here would be a second copy of a matrix row.
    /// What tier 6 adds is that the resolution the repair is built on does not differ by host.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    [Trait(CoachGateTier.Key, CoachGateTier.HostParity)]
    [Trait(CoachGateCase.Key, CoachGateCase.F2)]
    public void AC_F2_an_unadvertised_capability_is_absent_in_both_hosts(string host, int handshakeVersion)
    {
        var resolver = ResolverWithTheme();

        var availability = resolver.Resolve(
            ThemeCapability,
            CoachCapabilityStage.Presentation,
            CapabilityFixtures.Handshake(handshakeVersion));

        availability.Should().NotBe(
            CoachCapabilityAvailability.Present,
            "AC-F2 must hold in the {0} host. A host that resolves Present without advertising the "
            + "code would let Sam claim a capability the learner's surface cannot execute",
            host);
    }

    /// <summary>
    /// AC-F3 in both hosts. The input to the false-limitation rule is the same on both.
    /// </summary>
    /// <remarks>
    /// AC-F3's bar is that <c>FalseLimitation</c> fires when the answer claims inability while the
    /// manifest resolves <c>Present</c>. The rule's behaviour is asserted in the acceptance suite.
    /// The host-parity question is upstream of it: both hosts must agree the capability
    /// <em>is</em> present, or the rule fires on one host and not the other for the same answer.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    [Trait(CoachGateTier.Key, CoachGateTier.HostParity)]
    [Trait(CoachGateCase.Key, CoachGateCase.F3)]
    public void AC_F3_the_false_limitation_precondition_holds_in_both_hosts(string host, int handshakeVersion)
    {
        var resolver = ResolverWithTheme();

        var availability = resolver.Resolve(
            ThemeCapability,
            CoachCapabilityStage.Presentation,
            CapabilityFixtures.Handshake(handshakeVersion, CoachClientCapabilityCode.ThemeMetadata));

        availability.Should().Be(
            CoachCapabilityAvailability.Present,
            "AC-F3 asks whether a stated inability contradicts the manifest. If the {0} host "
            + "resolved anything other than Present, the same answer would be honest on one host "
            + "and a false limitation on the other",
            host);
    }

    /// <summary>
    /// AC-F5 in both hosts. An unknown code is ignored; the turn still renders.
    /// </summary>
    /// <remarks>
    /// This is the case most likely to break under version skew, and the reason the two versions
    /// here are seven apart rather than adjacent: an unknown code is exactly what an older host
    /// receives from a newer contract, and exactly what a newer host sends to an older server.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    [Trait(CoachGateTier.Key, CoachGateTier.HostParity)]
    [Trait(CoachGateCase.Key, CoachGateCase.F5)]
    public void AC_F5_an_unknown_code_is_ignored_in_both_hosts(string host, int handshakeVersion)
    {
        var resolver = ResolverWithTheme();
        var unknown = (CoachClientCapabilityCode)9_999;

        var resolve = () => resolver.Resolve(
            ThemeCapability,
            CoachCapabilityStage.Presentation,
            CapabilityFixtures.Handshake(
                handshakeVersion,
                unknown,
                CoachClientCapabilityCode.ThemeMetadata));

        resolve.Should().NotThrow(
            "AC-F5 requires an unknown code to be ignored rather than fatal. A throw here is a "
            + "turn that never renders on the {0} host",
            host);

        resolve().Should().Be(
            CoachCapabilityAvailability.Present,
            "the unknown code must be ignored, not treated as poisoning the whole handshake. The "
            + "known code beside it still advertises the capability on the {0} host",
            host);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The parity property itself, stated directly.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier 6. Across every stage, the two hosts agree for the same advertised code set.
    /// </summary>
    /// <remarks>
    /// The four case tests above check parity at the stage each case names. This sweeps every
    /// stage, so a version gate introduced at a stage no §14.1 case happens to exercise is still
    /// caught. Asserted as a single comparison per stage rather than per host, so the failure
    /// message names the disagreement rather than one side of it.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.HostParity)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void Both_hosts_resolve_identically_at_every_stage_for_the_same_codes()
    {
        var resolver = ResolverWithTheme();

        var disagreements = Enum.GetValues<CoachCapabilityStage>()
            .Select(stage =>
            {
                var native = resolver.Resolve(
                    ThemeCapability,
                    stage,
                    CapabilityFixtures.Handshake(
                        NativeHostHandshakeVersion,
                        CoachClientCapabilityCode.ThemeMetadata));

                var web = resolver.Resolve(
                    ThemeCapability,
                    stage,
                    CapabilityFixtures.Handshake(
                        WebHostHandshakeVersion,
                        CoachClientCapabilityCode.ThemeMetadata));

                return (Stage: stage, Native: native, Web: web);
            })
            .Where(row => row.Native != row.Web)
            .Select(row => $"{row.Stage}: native={row.Native}, web={row.Web}")
            .ToList();

        disagreements.Should().BeEmpty(
            "plan §14 tier 6 requires every capability acceptance case to pass in both Sam hosts. "
            + "A host reaches this layer only through the handshake, so a disagreement here means "
            + "resolution is gated on the handshake version — which the two heads do not share, "
            + "because they ship independently. Disagreements: {0}",
            string.Join("; ", disagreements));
    }

    /// <summary>
    /// Tier 6, non-vacuity. The sweep above compares a resolver that can actually say more than
    /// one thing.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="Both_hosts_resolve_identically_at_every_stage_for_the_same_codes"/>
    /// would pass just as happily against a resolver that returned one constant for every stage —
    /// perfect parity, zero information. This pins that the fixture's resolver distinguishes
    /// stages, so the parity sweep is comparing a function with range rather than a constant.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.HostParity)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void The_parity_sweep_runs_against_a_resolver_that_distinguishes_stages()
    {
        var resolver = ResolverWithTheme();
        var handshake = CapabilityFixtures.Handshake(
            NativeHostHandshakeVersion,
            CoachClientCapabilityCode.ThemeMetadata);

        var distinct = Enum.GetValues<CoachCapabilityStage>()
            .Select(stage => resolver.Resolve(ThemeCapability, stage, handshake))
            .Distinct()
            .ToList();

        distinct.Should().HaveCountGreaterThan(
            1,
            "a parity sweep over a resolver that answers the same thing at every stage proves "
            + "nothing. The theme capability is declared at Presentation and must not resolve "
            + "Present below it, so the sweep has at least two outcomes to disagree about");
    }

    /// <summary>A manifest carrying the theme capability with its ceiling lifted, per AC-F1.</summary>
    private static CoachCapabilityResolver ResolverWithTheme() =>
        new(CapabilityFixtures.ManifestWith(CapabilityFixtures.LegalPresentationState(ThemeCapability)));
}
