using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.Wire;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// What a shipped answer tells the learner about the grounding layer's handling of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the refusal path, and deliberately so.</b> A refused turn delivers no answer,
/// so its honest statement is a limitation: here is what I could not tell you and where to look.
/// A turn that ships an answer the layer rewrote has the opposite problem — the learner is holding
/// text that is not what the coach first produced, and nothing on the wire said so. That is the
/// gap these close.
/// </para>
/// <para>
/// <b>Count-free by construction.</b> The disclosure is one closed enum. No finding counts, no rule
/// codes, no spans, no server prose. A learner does not need to know that two rules fired on three
/// spans; they need to know the text was changed. Counts belong on the operator report, which
/// already has them, and putting them here would make an ordinary answer carry a diagnostic
/// payload the learner cannot act on.
/// </para>
/// <para>
/// <b>Turn tests, not projection tests.</b> Every case that can be driven through
/// <c>SubmitTurnAsync</c> is, for the same reason the grounding-path suite is: a projection test
/// passes happily while nothing calls the projection. The two direct-projection cases at the bottom
/// cover a state the turn path cannot reach.
/// </para>
/// </remarks>
public sealed class CoachRepairDisclosureTests
{
    private const string Question = "Am I doing well with these words?";

    /// <summary>A learner-state claim no read supports. The rule that fires has a substitute.</summary>
    private const string UnverifiedClaim = "You have reviewed these words plenty of times already.";

    /// <summary>Instructional text no rule touches.</summary>
    private const string InstructionalText = "The verb ending changes with the politeness level.";

    // ─────────────────────────────────────────────────────────── the answer changed

    [Fact]
    public async Task An_english_answer_the_layer_rewrote_says_so()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Repair);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        SpanText(response.Answer!, 0).Should().Be(CoachDeterministicCopy.UncheckedLearnerState,
            "the premise of the disclosure is that a substitution actually happened");

        response.RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered,
            "the learner is holding text the coach did not write, and nothing else on the wire "
            + "would tell them");

        response.Limitation.Should().BeNull("the answer shipped, so there is no limitation");
    }

    [Fact]
    public async Task Both_arms_disclose_the_rewrite()
    {
        foreach (var arm in new[] { CoachImplementation.Baseline, CoachImplementation.Harness })
        {
            using var harness = new CoachApplicationHarness();
            harness.Options.CurrentValue.Implementation = arm;
            harness.SetGroundingStage(CoachGroundingStage.Repair);
            harness.SeedFailedRead();

            var response = await AskAsync(harness, TwoSpanAnswer());

            response.RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered,
                $"the {arm} route composes its answer through the same builder");
        }
    }

    // ───────────────────────────────────────────────── the answer could not be repaired

    [Fact]
    public async Task A_korean_answer_the_layer_left_alone_says_the_repair_was_withheld()
    {
        using var harness = new CoachApplicationHarness();
        harness.Languages.Profile = new CoachLanguageProfile("ko-KR", "ko-KR", "ko-KR");
        harness.SetGroundingStage(CoachGroundingStage.Repair);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        SpanText(response.Answer!, 0).Should().Be(UnverifiedClaim,
            "no English constant was written into a Korean answer, which is the whole reason the "
            + "repair was suppressed");

        response.RepairDisclosure.Should().Be(CoachRepairDisclosure.RepairSuppressedForLanguage,
            "the finding stands and the text is unchanged: a Korean learner is entitled to know "
            + "the coach could not act on what it found, rather than being told nothing happened");
    }

    [Fact]
    public async Task A_korean_answer_with_nothing_to_find_is_clean_not_suppressed()
    {
        using var harness = new CoachApplicationHarness();
        harness.Languages.Profile = new CoachLanguageProfile("ko-KR", "ko-KR", "ko-KR");
        harness.SetGroundingStage(CoachGroundingStage.Repair);

        var response = await AskAsync(harness, InstructionalOnlyAnswer());

        response.RepairDisclosure.Should().Be(CoachRepairDisclosure.None,
            "suppression is a fact about a finding the layer could not act on, not a property of "
            + "writing in Korean; a clean Korean answer is clean");
    }

    // ─────────────────────────────────────────────────────────────── refusal

    [Fact]
    public async Task A_refused_turn_carries_a_limitation_and_no_disclosure()
    {
        using var harness = new CoachApplicationHarness();
        harness.Languages.Profile = new CoachLanguageProfile("ko-KR", "ko-KR", "ko-KR");
        harness.SetGroundingStage(CoachGroundingStage.Enforce);

        // Rows were withheld and the reason is not knowable, so the answer cannot truthfully
        // disclose the withholding and there is no substitute that would make it truthful.
        harness.SeedWithheldVocabularyRead(
            reason: SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason.None);

        var response = await AskAsync(harness, SilentAboutWithholdingAnswer());

        response.Status.Should().Be(CoachTurnStatus.Rejected);
        response.Limitation.Should().NotBeNull("the refusal path speaks through the limitation");

        response.RepairDisclosure.Should().BeNull(
            "there is no answer in front of the learner for a disclosure to be about, and shipping "
            + "both would have the coach describing an answer it withheld");
    }

    // ───────────────────────────────────────────────────────── the layer was off

    [Fact]
    public async Task Off_discloses_nothing_at_all()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Off);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        SpanText(response.Answer!, 0).Should().Be(UnverifiedClaim);

        response.RepairDisclosure.Should().BeNull(
            "null is 'not checked', which is a different statement from None's 'checked and left "
            + "alone'; collapsing them would let an unguarded host claim a clean bill of health");
    }

    [Fact]
    public async Task Observe_found_something_and_still_reports_the_answer_untouched()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Observe);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        SpanText(response.Answer!, 0).Should().Be(UnverifiedClaim, "Observe never alters");
        harness.ClaimFindings.Record!.HasFindings.Should().BeTrue("it did find something");

        response.RepairDisclosure.Should().Be(CoachRepairDisclosure.None,
            "the disclosure is about what was done to the answer, not what was noticed; the count "
            + "is the operator report's business and the learner's answer is genuinely untouched");
    }

    [Fact]
    public async Task A_clean_answer_at_repair_reports_none()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Repair);

        var response = await AskAsync(harness, InstructionalOnlyAnswer());

        response.RepairDisclosure.Should().Be(CoachRepairDisclosure.None);
        response.Limitation.Should().BeNull();
    }

    // ──────────────────────────────────────────────── precedence and projection

    [Fact]
    public void An_altered_answer_wins_over_a_suppression_claim()
    {
        // The turn path cannot produce both — suppression is exactly the condition under which
        // nothing is substituted — but the summary is a frozen record with two independent bools,
        // and a future rung could set both. Deciding it here beats discovering it in production.
        var disclosure = CoachRefusalLimitationProjection.ProjectDisclosure(
            Summary(altered: true, suppressed: true), refused: false);

        disclosure.Should().Be(CoachRepairDisclosure.AnswerAltered,
            "the learner can verify an alteration by reading the text; telling them the repair was "
            + "withheld when it was applied is the one statement here that would be false");
    }

    [Fact]
    public void A_refusal_never_projects_a_disclosure_however_the_summary_reads()
    {
        CoachRefusalLimitationProjection.ProjectDisclosure(
            Summary(altered: true, suppressed: false), refused: true)
            .Should().BeNull();

        CoachRefusalLimitationProjection.ProjectDisclosure(
            Summary(altered: false, suppressed: true), refused: true)
            .Should().BeNull();

        CoachRefusalLimitationProjection.ProjectDisclosure(summary: null, refused: false)
            .Should().BeNull("no summary means the layer never ran");
    }

    // ────────────────────────────────────────────────────────── the wire shape

    [Fact]
    public void The_disclosure_carries_no_text_of_any_kind()
    {
        typeof(CoachRepairDisclosure).IsEnum.Should().BeTrue(
            "an enum cannot carry a term, a gloss, a span, or a server sentence; making this a DTO "
            + "would reopen every one of those");

        Enum.GetUnderlyingType(typeof(CoachRepairDisclosure)).Should().Be(typeof(int));

        typeof(CoachTurnResponse).GetProperty(nameof(CoachTurnResponse.RepairDisclosure))!
            .PropertyType.Should().Be(typeof(CoachRepairDisclosure?));

        typeof(CoachSessionResponse).GetProperty(nameof(CoachSessionResponse.RepairDisclosure))!
            .PropertyType.Should().Be(typeof(CoachRepairDisclosure?));
    }

    [Fact]
    public void The_ordinals_are_pinned()
    {
        // A client stores these. Renumbering would silently reinterpret every persisted turn.
        ((int)CoachRepairDisclosure.Unknown).Should().Be(0);
        ((int)CoachRepairDisclosure.None).Should().Be(1);
        ((int)CoachRepairDisclosure.AnswerAltered).Should().Be(2);
        ((int)CoachRepairDisclosure.RepairSuppressedForLanguage).Should().Be(3);

        Enum.GetValues<CoachRepairDisclosure>().Should().HaveCount(4,
            "a new member is a wire change and must be a deliberate one");
    }

    [Fact]
    public void An_absent_or_defaulted_value_is_neutral_rather_than_a_claim()
    {
        // The wire property is nullable, so an old server that never writes it leaves the client
        // with null — "not checked" — and not a claim about the text.
        var absent = JsonSerializer.Deserialize<NullableHolder>(
            "{}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        absent!.Disclosure.Should().BeNull();

        // And a default-constructed value is Unknown, never None: a struct that was never assigned
        // must not read as "the layer checked this and it was clean".
        default(CoachRepairDisclosure).Should().Be(CoachRepairDisclosure.Unknown,
            "zero is the neutral rung, which is what makes the declared SafeZero fallback safe");
    }

    [Fact]
    public void The_enum_is_wired_exactly_as_its_siblings_on_the_same_response_are()
    {
        // CoachLimitationCode rides the same DTO and is the precedent. Diverging here — a bespoke
        // tolerant converter on one enum and not the other — is how two properties on one response
        // end up with different behaviour on an old client.
        static JsonConverterAttribute? Converter(Type type) =>
            type.GetCustomAttribute<JsonConverterAttribute>();

        Converter(typeof(CoachRepairDisclosure))!.ConverterType
            .Should().Be(Converter(typeof(CoachLimitationCode))!.ConverterType,
                "the two closed enums on CoachTurnResponse serialise the same way");
    }

    [Fact]
    public void The_fallback_is_declared_where_the_census_can_see_it()
    {
        var attribute = typeof(CoachRepairDisclosure)
            .GetCustomAttribute<WireEnumFallbackAttribute>();

        attribute.Should().NotBeNull("the W1 census enumerates declared fallbacks, not conventions");
        attribute!.MemberName.Should().Be(nameof(CoachRepairDisclosure.Unknown));
        attribute.Kind.Should().Be(WireEnumFallbackKind.SafeZero);
    }

    [Fact]
    public void A_turn_response_serialises_the_disclosure_as_a_name()
    {
        var json = JsonSerializer.Serialize(
            new Holder { Disclosure = CoachRepairDisclosure.AnswerAltered },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("AnswerAltered", "names survive renumbering; ordinals do not");
        json.Should().NotContain("\"2\"");
    }

    // ──────────────────────────────────────────────────────── call-site guard

    [Fact]
    public async Task The_disclosure_is_projected_once_and_only_on_the_ship_branch()
    {
        await Task.CompletedTask;

        var service = await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Api", "Coach", "Application",
            "CoachSessionService.cs"));

        var code = string.Join('\n', service.Split('\n').Select(line =>
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment >= 0 ? line[..comment] : line;
        }));

        Count(code, "ProjectDisclosure(").Should().Be(
            1, "one projection site, next to the summary it reads, so the two cannot drift apart");

        code.Should().Contain("RepairDisclosure = limitation is null ? _turnRepairDisclosure : null",
            "the response builder refuses the pair a second time; the projection already does, and "
            + "two independent guards are what stop a future edit surfacing both");
    }

    // ───────────────────────────────────────────────────────────── helpers

    private sealed class Holder
    {
        public CoachRepairDisclosure Disclosure { get; set; }
    }

    private sealed class NullableHolder
    {
        public CoachRepairDisclosure? Disclosure { get; set; }
    }

    private static CoachGroundingTurnSummary Summary(bool altered, bool suppressed) => new(
        RequestedStage: CoachGroundingStage.Repair,
        SubstitutionAllowed: !suppressed,
        Refused: false,
        Altered: altered,
        RepairSuppressedForLanguage: suppressed,
        FindingCount: 1,
        RuleCounts: [],
        LimitationCode: null,
        ShadowLabel: CoachShadowRouteLabel.Unknown);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static int Count(string source, string token)
    {
        var count = 0;
        var index = 0;

        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string SpanText(CoachAnswerDto answer, int index) =>
        answer.Blocks[0].Spans[index].Text;

    private static async Task<CoachTurnResponse> AskAsync(
        CoachApplicationHarness harness,
        CoachPedagogicalAnswerIntent answer)
    {
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                PedagogicalAnswer = answer,
                CoachMessage = string.Empty
            }
        };

        var result = await harness.Service.SubmitTurnAsync(
            sessionId,
            new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = Question },
            CoachTurnExecutionContext.Default);

        result.IsOk.Should().BeTrue();
        return result.Value!;
    }

    private static CoachPedagogicalAnswerIntent TwoSpanAnswer() =>
        Answer(UnverifiedClaim, InstructionalText);

    private static CoachPedagogicalAnswerIntent InstructionalOnlyAnswer() =>
        Answer(InstructionalText);

    private static CoachPedagogicalAnswerIntent SilentAboutWithholdingAnswer() =>
        Answer(InstructionalText);

    private static CoachPedagogicalAnswerIntent Answer(params string[] spans) => new()
    {
        Topic = CoachAnswerTopic.Vocabulary,
        Blocks =
        [
            new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Answer,
                Spans = [.. spans.Select(text => new CoachAnswerSpanIntent
                {
                    Text = text,
                    Language = CoachLanguageRole.Display
                })]
            }
        ]
    };
}
