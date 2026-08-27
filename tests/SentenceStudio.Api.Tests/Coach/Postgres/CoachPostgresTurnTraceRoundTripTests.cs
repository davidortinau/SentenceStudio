using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The turn trace through the real protected column, on a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// A protected-column change proven only in memory is not proven. The payload takes a different
/// path here than it does against SQLite: it is protected, written to a <c>text</c> column through
/// Npgsql, read back, and unprotected — and the failure this catches is the one where a payload
/// that grew a nested object round-trips fine through an in-memory provider and truncates,
/// re-encodes, or fails to decrypt against the real one.
/// </para>
/// <para>
/// The schema-version bump is exercised here too, in both directions: a row written at version 1
/// must still yield its answer, because the alternative is a completed conversation silently
/// reading back as empty for every learner who used the coach before this build.
/// </para>
/// </remarks>
public sealed class CoachPostgresTurnTraceRoundTripTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    private CoachPostgresHarness _harness = null!;
    private string _conversationId = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("trace");

        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);
        var created = await conversations.CreateAsync(
            CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        _conversationId = created.Conversation!.Id;
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------ round trip

    /// <summary>
    /// A version-2 outcome survives protect, persist, unprotect and read.
    /// </summary>
    [PostgresFact]
    public async Task A_schema_two_outcome_round_trips_through_the_protected_column()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("turn-with-trace"), Trace()), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-trace-v2", payload, schemaVersion: 2);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored.Should().NotBeNull();
        stored!.IsReadable.Should().BeTrue("the protected payload decrypted");
        stored.SchemaVersion.Should().Be(2);

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read.Should().NotBeNull();
        read!.Answer!.TurnId.Should().Be("turn-with-trace");

        read.Trace.Should().NotBeNull();
        read.Trace!.BudgetUsed.Should().Be(3);
        read.Trace.BudgetLimit.Should().Be(20);

        var call = read.Trace.Calls.Should().ContainSingle().Subject;
        call.Ordinal.Should().Be(1);
        call.ToolName.Should().Be(CoachToolNames.GetPracticeBalance);
        call.Outcome.Should().Be(CoachToolCallOutcome.Succeeded);
        call.Coverage.Should().Be(CoachScopeCoverage.WindowBounded);
        call.DefinitionCode.Should().Be(
            CoachScopeDefinition.PracticeWindowBalance,
            "the foundation member is the one a JSON round trip of the scope would have lost");
        call.WithheldReason.Should().Be(CoachScopeWithheldReason.BelowMinimumEvidence);
        call.MatchedCount.Should().Be(13);
        call.ReturnedCount.Should().Be(7);
        call.WithheldCount.Should().Be(6);
        call.ElapsedMs.Should().Be(42);
    }

    /// <summary>
    /// A version-1 row still yields its answer, with a null trace.
    /// </summary>
    /// <remarks>
    /// Written the way version 1 wrote it — the bare answer at the root — and read by the current
    /// build. This is the regression the bump could have shipped: a reader that compared the stored
    /// version for equality would return null here, and every pre-existing turn would read back as
    /// no answer at all.
    /// </remarks>
    [PostgresFact]
    public async Task A_version_one_row_still_reads_back_its_answer_after_the_bump()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var legacy = JsonSerializer.Serialize(Answer("legacy-turn"), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-trace-v1", legacy, schemaVersion: 1);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored!.SchemaVersion.Should().Be(1);

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read.Should().NotBeNull("a version-1 row is still readable");
        read!.Answer!.TurnId.Should().Be("legacy-turn");
        read.Trace.Should().BeNull();
    }

    /// <summary>
    /// A turn that called no tools stores an outcome with no trace section.
    /// </summary>
    [PostgresFact]
    public async Task A_turn_with_no_tool_calls_stores_no_trace()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("no-tools"), null), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-trace-none", payload, schemaVersion: 2);
        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        var read = CoachConversationService.ReadOutcome(stored!.Payload, stored.SchemaVersion);

        read!.Answer!.TurnId.Should().Be("no-tools");
        read.Trace.Should().BeNull();
    }

    // ------------------------------------------------------------------------- rollback

    /// <summary>
    /// A row written by a later build, read by this one: the answer survives, the trace does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rollback this batch exists for. A forward deployment writes traces naming enum members
    /// this build has never heard of; a rollback then has to read those rows. Before section-scoped
    /// tolerance the read threw inside the trace, <c>ReadOutcome</c> returned null, and every turn
    /// written during the forward window read back as no answer at all — a completed conversation
    /// silently becoming an empty one, with no error anywhere.
    /// </para>
    /// <para>
    /// Exercised through the real protected column rather than in memory, because that is the path
    /// a rollback actually takes: protect, persist as <c>text</c> through Npgsql, read back,
    /// unprotect, parse. The forward payload is built by editing the serialized JSON, which is the
    /// only honest way to produce bytes a later build would have written.
    /// </para>
    /// </remarks>
    [PostgresTheory]
    [InlineData("\"coverage\":\"WindowBounded\"", "\"coverage\":\"SomeFutureCoverage\"")]
    [InlineData("\"definitionCode\":\"PracticeWindowBalance\"", "\"definitionCode\":\"SomeFuturePopulation\"")]
    [InlineData("\"withheldReason\":\"BelowMinimumEvidence\"", "\"withheldReason\":\"SomeFutureReason\"")]
    [InlineData("\"outcome\":1", "\"outcome\":97")]
    [InlineData("\"argumentMask\":1", "\"argumentMask\":1048577")]
    public async Task A_trace_from_a_later_build_keeps_its_answer_through_the_protected_column(
        string current, string future)
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("rollback-answer"), Trace()), OutcomeJson);

        payload.Should().Contain(current, "the fixture must really carry the member being aged forward");
        var forward = payload.Replace(current, future, StringComparison.Ordinal);

        var operationId = await CompleteAsync(
            operations, $"idem-rollback-{future.GetHashCode():x}", forward, schemaVersion: 2);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored.Should().NotBeNull();
        stored!.IsReadable.Should().BeTrue("the row decrypts; only this build's vocabulary is older");

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read.Should().NotBeNull("a diagnostic must not be able to destroy a learner's answer");
        read!.Answer!.TurnId.Should().Be("rollback-answer");
        read.Trace.Should().BeNull("the trace is from a vocabulary this build cannot read correctly");
    }

    /// <summary>
    /// An unregistered tool name never reaches the protected column, even under rollback.
    /// </summary>
    /// <remarks>
    /// The write-boundary half, proven where it matters. The projection collapses a non-member to
    /// the server constant before anything is serialized, so the raw value is absent from the
    /// ciphertext and from the decrypted payload alike — and the call is still recorded as having
    /// happened, with its ordinal.
    /// </remarks>
    [PostgresFact]
    public async Task An_unregistered_tool_name_is_collapsed_before_it_reaches_the_column()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        const string Smuggled = "what is 사과 in English";

        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(new CoachToolCallObservation(
            Smuggled, 1, CoachToolCallOutcome.Succeeded, null, CoachToolArgumentMask.Query, 9, null));

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("collapsed"), CoachTurnTraceProjection.Project(buffer)),
            OutcomeJson);

        payload.Should().NotContain("사과", "the raw input never gets as far as the payload");

        var operationId = await CompleteAsync(operations, "idem-collapsed", payload, schemaVersion: 2);
        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        var read = CoachConversationService.ReadOutcome(stored!.Payload, stored.SchemaVersion);

        read!.Answer!.TurnId.Should().Be("collapsed");
        var call = read.Trace!.Calls.Should().ContainSingle().Subject;
        call.ToolName.Should().Be(CoachToolNames.Unregistered);
        call.Ordinal.Should().Be(1, "the entry and its ordinal are retained");
    }

    /// <summary>
    /// A W4 trace stored through the real column reads back with a null budget pair.
    /// </summary>
    /// <remarks>
    /// The amendment, proven end to end. A round trip is where an unrecorded budget would most
    /// plausibly become a zero, and a zero here would read as "the turn ran comfortably inside a
    /// cap" on a turn where nothing was ever measured.
    /// </remarks>
    [PostgresFact]
    public async Task A_stored_w4_trace_reads_back_a_null_budget_pair()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var buffer = new CoachTurnObservationBuffer();
        buffer.Add(new CoachToolCallObservation(
            CoachToolNames.GetSkillList, 1, CoachToolCallOutcome.Succeeded, null,
            CoachToolArgumentMask.None, 4, null));

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("null-budget"), CoachTurnTraceProjection.Project(buffer)),
            OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-null-budget", payload, schemaVersion: 2);
        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        var read = CoachConversationService.ReadOutcome(stored!.Payload, stored.SchemaVersion);

        read!.Trace.Should().NotBeNull();
        read.Trace!.Calls.Should().ContainSingle("the trace is non-empty, so the null pair is not vacuous");
        read.Trace.BudgetUsed.Should().BeNull();
        read.Trace.BudgetLimit.Should().BeNull();
    }

    /// <summary>
    /// An observed turn that called nothing stores an empty trace, and it reads back as empty
    /// rather than as absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The storage half of the trace-conflation fix, proven where it actually lands. The projection
    /// now distinguishes "no buffer" from "a buffer that recorded zero calls", and this asserts the
    /// distinction survives protect, persist, unprotect and the section-scoped read — because a
    /// distinction that only holds in memory would leave stored history unable to answer the
    /// question the grounding rules now answer.
    /// </para>
    /// <para>
    /// No schema change is involved and none is needed: <c>Trace</c> was already nullable and an
    /// empty <c>Calls</c> list was always well-formed. What changed is which of the two a zero-call
    /// turn writes.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task An_observed_turn_that_called_nothing_stores_an_empty_trace_not_an_absent_one()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        // Present, and idle. Nothing is seeded, and nothing is fabricated to stand in for it.
        var idle = new CoachTurnObservationBuffer();

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("idle-turn"), CoachTurnTraceProjection.Project(idle)),
            OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-trace-idle", payload, schemaVersion: 2);
        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored!.IsReadable.Should().BeTrue();

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read!.Answer!.TurnId.Should().Be("idle-turn");
        read.Trace.Should().NotBeNull(
            "the stored row records positively that this turn read nothing; a null here would be "
            + "indistinguishable from a turn nobody observed");
        read.Trace!.Calls.Should().BeEmpty();
        read.Trace.BudgetUsed.Should().BeNull("W4 stores a null budget pair and none is invented");
        read.Trace.BudgetLimit.Should().BeNull();
    }

    /// <summary>
    /// A row written with no trace section still reads back with none.
    /// </summary>
    /// <remarks>
    /// The old-row half. Every turn stored before the projection changed carries a null trace, and
    /// those rows must keep meaning "nothing was observed" rather than being re-read as "nothing was
    /// called". The reader is unchanged; this pins that it stayed unchanged.
    /// </remarks>
    [PostgresFact]
    public async Task A_row_stored_before_the_split_still_reads_back_an_absent_trace()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("pre-split"), null), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-trace-presplit", payload, schemaVersion: 2);
        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        var read = CoachConversationService.ReadOutcome(stored!.Payload, stored.SchemaVersion);

        read!.Answer!.TurnId.Should().Be("pre-split");
        read.Trace.Should().BeNull("unobserved stays unobserved, and no reader invents an empty turn");
    }

    // ------------------------------------------------------------------------- protection

    /// <summary>
    /// The trace is not readable in the database, only through the protector.
    /// </summary>
    /// <remarks>
    /// Read with raw SQL, bypassing the store entirely. The tool name is a server constant and the
    /// least sensitive thing in the trace, which is exactly why it is the right probe: if the most
    /// innocuous token is absent from the column, nothing else in the payload is present either.
    /// </remarks>
    [PostgresFact]
    public async Task The_stored_trace_is_ciphertext_in_the_column()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("protected-turn"), Trace()), OutcomeJson);

        await CompleteAsync(operations, "idem-trace-protected", payload, schemaVersion: 2);

        var columns = await _harness.StringsAsync(
            "SELECT \"ProtectedOutcome\" FROM \"CoachTurnOperation\" WHERE \"ProtectedOutcome\" IS NOT NULL");

        columns.Should().ContainSingle();

        var ciphertext = columns[0];
        ciphertext.Should().NotBeNullOrWhiteSpace();
        ciphertext.Should().NotContain(CoachToolNames.GetPracticeBalance);
        ciphertext.Should().NotContain("PracticeWindowBalance");
        ciphertext.Should().NotContain("protected-turn");
        ciphertext.Should().NotContain("budgetUsed");
    }

    /// <summary>
    /// A payload written under one key ring is unreadable under another, and says so.
    /// </summary>
    /// <remarks>
    /// The failure mode a trace must degrade into rather than throw: a restored backup with no key
    /// vault reads the row, cannot decrypt it, and reports an unreadable outcome. The learner's
    /// messages are still in the ledger, so the client re-reads the conversation.
    /// </remarks>
    [PostgresFact]
    public async Task An_outcome_written_under_another_key_ring_is_reported_unreadable()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("rotated"), Trace()), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-trace-rotated", payload, schemaVersion: 2);

        // The same rows, a different key ring — a restored backup without its vault.
        var rotated = _harness.WithDataProtection(
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

        await using var rotatedDb = rotated.NewContext();
        var stored = await rotated.NewTurnOperationStore(rotatedDb)
            .GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored.Should().NotBeNull("the row is still there");
        stored!.IsReadable.Should().BeFalse("but it cannot be decrypted, and says so rather than throwing");

        CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion).Should().BeNull();
    }

    // ---------------------------------------------------------------------------- helpers

    private async Task<string> CompleteAsync(
        ICoachTurnOperationStore operations, string key, string payload, int schemaVersion)
    {
        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Owner, CoachHistorySamples.Claim(_conversationId, key: key));

        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var complete = await operations.CompleteAsync(
            CoachHistorySamples.Owner,
            claim.Operation!.Id,
            "worker-a",
            claim.FencingVersion,
            outcomePayload: payload,
            outcomeSchemaVersion: schemaVersion,
            firstResponseSequence: 1,
            lastResponseSequence: 2);

        complete.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);
        return claim.Operation.Id;
    }

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

    /// <summary>A trace projected from a real observation, not hand-built.</summary>
    /// <remarks>
    /// Going through the projection means this test exercises the same reduction production uses,
    /// so a projection that started carrying something it should not would fail the ciphertext
    /// probe above rather than passing a hand-written fixture.
    /// </remarks>
    private static CoachTurnTraceSummary Trace()
    {
        var buffer = new CoachTurnObservationBuffer();

        buffer.Add(new CoachToolCallObservation(
            CoachToolNames.GetPracticeBalance,
            1,
            CoachToolCallOutcome.Succeeded,
            null,
            CoachToolArgumentMask.Window,
            42,
            new CoachResultScope
            {
                Coverage = CoachScopeCoverage.WindowBounded,
                Order = CoachScopeOrder.MinutesDescending,
                OrderHonored = true,
                Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.DateWindow,
                AsOfUtc = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc),
                WindowStartDate = new DateOnly(2026, 8, 8),
                WindowEndDate = new DateOnly(2026, 8, 14),
                ReturnedCount = 7,
                MatchedCount = 13,
                WithheldCount = 6,
                WithheldReason = CoachScopeWithheldReason.BelowMinimumEvidence,
                DefinitionCode = CoachScopeDefinition.PracticeWindowBalance
            },
            CoachToolSubjectCode.ForPreferenceSetting("session_minutes")));

        buffer.RecordBudget(used: 3, limit: 20);

        return CoachTurnTraceProjection.Project(buffer)!;
    }
}
