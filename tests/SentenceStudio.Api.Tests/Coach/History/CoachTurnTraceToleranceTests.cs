using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// What a stored turn outcome does when the trace beside the answer is from the future.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> A version-2 payload was read with one
/// <c>Deserialize&lt;CoachStoredTurnOutcome&gt;</c>, so an enum member a later build named and this
/// one does not threw inside the trace and took the whole read down. <c>ReadOutcome</c> returned
/// null, and a completed turn read back as no answer at all — the learner lost their answer to a
/// diagnostic they cannot see and did not ask for. A rollback to the previous build after any
/// forward deployment would have done this to every turn written in between.
/// </para>
/// <para>
/// <b>The rule.</b> Tolerance is section-scoped. The answer is strict and is parsed on its own; the
/// trace is judged separately and, when this build cannot read it correctly, is dropped whole while
/// the answer survives. A payload that is not JSON, a root that is not an object, and an answer
/// that will not parse still make the entire row absent, because those are corruption rather than
/// version skew.
/// </para>
/// <para>
/// <b>Why the census is over all six enums.</b> They do not fail alike. The three scope enums carry
/// the string converter and throw on an unknown name; the three observation enums are written as
/// numbers and System.Text.Json accepts any integer silently. Testing only the loud half would have
/// left the quiet half — including an unknown <c>CoachToolArgumentMask</c> bit — reading back as a
/// confidently wrong presence set.
/// </para>
/// </remarks>
public sealed class CoachTurnTraceToleranceTests
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    // ====================================================================== census

    /// <summary>
    /// The census covers exactly the enum types the stored entry declares.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="CoachTurnTraceEntry"/>'s own members rather than from a list, so a
    /// seventh enum added to the entry fails here instead of being silently untested — which is the
    /// gap that let the first six ship without one.
    /// </remarks>
    [Fact]
    public void The_integrity_census_covers_every_enum_the_entry_declares()
    {
        var declared = typeof(CoachTurnTraceEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .Where(t => t.IsEnum)
            .Distinct()
            .ToList();

        declared.Should().HaveCount(6, "the trace persists six enum types");

        CoveredEnumTypes().Should().BeEquivalentTo(
            declared,
            "an enum the entry stores and the census does not judge is one an unknown value passes "
            + "through unchecked");
    }

    /// <summary>Every declared value of every covered enum reads back as readable.</summary>
    /// <remarks>
    /// The positive control. A census that rejected everything would pass every negative test in
    /// this file and destroy every trace in production.
    /// </remarks>
    [Fact]
    public void Every_declared_value_of_every_covered_enum_survives_the_census()
    {
        var checkedValues = 0;

        foreach (var outcome in Enum.GetValues<CoachToolCallOutcome>())
        {
            ReadTrace(TraceJson(entry => entry["outcome"] = (int)outcome))
                .Should().NotBeNull($"{outcome} is declared");
            checkedValues++;
        }

        foreach (var failure in Enum.GetValues<CoachToolFailureKind>())
        {
            ReadTrace(TraceJson(entry => entry["failureKind"] = (int)failure))
                .Should().NotBeNull($"{failure} is declared");
            checkedValues++;
        }

        foreach (var mask in Enum.GetValues<CoachToolArgumentMask>())
        {
            ReadTrace(TraceJson(entry => entry["argumentMask"] = (int)mask))
                .Should().NotBeNull($"{mask} is declared");
            checkedValues++;
        }

        foreach (var coverage in Enum.GetValues<CoachScopeCoverage>())
        {
            ReadTrace(TraceJson(entry => entry["coverage"] = coverage.ToString()))
                .Should().NotBeNull($"{coverage} is declared");
            checkedValues++;
        }

        foreach (var definition in Enum.GetValues<CoachScopeDefinition>())
        {
            ReadTrace(TraceJson(entry => entry["definitionCode"] = definition.ToString()))
                .Should().NotBeNull($"{definition} is declared");
            checkedValues++;
        }

        foreach (var reason in Enum.GetValues<CoachScopeWithheldReason>())
        {
            ReadTrace(TraceJson(entry => entry["withheldReason"] = reason.ToString()))
                .Should().NotBeNull($"{reason} is declared");
            checkedValues++;
        }

        checkedValues.Should().BeGreaterThan(
            40, "the sweep must actually have exercised the six vocabularies, not an empty set");
    }

    // ====================================================== unknown values, per enum

    /// <summary>
    /// An unknown value in any of the six enums preserves the answer and drops the trace.
    /// </summary>
    /// <remarks>
    /// One theory rather than six tests, because the guarantee is the same guarantee six times and
    /// splitting it invites somebody to fix five. The numeric cases use an ordinal no member has;
    /// the string cases use a name no member has.
    /// </remarks>
    [Theory]
    [InlineData("outcome", 97)]
    [InlineData("failureKind", 98)]
    [InlineData("argumentMask", 1 << 20)]
    [InlineData("coverage", "SomeFutureCoverage")]
    [InlineData("definitionCode", "SomeFuturePopulation")]
    [InlineData("withheldReason", "SomeFutureReason")]
    public void An_unknown_enum_value_anywhere_in_the_trace_keeps_the_answer_and_drops_the_trace(
        string member, object futureValue)
    {
        var payload = Payload(entry => entry[member] = JsonValue.Create(futureValue));

        var read = CoachConversationService.ReadOutcome(payload, 2);

        read.Should().NotBeNull(
            "the answer is the turn; the trace is a diagnostic and must not be able to destroy it");
        read!.Answer.Should().NotBeNull();
        read.Answer!.TurnId.Should().Be(
            "answer-survives", "an unreadable trace costs the trace and nothing else");
        read.Trace.Should().BeNull(
            "a trace this build cannot read correctly is absent rather than partly invented");
    }

    /// <summary>
    /// An unknown argument-mask bit drops the trace rather than masking itself off.
    /// </summary>
    /// <remarks>
    /// The one case where "preserve the known flags" looks defensible and is not. Keeping
    /// <c>Window</c> and discarding the unknown bit states a presence set narrower than the one
    /// recorded, and this build cannot know whether the unknown bit changes what <c>Window</c>
    /// meant. Simon's "only if provably correct" is not satisfiable, so the section stands down.
    /// </remarks>
    [Fact]
    public void An_unknown_argument_mask_bit_is_not_masked_off_to_salvage_the_known_ones()
    {
        var known = (int)CoachToolArgumentMask.Window | (int)CoachToolArgumentMask.MaxResults;
        var withFutureBit = known | (1 << 21);

        var read = CoachConversationService.ReadOutcome(
            Payload(entry => entry["argumentMask"] = withFutureBit), 2);

        read!.Answer.Should().NotBeNull();
        read.Trace.Should().BeNull(
            "the known bits are not provably still correct beside a bit this build cannot name");
    }

    /// <summary>One unreadable entry condemns the section, not just itself.</summary>
    /// <remarks>
    /// Dropping the bad entry and keeping its neighbours would renumber nothing but would silently
    /// shorten the record of what the turn did, and a trace that is quietly incomplete reads as
    /// authoritative.
    /// </remarks>
    [Fact]
    public void One_unreadable_entry_drops_the_whole_trace_rather_than_shortening_it()
    {
        var root = PayloadNode();
        var calls = root["trace"]!["calls"]!.AsArray();

        var second = JsonNode.Parse(calls[0]!.ToJsonString())!;
        second["ordinal"] = 2;
        second["coverage"] = "SomeFutureCoverage";
        calls.Add(second);

        var read = CoachConversationService.ReadOutcome(root.ToJsonString(), 2);

        read!.Answer.Should().NotBeNull();
        read.Trace.Should().BeNull(
            "a partly-readable trace presented as complete is a worse claim than no trace");
    }

    // ============================================== what must still be unreadable overall

    /// <summary>A structurally malformed payload is still absent altogether.</summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    public void A_structurally_malformed_payload_is_still_unreadable_overall(string payload)
    {
        CoachConversationService.ReadOutcome(payload, 2).Should().BeNull(
            "corruption is not version skew, and reporting a null answer beside a readable trace "
            + "would claim the turn produced nothing");
    }

    /// <summary>A malformed answer is still unreadable overall, trace or no trace.</summary>
    /// <remarks>
    /// The asymmetry that makes section-scoping honest. The trace may degrade; the answer may not.
    /// A row whose answer will not parse is a row this build cannot read, and saying so lets the
    /// client re-read the ledger instead of rendering an empty turn.
    /// </remarks>
    [Fact]
    public void A_malformed_answer_is_still_unreadable_overall_even_with_a_readable_trace()
    {
        var root = PayloadNode();
        root["answer"]!["expiresAtUtc"] = "not-a-timestamp";

        CoachConversationService.ReadOutcome(root.ToJsonString(), 2).Should().BeNull();
    }

    /// <summary>An answer of the wrong JSON shape is unreadable overall.</summary>
    [Fact]
    public void An_answer_that_is_not_an_object_is_unreadable_overall()
    {
        var root = PayloadNode();
        root["answer"] = 7;

        CoachConversationService.ReadOutcome(root.ToJsonString(), 2).Should().BeNull();
    }

    // ============================================ what must keep working exactly as before

    /// <summary>A version-1 row still yields its answer.</summary>
    /// <remarks>
    /// Re-asserted here rather than left to the schema-version file, because the reader was rewritten
    /// and the v1 arm is the one whose loss would be silent: every turn stored before the bump would
    /// read back empty with no error anywhere.
    /// </remarks>
    [Fact]
    public void A_version_one_row_is_untouched_by_section_scoping()
    {
        var legacy = JsonSerializer.Serialize(Answer("legacy-answer"), OutcomeJson);

        var read = CoachConversationService.ReadOutcome(legacy, 1);

        read!.Answer!.TurnId.Should().Be("legacy-answer");
        read.Trace.Should().BeNull();
    }

    /// <summary>A fully-known version-2 row still reads back both halves intact.</summary>
    [Fact]
    public void A_fully_known_version_two_row_still_reads_back_its_whole_trace()
    {
        var read = CoachConversationService.ReadOutcome(Payload(), 2);

        read!.Answer!.TurnId.Should().Be("answer-survives");
        read.Trace.Should().NotBeNull();

        var call = read.Trace!.Calls.Should().ContainSingle().Subject;
        call.Ordinal.Should().Be(1);
        call.ToolName.Should().Be(CoachToolNames.GetPracticeBalance);
        call.Outcome.Should().Be(CoachToolCallOutcome.Succeeded);
        call.ArgumentMask.Should().Be(CoachToolArgumentMask.Window);
        call.Coverage.Should().Be(CoachScopeCoverage.WindowBounded);
        call.DefinitionCode.Should().Be(CoachScopeDefinition.PracticeWindowBalance);
        call.WithheldReason.Should().Be(CoachScopeWithheldReason.BelowMinimumEvidence);
        call.MatchedCount.Should().Be(13);
        call.ReturnedCount.Should().Be(7);
        call.WithheldCount.Should().Be(6);
        call.ElapsedMs.Should().Be(42);
        call.Truncated.Should().BeFalse();
    }

    /// <summary>A version-2 row with no trace section is unchanged.</summary>
    [Fact]
    public void A_version_two_row_with_an_absent_or_null_trace_still_reads_its_answer()
    {
        var absent = JsonSerializer.Serialize(new { answer = Answer("no-trace") }, OutcomeJson);
        var explicitNull = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("null-trace"), null), OutcomeJson);

        CoachConversationService.ReadOutcome(absent, 2)!.Answer!.TurnId.Should().Be("no-trace");
        CoachConversationService.ReadOutcome(absent, 2)!.Trace.Should().BeNull();
        CoachConversationService.ReadOutcome(explicitNull, 2)!.Answer!.TurnId.Should().Be("null-trace");
        CoachConversationService.ReadOutcome(explicitNull, 2)!.Trace.Should().BeNull();
    }

    /// <summary>An unknown schema version is still absent.</summary>
    [Theory]
    [InlineData(0)]
    // Was 3 until W8 made version 3 real. Moved up rather than deleted: the point of the case is
    // that a version beyond what this build writes is absent rather than misparsed, and that point
    // survives every bump — it just has to be re-aimed at one.
    [InlineData(4)]
    [InlineData(null)]
    public void An_unknown_schema_version_is_still_absent(int? version) =>
        CoachConversationService.ReadOutcome(Payload(), version).Should().BeNull();

    // ================================================================== the read is quiet

    /// <summary>
    /// Nothing about an unreadable trace reaches a log, and no raw value survives the read.
    /// </summary>
    /// <remarks>
    /// <c>ReadOutcome</c> is static and takes no logger, which is the structural form of "no
    /// exception logs". Asserted rather than assumed, because adding one would be a one-line change
    /// that puts stored payload fragments into operational logs.
    /// </remarks>
    [Fact]
    public void The_reader_has_no_way_to_log_what_it_could_not_read()
    {
        var method = typeof(CoachConversationService).GetMethod(
            "ReadOutcome", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        method!.GetParameters().Select(p => p.ParameterType)
            .Should().BeEquivalentTo([typeof(string), typeof(int?)],
                "a reader that could log would be a reader that could log a stored payload");
    }

    // ============================================================================ helpers

    private static IReadOnlyList<Type> CoveredEnumTypes()
    {
        var property = typeof(CoachStoredTurnOutcome).Assembly
            .GetType("SentenceStudio.Api.Coach.Persistence.History.CoachTurnTraceIntegrity", throwOnError: true)!
            .GetProperty("CoveredEnumTypes", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (IReadOnlyList<Type>)property.GetValue(null)!;
    }

    /// <summary>The trace section alone, read through the full outcome reader.</summary>
    private static CoachTurnTraceSummary? ReadTrace(JsonNode trace)
    {
        var root = PayloadNode();
        root["trace"] = trace;
        return CoachConversationService.ReadOutcome(root.ToJsonString(), 2)?.Trace;
    }

    /// <summary>A one-entry trace section, with <paramref name="mutate"/> applied to the entry.</summary>
    private static JsonNode TraceJson(Action<JsonObject> mutate)
    {
        var trace = PayloadNode()["trace"]!;
        mutate(trace["calls"]![0]!.AsObject());
        return JsonNode.Parse(trace.ToJsonString())!;
    }

    /// <summary>A complete, wholly-known version-2 payload, with an optional entry mutation.</summary>
    private static string Payload(Action<JsonObject>? mutateEntry = null)
    {
        var root = PayloadNode();
        mutateEntry?.Invoke(root["trace"]!["calls"]![0]!.AsObject());
        return root.ToJsonString();
    }

    /// <summary>
    /// The payload as the current build writes it, reparsed so a test can edit one member.
    /// </summary>
    /// <remarks>
    /// Serialized from the real records rather than hand-written, so a test cannot assert tolerance
    /// against a shape production never produces — and so a member renamed on the record shows up
    /// here as a failing test rather than as a silently ignored JSON property.
    /// </remarks>
    private static JsonObject PayloadNode()
    {
        var stored = new CoachStoredTurnOutcome(Answer("answer-survives"), Trace());
        return JsonNode.Parse(JsonSerializer.Serialize(stored, OutcomeJson))!.AsObject();
    }

    private static CoachTurnTraceSummary Trace() => new(
        [
            new CoachTurnTraceEntry(
                1,
                CoachToolNames.GetPracticeBalance,
                CoachToolCallOutcome.Succeeded,
                null,
                CoachToolArgumentMask.Window,
                42,
                CoachScopeCoverage.WindowBounded,
                CoachScopeDefinition.PracticeWindowBalance,
                CoachScopeWithheldReason.BelowMinimumEvidence,
                13,
                7,
                6,
                false)
        ],
        BudgetUsed: null,
        BudgetLimit: null);

    private static CoachTurnResponse Answer(string marker)
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
                PlanDate = new DateOnly(2026, 8, 14),
                PlanVersion = "v1",
                AppliedConstraints = constraints,
                EstimatedTotalMinutes = 10,
                CompletedCount = 0,
                TotalCount = 3,
                CompletionPercentage = 0
            },
            ExpiresAtUtc = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc)
        };
    }
}
