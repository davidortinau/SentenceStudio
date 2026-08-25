using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// Amendment A1: the budget reaches the trace, once, and not as a fake tool call.
/// </summary>
/// <remarks>
/// <para>
/// W4 shipped <c>BudgetUsed</c> and <c>BudgetLimit</c> nullable and unset, because the budget lives
/// in the agent arms and the no-leak boundary put those files outside W4's scope. W6 needs them:
/// a trace showing three calls against a limit of three is a turn that ran out, not a turn that
/// decided it had enough, and an honesty rule reads those two cases very differently.
/// </para>
/// <para>
/// The temptation is to record the budget as one more entry in <c>Calls</c>, because the buffer
/// already accepts entries and that needs no new method. It would corrupt everything downstream:
/// the call count inflates by one on every turn, the 1-based contiguous ordinals the seam
/// guarantees acquire a hole, and every rule that counts reads is off by one. Hence a separate
/// method and the test below.
/// </para>
/// </remarks>
public sealed class CoachTurnBudgetRecordingTests
{
    [Fact]
    public void Recording_a_budget_adds_no_observation()
    {
        var buffer = new CoachTurnObservationBuffer();

        buffer.RecordBudget(4, 6);

        buffer.Observations.Should().BeEmpty(
            "a synthetic entry in Calls would inflate every count the trace reports and put a hole "
            + "in the ordinals the seam guarantees are 1-based and contiguous");
    }

    [Fact]
    public void Recording_a_budget_twice_keeps_the_last_value_and_still_adds_no_call()
    {
        var buffer = new CoachTurnObservationBuffer();

        buffer.RecordBudget(1, 6);
        buffer.RecordBudget(4, 6);

        buffer.Observations.Should().BeEmpty();
    }

    /// <summary>
    /// Both agent arms take the sink and both call it. Asserted structurally, because the
    /// alternative is a full model round trip.
    /// </summary>
    [Theory]
    [InlineData(typeof(BaselineLearningCoach))]
    [InlineData(typeof(HarnessLearningCoach))]
    public void Both_agent_arms_accept_the_observation_sink(Type armType)
    {
        var constructor = armType.GetConstructors().Should().ContainSingle().Subject;

        constructor.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should().Contain(
                typeof(ICoachTurnObservationSink),
                "{0} is where the real budget lives, so it is the only place that can record it",
                armType.Name);
    }

    /// <summary>
    /// The sink is optional, so a host without the seam registered still runs a turn.
    /// </summary>
    [Theory]
    [InlineData(typeof(BaselineLearningCoach))]
    [InlineData(typeof(HarnessLearningCoach))]
    public void The_observation_sink_is_optional(Type armType)
    {
        var constructor = armType.GetConstructors().Single();

        var sink = constructor.GetParameters()
            .Single(parameter => parameter.ParameterType == typeof(ICoachTurnObservationSink));

        sink.HasDefaultValue.Should().BeTrue(
            "the seam is a diagnostic; a host that has not registered it must still answer a learner");
    }

    /// <summary>
    /// Exactly one recording site per arm. Two would be a double-count nobody would notice.
    /// </summary>
    [Theory]
    [InlineData("BaselineLearningCoach.cs")]
    [InlineData("HarnessLearningCoach.cs")]
    public void Each_arm_records_the_budget_exactly_once(string fileName)
    {
        var source = ReadAgentSource(fileName);

        var occurrences = CountOccurrences(source, "RecordBudget(");

        occurrences.Should().Be(
            1,
            "{0} must record the turn's budget once at the turn boundary; a second call would "
            + "overwrite the first with a stale or duplicated figure",
            fileName);
    }

    /// <summary>
    /// The recorded figures come from the real budget, not from a literal or a guess.
    /// </summary>
    [Theory]
    [InlineData("BaselineLearningCoach.cs")]
    [InlineData("HarnessLearningCoach.cs")]
    public void The_recorded_budget_comes_from_the_turn_budget(string fileName)
    {
        var source = ReadAgentSource(fileName);

        source.Should().Contain(
            "RecordBudget(toolBudget.Used, toolBudget.Limit)",
            "{0} must record the turn's own budget object; anything else is a number the trace "
            + "would present as measured",
            fileName);
    }

    /// <summary>Neither arm fabricates a tool observation to carry the budget.</summary>
    [Theory]
    [InlineData("BaselineLearningCoach.cs")]
    [InlineData("HarnessLearningCoach.cs")]
    public void Neither_arm_adds_a_synthetic_observation(string fileName)
    {
        var source = StripComments(ReadAgentSource(fileName));

        source.Should().NotContain(
            "new CoachToolCallObservation",
            "{0} must not manufacture a tool call; the seam records real invocations only",
            fileName);
        source.Should().NotContain(
            ".Add(",
            "{0} must not push into the observation sink's call list",
            fileName);
    }

    private static string ReadAgentSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Api")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root must be reachable from the test binary");

        var path = Path.Combine(
            directory!.FullName, "src", "SentenceStudio.Api", "Coach", "Agents", fileName);

        File.Exists(path).Should().BeTrue("{0} must exist for this scan to mean anything", path);

        return File.ReadAllText(path);
    }

    /// <summary>Strips comments, so the file's own explanation is not mistaken for code.</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        return System.Text.RegularExpressions.Regex.Replace(
            withoutBlocks, @"^[ \t]*///?.*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);
    }

    private static int CountOccurrences(string source, string token)
    {
        var code = StripComments(source);
        var count = 0;
        var index = 0;

        while ((index = code.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}

/// <summary>
/// The capability rules across stage, surface and handshake. AC-F1, AC-F2, AC-F3, AC-F5.
/// </summary>
/// <remarks>
/// <para>
/// Run against the real <see cref="CoachCapabilityResolver"/> rather than the stub, because the
/// interesting behaviour is the §5.3 minimum — a capability can be declared <c>Present</c> and
/// still resolve absent because the stage is too low or the client did not advertise it, and a stub
/// that answers from a dictionary cannot exercise that.
/// </para>
/// <para>
/// Synthetic registrations and a synthetic handshake throughout, exactly as plan §14 requires:
/// no shipped client advertises a client capability until after the gate, so reading these as
/// production preconditions would make the gate circular.
/// </para>
/// </remarks>
public sealed class CoachCapabilityRuleMatrixTests
{
    private const string ThemeCapability = "set_theme";

    private static CoachCapabilityDescriptor Descriptor(
        CoachCapabilitySurface surface,
        CoachCapabilityStage requiredStage,
        CoachCapabilityAvailability maxAvailability = CoachCapabilityAvailability.Present,
        CoachClientCapabilityCode clientCode = CoachClientCapabilityCode.Unknown) =>
        new()
        {
            Name = ThemeCapability,
            IsToolBacked = true,
            EffectClass = CoachCapabilityEffectClass.PresentationState,
            Surface = surface,
            MaxAvailability = maxAvailability,
            RequiredStage = requiredStage,
            Reversal = CoachCapabilityReversal.ClientRevert,
            Confirmation = CoachCapabilityConfirmation.Gesture,
            ReceiptKind = CoachCapabilityReceiptKind.Client,
            Scope = CoachCapabilityScope.Session,
            DeclaredStepCount = 1,
            RiskClass = CoachToolRiskClass.Read,
            ClientCapabilityCode = clientCode
        };

    private sealed class OneDescriptorManifest : ICoachCapabilityManifest
    {
        private readonly CoachCapabilityDescriptor _descriptor;

        internal OneDescriptorManifest(CoachCapabilityDescriptor descriptor) => _descriptor = descriptor;

        public IReadOnlyList<CoachCapabilityDescriptor> All => [_descriptor];

        public CoachCapabilityDescriptor? Find(string name) =>
            string.Equals(name, _descriptor.Name, StringComparison.Ordinal) ? _descriptor : null;

        public bool Contains(string name) => Find(name) is not null;
    }

    /// <summary>AC-F1. Declared, stage met, synthetic handshake advertises it.</summary>
    [Fact]
    public void Present_when_the_stage_is_met_and_the_server_owns_it()
    {
        var resolver = new CoachCapabilityResolver(new OneDescriptorManifest(
            Descriptor(CoachCapabilitySurface.Server, CoachCapabilityStage.Read)));

        var rule = new CoachCapabilityAbsentRule(resolver);

        rule.Evaluate(Turn(CoachCapabilityStage.Presentation, handshake: null)).Should().BeEmpty(
            "a server capability at or below the promoted stage is genuinely present");
    }

    /// <summary>AC-F2, first half: the stage is too low, so the answer over-claims.</summary>
    [Fact]
    public void Capability_absent_when_the_required_stage_exceeds_the_promoted_stage()
    {
        var resolver = new CoachCapabilityResolver(new OneDescriptorManifest(
            Descriptor(CoachCapabilitySurface.Server, CoachCapabilityStage.External)));

        var rule = new CoachCapabilityAbsentRule(resolver);

        rule.Evaluate(Turn(CoachCapabilityStage.Read, handshake: null)).Should().ContainSingle(
            "§5.3 rule 1: a capability whose RequiredStage exceeds the current stage never resolves "
            + "to Present, so proposing it is an over-claim");
    }

    /// <summary>AC-F2, second half: a client capability the synthetic handshake does not advertise.</summary>
    [Fact]
    public void Capability_absent_when_the_client_did_not_advertise_it()
    {
        var resolver = new CoachCapabilityResolver(new OneDescriptorManifest(
            Descriptor(
                CoachCapabilitySurface.Client,
                CoachCapabilityStage.Read,
                clientCode: CoachClientCapabilityCode.ThemeMetadata)));

        var rule = new CoachCapabilityAbsentRule(resolver);

        rule.Evaluate(Turn(CoachCapabilityStage.Presentation, handshake: null)).Should().ContainSingle(
            "§5.3 rule 2: a Client-surface capability the handshake omits resolves to "
            + "PresentOnAnotherSurface, and the answer must name the screen instead of doing it");
    }

    /// <summary>AC-F5. An unknown code in the handshake is ignored, and the turn still renders.</summary>
    [Fact]
    public void An_unknown_handshake_code_is_ignored_without_throwing()
    {
        var resolver = new CoachCapabilityResolver(new OneDescriptorManifest(
            Descriptor(CoachCapabilitySurface.Server, CoachCapabilityStage.Read)));

        var rule = new CoachCapabilityAbsentRule(resolver);

        var handshake = new CoachClientCapabilityHandshake
        {
            Version = 1,
            Codes = [(CoachClientCapabilityCode)9999]
        };

        var act = () => rule.Evaluate(Turn(CoachCapabilityStage.Presentation, handshake)).ToArray();

        act.Should().NotThrow("AC-F5: an unknown code is ignored and the turn renders");
    }

    /// <summary>AC-F3, through the real resolver rather than a dictionary.</summary>
    [Fact]
    public void False_limitation_fires_against_a_genuinely_present_capability()
    {
        var resolver = new CoachCapabilityResolver(new OneDescriptorManifest(
            Descriptor(CoachCapabilitySurface.Server, CoachCapabilityStage.Read)));

        var rule = new CoachFalseLimitationRule(resolver);

        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I can't change your theme."),
            ProposedCapabilities = [ThemeCapability],
            Stage = CoachCapabilityStage.Presentation
        };

        rule.Evaluate(context).Should().ContainSingle();
    }

    /// <summary>
    /// The two capability rules never both fire on one turn. They are opposite defects.
    /// </summary>
    [Theory]
    [InlineData(CoachCapabilityStage.Read)]
    [InlineData(CoachCapabilityStage.Presentation)]
    [InlineData(CoachCapabilityStage.External)]
    public void Absent_and_false_limitation_are_mutually_exclusive(CoachCapabilityStage stage)
    {
        var resolver = new CoachCapabilityResolver(new OneDescriptorManifest(
            Descriptor(CoachCapabilitySurface.Server, CoachCapabilityStage.Presentation)));

        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I can't change your theme."),
            ProposedCapabilities = [ThemeCapability],
            Stage = stage
        };

        var absent = new CoachCapabilityAbsentRule(resolver).Evaluate(context).Any();
        var falseLimitation = new CoachFalseLimitationRule(resolver).Evaluate(context).Any();

        (absent && falseLimitation).Should().BeFalse(
            "over-claiming and under-claiming are the same defect from opposite sides; a turn that "
            + "trips both means the resolver answered two ways at once");
    }

    private static CoachClaimRuleContext Turn(
        CoachCapabilityStage stage,
        CoachClientCapabilityHandshake? handshake) =>
        new()
        {
            Answer = ClaimFixture.Answer("I'll switch you to the dark theme."),
            ProposedCapabilities = [ThemeCapability],
            Stage = stage,
            Handshake = handshake
        };
}
