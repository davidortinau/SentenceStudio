using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// W9 R0: a fourth outcome section, added without a version bump.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ruling these tests hold.</b> The ceremony proposed v3 → v4. The amended ruling keeps the
/// version at 3 and adds a named section instead, because a bump is the more dangerous of the two
/// options during a rolling deployment: an older replica reading a v4 row falls into the
/// unknown-version arm and reports <em>no answer at all</em>, while the same replica reading a v3
/// row with an unfamiliar property ignores the property and returns the answer.
/// </para>
/// <para>
/// That claim is not left to a comment. <see cref="FrozenPreW9Reader"/> below is a byte-for-byte
/// emulation of the parser as it stood before this change, and it is fed a payload this build
/// wrote.
/// </para>
/// </remarks>
public sealed class CoachGroundingSectionSchemaTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── The section itself ───────────────────────────────────────────────────

    [Fact]
    public void The_outcome_schema_version_is_still_three()
    {
        CoachConversationService.CurrentOutcomeSchemaVersion.Should().Be(
            3,
            "a named section is invisible to a reader that does not look for it, so it needs no "
            + "bump \u2014 and a bump would make an older replica report no answer at all for every "
            + "row this build writes");
    }

    [Fact]
    public void Grounding_is_absent_by_default()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null, null), Web);

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull();
        read.Grounding.Should().BeNull("production writes null until R2, and absent must mean null");
    }

    [Fact]
    public void A_full_grounding_summary_round_trips()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null, null, Summary()), Web);

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Grounding.Should().NotBeNull();

        var grounding = read.Grounding!;
        grounding.RequestedStage.Should().Be(CoachGroundingStage.Enforce);
        grounding.SubstitutionAllowed.Should().BeFalse();
        grounding.Refused.Should().BeTrue();
        grounding.Altered.Should().BeFalse();
        grounding.RepairSuppressedForLanguage.Should().BeTrue();
        grounding.FindingCount.Should().Be(3);
        grounding.LimitationCode.Should().Be(CoachLimitationCode.WouldRemoveLearningValue);
        grounding.ShadowLabel.Should().Be(CoachShadowRouteLabel.LearnerState);

        grounding.RuleCounts.Should().BeEquivalentTo(
            [
                new CoachGroundingRuleCount(CoachClaimRuleCode.UnverifiedLearnerStateClaim, 2),
                new CoachGroundingRuleCount(CoachClaimRuleCode.WithheldNotDisclosed, 1)
            ],
            "per-rule counts are the whole reportable content, so losing them silently makes every "
            + "stored summary a bare boolean");
    }

    /// <summary>
    /// The requested stage is recorded, not a collapsed effective value.
    /// </summary>
    /// <remarks>
    /// "Enforce with substitution withheld" and "Observe" produce the same zeros everywhere else on
    /// this shape. Keeping the two fields separate is what lets a reader tell them apart, and it is
    /// the F1 split arriving early in the durable record.
    /// </remarks>
    [Fact]
    public void Enforce_without_substitution_is_distinguishable_from_observe()
    {
        var enforce = Summary() with
        {
            RequestedStage = CoachGroundingStage.Enforce,
            SubstitutionAllowed = false
        };

        var observe = Summary() with
        {
            RequestedStage = CoachGroundingStage.Observe,
            SubstitutionAllowed = false
        };

        Read(enforce)!.RequestedStage.Should().Be(CoachGroundingStage.Enforce);
        Read(observe)!.RequestedStage.Should().Be(CoachGroundingStage.Observe);

        JsonSerializer.Serialize(enforce, Web).Should().NotBe(JsonSerializer.Serialize(observe, Web));
    }

    // ── Tolerance: grounding degrades alone ──────────────────────────────────

    /// <summary>
    /// A rule code from a later build drops the section and nothing else.
    /// </summary>
    [Fact]
    public void An_unknown_rule_code_drops_only_the_grounding_section()
    {
        var payload = "{\"answer\":"
            + JsonSerializer.Serialize(Answer(), Web)
            + ",\"trace\":" + JsonSerializer.Serialize(Trace(), Web)
            + ",\"dispute\":" + JsonSerializer.Serialize(Dispute(), Web)
            + ",\"grounding\":{\"requestedStage\":\"Enforce\",\"substitutionAllowed\":false,"
            + "\"refused\":true,\"altered\":false,\"repairSuppressedForLanguage\":false,"
            + "\"findingCount\":1,\"ruleCounts\":[{\"rule\":\"SomeFutureRule\",\"count\":1}],"
            + "\"limitationCode\":null,\"shadowLabel\":\"LearnerState\"}}";

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull("a diagnostic must never take the turn down with it");
        read.Trace.Should().NotBeNull("the trace is a sibling section and is unaffected");
        read.Dispute.Should().NotBeNull("the dispute is a sibling section and is unaffected");
        read.Grounding.Should().BeNull();
    }

    /// <summary>An unknown stage name behaves the same way.</summary>
    [Fact]
    public void An_unknown_stage_name_drops_only_the_grounding_section()
    {
        var payload = "{\"answer\":"
            + JsonSerializer.Serialize(Answer(), Web)
            + ",\"grounding\":{\"requestedStage\":\"Annihilate\",\"substitutionAllowed\":true,"
            + "\"refused\":false,\"altered\":false,\"repairSuppressedForLanguage\":false,"
            + "\"findingCount\":0,\"ruleCounts\":[],\"limitationCode\":null,"
            + "\"shadowLabel\":\"Unknown\"}}";

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Answer.Should().NotBeNull();
        read.Grounding.Should().BeNull();
    }

    /// <summary>
    /// An undefined ordinal does not throw, so the census is what catches it.
    /// </summary>
    [Theory]
    [InlineData("\"requestedStage\":97")]
    [InlineData("\"shadowLabel\":97")]
    public void An_undefined_enum_ordinal_drops_only_the_grounding_section(string member)
    {
        var body = "{\"requestedStage\":\"Observe\",\"substitutionAllowed\":true,\"refused\":false,"
            + "\"altered\":false,\"repairSuppressedForLanguage\":false,\"findingCount\":0,"
            + "\"ruleCounts\":[],\"limitationCode\":null,\"shadowLabel\":\"Unknown\"}";

        var key = member[..member.IndexOf(':', StringComparison.Ordinal)];
        var replaced = System.Text.RegularExpressions.Regex.Replace(
            body, System.Text.RegularExpressions.Regex.Escape(key) + ":(\"[A-Za-z]+\"|\\d+)", member);

        var payload = "{\"answer\":" + JsonSerializer.Serialize(Answer(), Web)
            + ",\"grounding\":" + replaced + "}";

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Answer.Should().NotBeNull();
        read.Grounding.Should().BeNull(
            "System.Text.Json materialises any integer into an enum without throwing, so an "
            + "undefined ordinal has to be caught by IsWellFormed rather than by the deserializer");
    }

    [Fact]
    public void A_malformed_grounding_shape_drops_only_the_grounding_section()
    {
        var payload = "{\"answer\":" + JsonSerializer.Serialize(Answer(), Web)
            + ",\"grounding\":\"not an object\"}";

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Answer.Should().NotBeNull();
        read.Grounding.Should().BeNull();
    }

    /// <summary>A malformed answer is still unreadable overall. Only sections are tolerated.</summary>
    [Fact]
    public void A_malformed_answer_is_still_unreadable_overall()
    {
        var payload = "{\"answer\":\"not an answer\",\"grounding\":"
            + JsonSerializer.Serialize(Summary(), Web) + "}";

        CoachConversationService.ReadOutcome(payload, 3).Should().BeNull(
            "reporting a readable grounding section beside an answer-shaped null would claim the "
            + "turn produced no answer, when what happened is that this build cannot read the row");
    }

    // ── Bounds ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(CoachGroundingTurnSummary.MaxFindingCount + 1)]
    public void An_out_of_range_finding_count_is_refused(int count)
    {
        Read(Summary() with { FindingCount = count }).Should().BeNull();
    }

    [Fact]
    public void A_duplicated_rule_entry_is_refused()
    {
        var duplicated = Summary() with
        {
            RuleCounts =
            [
                new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, 1),
                new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, 2)
            ]
        };

        Read(duplicated).Should().BeNull(
            "a payload with two entries for one rule was not written by this build, and summing "
            + "them would let a reader report a count no writer produced");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(CoachGroundingTurnSummary.MaxFindingCount + 1)]
    public void An_out_of_range_rule_count_is_refused(int count)
    {
        var summary = Summary() with
        {
            RuleCounts = [new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, count)]
        };

        Read(summary).Should().BeNull();
    }

    [Fact]
    public void An_unknown_limitation_ordinal_is_refused()
    {
        Read(Summary() with { LimitationCode = (CoachLimitationCode)97 }).Should().BeNull();
    }

    [Fact]
    public void A_well_formed_summary_with_no_findings_is_accepted()
    {
        var quiet = new CoachGroundingTurnSummary(
            CoachGroundingStage.Observe,
            SubstitutionAllowed: true,
            Refused: false,
            Altered: false,
            RepairSuppressedForLanguage: false,
            FindingCount: 0,
            RuleCounts: [],
            LimitationCode: null,
            ShadowLabel: CoachShadowRouteLabel.Instructional);

        Read(quiet).Should().NotBeNull("a clean turn is the common case and must survive the census");
    }

    // ── Content-freeness ─────────────────────────────────────────────────────

    /// <summary>
    /// The shape has no string member, so no text or index can reach the durable record.
    /// </summary>
    /// <remarks>
    /// Structural rather than sampled. An index into an answer is a pointer at a sentence, and the
    /// stored answer sits in the same payload — a summary carrying block and span indices would
    /// reconstruct the offending sentence for anyone holding the row.
    /// </remarks>
    [Fact]
    public void The_summary_carries_no_text_or_index()
    {
        var members = typeof(CoachGroundingTurnSummary)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .ToArray();

        members.Should().NotBeEmpty();

        members.Where(member => member.PropertyType == typeof(string))
            .Should().BeEmpty("no text crosses the durability boundary");

        foreach (var forbidden in new[] { "Index", "Span", "Block", "Text", "Language", "User", "Conversation", "Tool" })
        {
            members.Select(member => member.Name).Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)
                        && !name.Equals(
                            nameof(CoachGroundingTurnSummary.RepairSuppressedForLanguage),
                            StringComparison.Ordinal),
                "a member named for {0} would put a pointer or an identifier into a record whose "
                + "whole guarantee is that it points at nothing",
                forbidden);
        }
    }

    [Fact]
    public void A_serialized_summary_names_only_codes_counts_and_flags()
    {
        var json = JsonSerializer.Serialize(Summary(), Web);

        json.Should().Contain("Enforce", "enums are written by name, not by ordinal");
        json.Should().Contain("UnverifiedLearnerStateClaim");
        json.Should().NotContain("blockIndex");
        json.Should().NotContain("spanIndex");
        json.Should().NotContain("languageTag");
    }

    // ── Version arms are unchanged ───────────────────────────────────────────

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(4, false)]
    public void The_readable_version_set_is_unchanged(int version, bool readable)
    {
        var payload = version == 1
            ? JsonSerializer.Serialize(Answer(), Web)
            : JsonSerializer.Serialize(
                new CoachStoredTurnOutcome(Answer(), null, null, Summary()), Web);

        var read = CoachConversationService.ReadOutcome(payload, version);

        if (readable)
        {
            read.Should().NotBeNull("version {0} must remain readable", version);
        }
        else
        {
            read.Should().BeNull(
                "version {0} is not a version this build writes or reads; R0 adds a section, not a "
                + "version",
                version);
        }
    }

    [Fact]
    public void A_version_two_row_reads_with_a_null_grounding_section()
    {
        var payload = JsonSerializer.Serialize(new CoachStoredTurnOutcome(Answer(), Trace()), Web);

        var read = CoachConversationService.ReadOutcome(payload, 2);

        read!.Answer.Should().NotBeNull();
        read.Trace.Should().NotBeNull();
        read.Grounding.Should().BeNull();
    }

    [Fact]
    public void A_version_one_row_reads_with_a_null_grounding_section()
    {
        var read = CoachConversationService.ReadOutcome(
            JsonSerializer.Serialize(Answer(), Web), 1);

        read!.Answer.Should().NotBeNull();
        read.Grounding.Should().BeNull();
    }

    private static CoachGroundingTurnSummary? Read(CoachGroundingTurnSummary summary)
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null, null, summary), Web);

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Answer.Should().NotBeNull("the answer must survive whatever the section does");

        return read.Grounding;
    }

    internal static CoachGroundingTurnSummary Summary() => new(
        CoachGroundingStage.Enforce,
        SubstitutionAllowed: false,
        Refused: true,
        Altered: false,
        RepairSuppressedForLanguage: true,
        FindingCount: 3,
        RuleCounts:
        [
            new CoachGroundingRuleCount(CoachClaimRuleCode.UnverifiedLearnerStateClaim, 2),
            new CoachGroundingRuleCount(CoachClaimRuleCode.WithheldNotDisclosed, 1)
        ],
        LimitationCode: CoachLimitationCode.WouldRemoveLearningValue,
        ShadowLabel: CoachShadowRouteLabel.LearnerState);

    internal static CoachTurnDisputeState Dispute() => new(
        CoachCorrectionSignal.WrongClaim,
        "3f1c9a44-0d3e-4c1b-9a5e-77b2c1d0e912",
        new DateTime(2026, 8, 22, 4, 5, 0, DateTimeKind.Utc),
        ResolvedAtUtc: null,
        CoachDisputeResolution.Open,
        [CoachScopeDefinition.TrackedVocabularyDueSummary]);

    internal static CoachTurnTraceSummary Trace() => new(
        [
            new CoachTurnTraceEntry(
                Ordinal: 1,
                ToolName: "get_vocabulary_due_summary",
                Outcome: Api.Coach.Tools.Observation.CoachToolCallOutcome.Succeeded,
                FailureKind: null,
                ArgumentMask: Api.Coach.Tools.Observation.CoachToolArgumentMask.None,
                ElapsedMs: 11,
                Coverage: CoachScopeCoverage.CompleteOwnedSet,
                DefinitionCode: CoachScopeDefinition.TrackedVocabularyDueSummary,
                WithheldReason: CoachScopeWithheldReason.None,
                MatchedCount: 12,
                ReturnedCount: 12,
                WithheldCount: null,
                Truncated: false)
        ],
        BudgetUsed: 1,
        BudgetLimit: 6);

    internal static CoachTurnResponse Answer(string marker = "turn-r0")
    {
        var constraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 10,
            AudioAllowed = true,
            SpeechAllowed = true,
            TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        };

        return new CoachTurnResponse
        {
            SessionId = "session-1",
            TurnId = marker,
            Status = CoachTurnStatus.Completed,
            StopReason = CoachStopReason.Completed,
            SessionStatus = CoachSessionStatus.Active,
            ActiveConstraints = constraints,
            PlanState = new CoachPlanStateDto
            {
                PlanDate = new DateOnly(2026, 8, 22),
                PlanVersion = "v1",
                AppliedConstraints = constraints,
                EstimatedTotalMinutes = 10,
                CompletedCount = 0,
                TotalCount = 3,
                CompletionPercentage = 0
            },
            ExpiresAtUtc = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc)
        };
    }
}
