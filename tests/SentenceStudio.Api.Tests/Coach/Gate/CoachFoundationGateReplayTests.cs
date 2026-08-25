using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// Tier 4, Replay. Plan §14: <em>"Recorded results plus traces compose a deterministic answer with
/// no model call."</em>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Tier 4 previously had no carrier in this build, and
/// <c>dotnet test --filter "Tier=4"</c> printed "No test matches" and <b>exited zero</b> — a tier
/// that runs nothing is indistinguishable in a CI log from a tier that passes. The census
/// (<see cref="CoachFoundationGateTierCensusTests"/>) now requires all six plan tiers to carry a
/// real test, so the gap is closed here with the assertions tier 4 actually names rather than by
/// re-tagging a loosely related suite. A borrowed carrier turns the filter green while proving
/// nothing, which is the failure mode the census exists to catch.
/// </para>
/// <para>
/// <b>What tier 4 means for the foundation gate.</b> The grounding layer is a replay engine. It is
/// handed a recorded answer, recorded evidence, and a recorded trace, and it composes a verdict.
/// The gate depends on that composition being a pure function of its inputs: the soak reads one
/// number per turn, and a layer that could return two different verdicts for one recorded turn
/// would make every soak zero unreproducible. Two properties carry that, and both are asserted
/// here — <b>determinism</b> over repeated and freshly constructed evaluations, and <b>no model
/// call</b>, proven structurally rather than by trusting that no chat client happened to be wired
/// in on the day the test ran.
/// </para>
/// <para>
/// <b>Scope.</b> Replay of the <em>grounding verdict</em>. The wider §14 "New guard"
/// <c>FeedbackPreviewTokenReplayTests</c> — preview-token replay — is a different subject and
/// belongs to a later workstream; this file does not stand in for it and does not claim to.
/// </para>
/// <para>Fixture rule (§14): every fixture below is a shape. No authentic text, term, identifier,
/// or conversation id appears.</para>
/// </remarks>
public sealed class CoachFoundationGateReplayTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Determinism — the same recorded turn composes the same verdict, every
    // time, including across freshly constructed evaluators.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier 4. One recorded turn replayed twenty times through one evaluator yields one verdict.
    /// </summary>
    /// <remarks>
    /// Repeat count is deliberate. A single second call would not catch an evaluator that mutates
    /// state on first use and stabilises after; twenty catches ordering and accumulation bugs that
    /// only appear once an internal buffer has been touched more than once.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Replay)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void A_recorded_turn_replays_to_the_same_verdict_every_time()
    {
        var evaluator = Evaluator();

        var verdicts = Enumerable.Range(0, 20)
            .Select(_ => Describe(Replay(evaluator)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        verdicts.Should().ContainSingle(
            "the grounding layer is a replay engine over recorded evidence. Twenty replays of one "
            + "recorded turn produced {0} distinct verdicts: {1}. A turn that can compose two "
            + "answers makes every soak zero unreproducible, because the number the window reports "
            + "would depend on when the turn happened to be evaluated rather than on what it said",
            verdicts.Count,
            string.Join(" | ", verdicts));
    }

    /// <summary>
    /// Tier 4. The verdict is a function of the recorded inputs, not of the evaluator instance.
    /// </summary>
    /// <remarks>
    /// Distinct from the test above. That one pins "no state accumulates across calls"; this one
    /// pins "no state was captured at construction". A layer that read a clock, a counter, or a
    /// static at build time would pass the first and fail this.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Replay)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void A_fresh_evaluator_replays_the_recorded_turn_to_the_same_verdict()
    {
        var first = Describe(Replay(Evaluator()));
        var second = Describe(Replay(Evaluator()));
        var third = Describe(Replay(Evaluator()));

        second.Should().Be(first, "a new evaluator over the same recorded turn is the same replay");
        third.Should().Be(first, "and remains so on the third construction");
    }

    /// <summary>
    /// Tier 4, non-vacuity. The replayed turn actually composes something.
    /// </summary>
    /// <remarks>
    /// Without this, both determinism tests above would pass over a fixture that produced no
    /// findings at all — twenty identical empty verdicts are perfectly deterministic and prove
    /// nothing about replay. This pins the fixture to a turn with real composition work in it, so
    /// the determinism claim is made over a turn that had something to get wrong.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Replay)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void The_replayed_turn_is_not_an_empty_one()
    {
        var result = Replay(Evaluator());

        result.Grounding.Should().NotBeNull("Enforce records a summary for every evaluated turn");
        result.Record.Should().NotBeNull();
        result.Record!.Findings.Should().NotBeEmpty(
            "a determinism assertion over a turn with no findings is a determinism assertion over "
            + "nothing. This fixture states a count the recorded evidence does not support, so the "
            + "replay has a verdict to compose rather than an empty list to return twenty times");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // No model call — structural, not incidental.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier 4. The replay path cannot reach a model, by construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted over the constructor signatures rather than by observing that no call happened
    /// during a test run. "No model was called today" is an observation about one execution;
    /// "no model is reachable" is a property of the type. Only the second survives someone adding
    /// a chat client to the evaluator later.
    /// </para>
    /// <para>
    /// Matched on the type name so this does not bind to one client abstraction. If the model
    /// dependency arrives under a different name, the collaborator scan below is the second net.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Replay)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void No_type_on_the_replay_path_takes_a_chat_client()
    {
        Type[] replayPath =
        [
            typeof(CoachTurnGroundingEvaluator),
            typeof(CoachClaimRuleEngine)
        ];

        var modelDependencies = replayPath
            .SelectMany(type => type.GetConstructors())
            .SelectMany(ctor => ctor.GetParameters().Select(p => (Ctor: ctor, Parameter: p)))
            .Where(pair => LooksLikeAModel(pair.Parameter.ParameterType))
            .Select(pair =>
                $"{pair.Ctor.DeclaringType?.Name}({pair.Parameter.ParameterType.Name} {pair.Parameter.Name})")
            .ToList();

        modelDependencies.Should().BeEmpty(
            "plan §14 tier 4 requires the replay to compose an answer with no model call. A model "
            + "dependency on the replay path makes the verdict non-deterministic and makes every "
            + "soak zero a sample rather than a measurement. Found: {0}",
            string.Join("; ", modelDependencies));
    }

    /// <summary>
    /// Tier 4. Nothing the replay path holds is a model either.
    /// </summary>
    /// <remarks>
    /// The constructor scan above catches injection. This catches the other way in: a field
    /// resolved from a service locator, a static, or a property. Together they close the two routes
    /// a model could take onto a path that must stay pure.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Replay)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void No_field_on_the_replay_path_holds_a_chat_client()
    {
        Type[] replayPath =
        [
            typeof(CoachTurnGroundingEvaluator),
            typeof(CoachClaimRuleEngine)
        ];

        var held = replayPath
            .SelectMany(type => type
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(field => (Type: type, Member: (MemberInfo)field, MemberType: field.FieldType))
                .Concat(type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Select(prop => (Type: type, Member: (MemberInfo)prop, MemberType: prop.PropertyType))))
            .Where(entry => LooksLikeAModel(entry.MemberType))
            .Select(entry => $"{entry.Type.Name}.{entry.Member.Name} : {entry.MemberType.Name}")
            .ToList();

        held.Should().BeEmpty(
            "a model reached through a field is the same defect as one reached through a "
            + "constructor, and is harder to see in review. Found: {0}",
            string.Join("; ", held));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fixture. Synthetic shape only — §14 fixture rule.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>True when the type is a model client under any of the names one could arrive as.</summary>
    private static bool LooksLikeAModel(Type type)
    {
        var name = type.Name;
        return name.Contains("ChatClient", StringComparison.Ordinal)
            || name.Contains("ChatCompletion", StringComparison.Ordinal)
            || name.Contains("LanguageModel", StringComparison.Ordinal);
    }

    /// <summary>
    /// One recorded turn: an answer stating a count the recorded evidence does not support.
    /// Shape only — 84 matched against 20 returned, and a stated 42 that is neither.
    /// </summary>
    private static CoachTurnGroundingResult Replay(CoachTurnGroundingEvaluator evaluator) =>
        evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            ClaimFixture.Answer("You have 42 words due this week."),
            evidence: [ClaimFixture.Evidence(matched: 84, returned: 20)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

    /// <summary>
    /// The verdict, flattened to a comparable string. Every field the soak or the report can read,
    /// so a drift in any one of them fails the determinism assertion rather than hiding behind a
    /// field the comparison forgot.
    /// </summary>
    private static string Describe(CoachTurnGroundingResult result)
    {
        var summary = result.Grounding;
        var codes = result.Record is null
            ? "-"
            : string.Join(
                ",",
                result.Record.Findings
                    .Select(f => $"{f.Rule}:{f.Action}:{f.ClaimedCount}/{f.EvidenceCount}")
                    .OrderBy(text => text, StringComparer.Ordinal));

        var ruleCounts = summary is null
            ? "-"
            : string.Join(
                ",",
                summary.RuleCounts
                    .Select(count => count.ToString())
                    .OrderBy(text => text, StringComparer.Ordinal));

        return string.Join(
            "|",
            result.Refused,
            result.Answer,
            codes,
            ruleCounts,
            summary?.RequestedStage.ToString() ?? "-",
            summary?.Refused.ToString() ?? "-",
            summary?.Altered.ToString() ?? "-",
            summary?.RepairSuppressedForLanguage.ToString() ?? "-",
            summary?.SubstitutionAllowed.ToString() ?? "-",
            summary?.LimitationCode?.ToString() ?? "-",
            summary?.ShadowLabel.ToString() ?? "-",
            summary?.FindingCount.ToString() ?? "-");
    }

    private static CoachTurnGroundingEvaluator Evaluator()
    {
        var resolver = new StubCapabilityResolver();
        var manifest = new StubCapabilityManifest();

        return new CoachTurnGroundingEvaluator(
            new CoachClaimRuleEngine(resolver, manifest),
            resolver,
            NullLogger<CoachTurnGroundingEvaluator>.Instance);
    }
}
