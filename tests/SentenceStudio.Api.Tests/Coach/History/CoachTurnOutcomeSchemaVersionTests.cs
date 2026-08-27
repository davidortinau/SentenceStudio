using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The outcome schema bump, and the compatibility it has to keep.
/// </summary>
/// <remarks>
/// <para>
/// Version 1 stored the answer at the root of the payload; version 2 wraps it so the trace can sit
/// beside it. The dangerous half of that change is not the new shape — it is the reader. Before the
/// bump the reader compared the stored version for equality and returned null on anything else, so
/// bumping the constant alone would have made every turn stored by an earlier build read back as
/// no answer at all: a completed conversation silently becoming an empty one, with no error
/// anywhere.
/// </para>
/// <para>
/// So both versions are asserted readable, and the version-1 case asserts the answer survives —
/// not merely that the trace is null.
/// </para>
/// </remarks>
public sealed class CoachTurnOutcomeSchemaVersionTests
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A minimal but complete answer. The turn id carries the marker each case asserts on, because
    /// it is a required member and therefore cannot be forgotten.
    /// </summary>
    private static CoachTurnResponse Answer(string marker = "turn-1")
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
        BudgetUsed: 3,
        BudgetLimit: 20);

    /// <summary>The current version is 2, and the reader still knows version 1.</summary>
    [Fact]
    public void The_current_outcome_version_is_two()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), Trace()), OutcomeJson);

        CoachConversationService.ReadOutcome(payload, 2).Should().NotBeNull();
    }

    /// <summary>
    /// A version-1 row reads back its answer, with a null trace and no exception.
    /// </summary>
    /// <remarks>
    /// The payload here is written exactly as version 1 wrote it — the bare answer at the root —
    /// so this is the real legacy shape rather than a v2 payload with the trace omitted.
    /// </remarks>
    [Fact]
    public void A_version_one_row_reads_back_its_answer_with_a_null_trace()
    {
        var legacy = JsonSerializer.Serialize(Answer("Stored under version one."), OutcomeJson);

        var read = CoachConversationService.ReadOutcome(legacy, 1);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull();
        read.Answer!.TurnId.Should().Be(
            "Stored under version one.",
            "bumping the version must not empty a turn an earlier build completed");
        read.Trace.Should().BeNull();
    }

    /// <summary>A version-2 row reads back both halves.</summary>
    [Fact]
    public void A_version_two_row_reads_back_its_answer_and_its_trace()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("Stored under version two."), Trace()), OutcomeJson);

        var read = CoachConversationService.ReadOutcome(payload, 2);

        read.Should().NotBeNull();
        read!.Answer!.TurnId.Should().Be("Stored under version two.");

        read.Trace.Should().NotBeNull();
        read.Trace!.BudgetUsed.Should().Be(3);
        read.Trace.BudgetLimit.Should().Be(20);

        var call = read.Trace.Calls.Should().ContainSingle().Subject;
        call.ToolName.Should().Be(CoachToolNames.GetPracticeBalance);
        call.Coverage.Should().Be(CoachScopeCoverage.WindowBounded);
        call.DefinitionCode.Should().Be(CoachScopeDefinition.PracticeWindowBalance);
        call.WithheldReason.Should().Be(CoachScopeWithheldReason.BelowMinimumEvidence);
        call.MatchedCount.Should().Be(13);
        call.WithheldCount.Should().Be(6);
        call.ElapsedMs.Should().Be(42);
    }

    /// <summary>A version-2 row whose turn used no tools reads back a null trace.</summary>
    [Fact]
    public void A_version_two_row_with_no_tool_calls_reads_back_a_null_trace()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null), OutcomeJson);

        var read = CoachConversationService.ReadOutcome(payload, 2);

        read!.Answer.Should().NotBeNull();
        read.Trace.Should().BeNull();
    }

    /// <summary>A version this build does not know is treated as absent, not misparsed.</summary>
    [Theory]
    [InlineData(0)]
    // Was 3 until W8 made version 3 real. Moved up rather than deleted: the point of the case is
    // that a version beyond what this build writes is absent rather than misparsed, and that point
    // survives every bump — it just has to be re-aimed at one.
    [InlineData(4)]
    [InlineData(99)]
    [InlineData(null)]
    public void An_unknown_version_reads_back_as_absent(int? version)
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), Trace()), OutcomeJson);

        CoachConversationService.ReadOutcome(payload, version).Should().BeNull();
    }

    /// <summary>A corrupt payload is absent rather than an exception.</summary>
    /// <remarks>
    /// The ledger still holds the messages, so the client re-reads the conversation instead of
    /// seeing a 500. Asserted for both versions, because the v1 arm is a second parse path and a
    /// second chance to throw.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_corrupt_payload_reads_back_as_absent(int version)
    {
        CoachConversationService.ReadOutcome("{ this is not json", version).Should().BeNull();
    }

    [Fact]
    public void A_null_payload_reads_back_as_absent()
    {
        CoachConversationService.ReadOutcome(null, 2).Should().BeNull();
    }

    /// <summary>
    /// The serialized trace carries no learner content, whatever it was projected from.
    /// </summary>
    /// <remarks>
    /// The end-to-end form of the shape rule: the observation this comes from held a
    /// <c>CoachResultScope</c> and a subject code, and neither may appear in the bytes that reach
    /// the protected column.
    /// </remarks>
    [Fact]
    public void The_serialized_trace_carries_no_scope_and_no_subject_code()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(new CoachToolCallObservation(
            CoachToolNames.ListUserVocabularies,
            1,
            CoachToolCallOutcome.Succeeded,
            null,
            CoachToolArgumentMask.Query,
            5,
            new CoachResultScope
            {
                Coverage = CoachScopeCoverage.CompleteOwnedSet,
                Order = CoachScopeOrder.MasteryDescending,
                OrderHonored = true,
                Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.ExcludeDue,
                AsOfUtc = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc),
                ReturnedCount = 1,
                MatchedCount = 1,
                DefinitionCode = CoachScopeDefinition.UndueVocabularySearch
            },
            CoachToolSubjectCode.ForPreferenceSetting("session_minutes")));

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), CoachTurnTraceProjection.Project(buffer)),
            OutcomeJson);

        payload.Should().NotContain("asOfUtc", "the scope's instant is not part of the trace");
        payload.Should().NotContain("filters");
        payload.Should().NotContain("orderHonored");
        payload.Should().NotContain("session_minutes");
        payload.Should().NotContain("subjectCode");

        // What it does carry, so the absence above is not simply an empty trace.
        payload.Should().Contain("list_user_vocabularies");
        payload.Should().Contain("CompleteOwnedSet");
        payload.Should().Contain("UndueVocabularySearch");
    }
}
