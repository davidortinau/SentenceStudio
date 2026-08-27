using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The dispute through the real protected column, on a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// A protected-column change proven only in memory is not proven. The payload takes a different
/// path here: protected, written to a <c>text</c> column through Npgsql, read back, unprotected.
/// The failure this catches is the one where a payload that grew a third nested object round-trips
/// fine against an in-memory provider and truncates, re-encodes, or fails to decrypt against the
/// real one.
/// </para>
/// <para>
/// <b>And the version bump is exercised in both directions.</b> A dispute is state the next turn is
/// judged against, so a dispute that does not survive persistence is a dispute the coach forgets —
/// the learner corrects it, the app restarts or the session resumes, and the correction is gone.
/// That is the same experience as never having been heard, which is the defect W8 exists to close.
/// </para>
/// </remarks>
public sealed class CoachPostgresDisputeRoundTripTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    private const string DisputedMessageId = "3f1c9a44-0d3e-4c1b-9a5e-77b2c1d0e912";

    private CoachPostgresHarness _harness = null!;
    private string _conversationId = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("dispute");

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

    /// <summary>An open dispute survives protect, persist, unprotect and read.</summary>
    [PostgresFact]
    public async Task An_open_dispute_round_trips_through_the_protected_column()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var dispute = OpenDispute();

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("turn-with-dispute"), null, dispute), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-dispute-open", payload, schemaVersion: 3);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored.Should().NotBeNull();
        stored!.IsReadable.Should().BeTrue("the protected payload decrypted");
        stored.SchemaVersion.Should().Be(3);

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read.Should().NotBeNull();
        read!.Answer!.TurnId.Should().Be("turn-with-dispute");

        read.Dispute.Should().NotBeNull(
            "a dispute that does not survive persistence is a dispute the coach forgets, which is "
            + "the same experience as never having been heard");

        read.Dispute!.Signal.Should().Be(CoachCorrectionSignal.DifferentCohort);
        read.Dispute.Resolution.Should().Be(CoachDisputeResolution.Open);
        read.Dispute.IsOpen.Should().BeTrue();
        read.Dispute.DisputedMessageId.Should().Be(DisputedMessageId);
        read.Dispute.OpenedAtUtc.Should().Be(new DateTime(2026, 8, 22, 2, 5, 0, DateTimeKind.Utc));
        read.Dispute.DisputedDefinitionCodes.Should().Equal(
            [CoachScopeDefinition.TrackedVocabularyDueSummary],
            "the definitions are what the next turn is compared against, so losing them silently "
            + "turns every subsequent answer into an acceptable re-read");
    }

    /// <summary>A resolved dispute keeps both timestamps and its resolution.</summary>
    [PostgresFact]
    public async Task A_resolved_dispute_round_trips_with_its_resolution()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var resolved = OpenDispute() with
        {
            Resolution = CoachDisputeResolution.ResolvedByReRead,
            ResolvedAtUtc = new DateTime(2026, 8, 22, 2, 9, 0, DateTimeKind.Utc)
        };

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("turn-resolved"), null, resolved), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-dispute-resolved", payload, schemaVersion: 3);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);
        var read = CoachConversationService.ReadOutcome(stored!.Payload, stored.SchemaVersion);

        read!.Dispute!.Resolution.Should().Be(CoachDisputeResolution.ResolvedByReRead);
        read.Dispute.IsOpen.Should().BeFalse();
        read.Dispute.ResolvedAtUtc.Should().Be(new DateTime(2026, 8, 22, 2, 9, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// A version-2 row still reads back its answer after the bump to 3.
    /// </summary>
    /// <remarks>
    /// The regression the bump could have shipped. A reader that compared the stored version for
    /// equality would return null here, and every turn stored between W4 and W8 would read back as
    /// no answer at all — the learner's completed conversation silently emptying.
    /// </remarks>
    [PostgresFact]
    public async Task A_version_two_row_still_reads_back_after_the_bump()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("pre-dispute-turn"), null), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-dispute-v2", payload, schemaVersion: 2);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored!.SchemaVersion.Should().Be(2);

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull("a v2 row must survive the bump to v3 intact");
        read.Answer!.TurnId.Should().Be("pre-dispute-turn");
        read.Dispute.Should().BeNull("a v2 payload has no dispute section, and absent means null");
    }

    /// <summary>A version-1 row still reads back too. The oldest arm, still load-bearing.</summary>
    [PostgresFact]
    public async Task A_version_one_row_still_reads_back_after_the_bump()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var legacy = JsonSerializer.Serialize(Answer("legacy-turn"), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-dispute-v1", legacy, schemaVersion: 1);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);
        var read = CoachConversationService.ReadOutcome(stored!.Payload, stored.SchemaVersion);

        read!.Answer!.TurnId.Should().Be("legacy-turn");
        read.Trace.Should().BeNull();
        read.Dispute.Should().BeNull();
    }

    /// <summary>
    /// The persisted payload holds no learner text.
    /// </summary>
    /// <remarks>
    /// Read back out of the real column and searched. The dispute was opened from a real correction
    /// sentence through the real classifier, so if any part of that sentence could reach the
    /// protected outcome, it would be in this string.
    /// </remarks>
    [PostgresFact]
    public async Task The_persisted_payload_contains_no_correction_text()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        const string Correction = "No, I meant the words I looked up, not the ones in the plan.";

        var coordinator = new CoachDisputeCoordinator(
            new CoachCorrectionClassifier(),
            new Claims.StaticOptionsMonitor<Api.Coach.Runtime.CoachOptions>(
                new Api.Coach.Runtime.CoachOptions
                {
                    CorrectionState = new Api.Coach.Runtime.CoachFeatureSwitch { Enabled = true }
                }));

        var dispute = coordinator.TryOpen(
            Correction,
            DisputedMessageId,
            null,
            new DateTime(2026, 8, 22, 2, 5, 0, DateTimeKind.Utc));

        dispute.Should().NotBeNull("the fixture is vacuous if no dispute was opened");

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer("turn-privacy"), null, dispute), OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-dispute-privacy", payload, schemaVersion: 3);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored!.Payload.Should().NotBeNull();
        stored.Payload!.Should().NotContain(
            "looked up",
            "the learner's correction lives in the encrypted message ledger, once. A second copy "
            + "here would carry a second retention story and a second erasure path");
        stored.Payload.Should().NotContain("I meant");
        stored.Payload.Should().Contain(
            "DifferentCohort",
            "the closed signal is what is stored, and it is what the next turn is judged against");
    }

    private static CoachTurnDisputeState OpenDispute() => new(
        CoachCorrectionSignal.DifferentCohort,
        DisputedMessageId,
        new DateTime(2026, 8, 22, 2, 5, 0, DateTimeKind.Utc),
        ResolvedAtUtc: null,
        CoachDisputeResolution.Open,
        [CoachScopeDefinition.TrackedVocabularyDueSummary]);

    // ─────────────────────────────────────────────────────────────────────────
    // The bounded load, and what it must never carry across.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The load finds the open dispute a resumed session has to honour.</summary>
    [PostgresFact]
    public async Task A_resumed_conversation_finds_its_open_dispute()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(
            operations,
            "idem-resume",
            JsonSerializer.Serialize(
                new CoachStoredTurnOutcome(Answer("resumed"), null, OpenDispute()), OutcomeJson),
            schemaVersion: 3);

        var recent = await operations.GetRecentOutcomesAsync(
            CoachHistorySamples.Owner, _conversationId, limit: 3);

        recent.Should().NotBeEmpty("the resume path reads the protected outcomes, not a new table");

        var open = recent
            .Select(outcome => CoachConversationService.ReadOutcome(outcome.Payload, outcome.SchemaVersion))
            .FirstOrDefault(stored => stored?.Dispute is { IsOpen: true });

        open.Should().NotBeNull(
            "a learner who closes the app mid-correction must come back to the same constraint");
        open!.Dispute!.DisputedMessageId.Should().Be(DisputedMessageId);
    }

    /// <summary>A second conversation belonging to the same learner sees nothing.</summary>
    [PostgresFact]
    public async Task A_dispute_does_not_carry_between_two_conversations_of_one_learner()
    {
        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(
            operations,
            "idem-conversation-a",
            JsonSerializer.Serialize(
                new CoachStoredTurnOutcome(Answer("thread-a"), null, OpenDispute()), OutcomeJson),
            schemaVersion: 3);

        var second = await conversations.CreateAsync(
            CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        var other = await operations.GetRecentOutcomesAsync(
            CoachHistorySamples.Owner, second.Conversation!.Id, limit: 3);

        other.Should().BeEmpty(
            "disagreeing in one thread must not constrain the answer in another; a learner asking "
            + "an unrelated question elsewhere has corrected nothing");
    }

    /// <summary>Another learner sees nothing, whatever conversation id they name.</summary>
    [PostgresFact]
    public async Task A_dispute_does_not_carry_between_two_learners()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(
            operations,
            "idem-owner-scope",
            JsonSerializer.Serialize(
                new CoachStoredTurnOutcome(Answer("mine"), null, OpenDispute()), OutcomeJson),
            schemaVersion: 3);

        // The same conversation id, the wrong owner. Owner scoping is the query's own owned set,
        // so this is not a filter the caller could have forgotten to apply.
        var intruder = await operations.GetRecentOutcomesAsync(
            CoachHistorySamples.Intruder, _conversationId, limit: 3);

        intruder.Should().BeEmpty();

        var mine = await operations.GetRecentOutcomesAsync(
            CoachHistorySamples.Owner, _conversationId, limit: 3);

        mine.Should().NotBeEmpty("the owner still reads their own, so the check is not vacuous");
    }

    /// <summary>An empty owner reads nothing rather than everything.</summary>
    [PostgresFact]
    public async Task An_unscoped_read_returns_nothing()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(
            operations,
            "idem-unscoped",
            JsonSerializer.Serialize(
                new CoachStoredTurnOutcome(Answer("scoped"), null, OpenDispute()), OutcomeJson),
            schemaVersion: 3);

        var results = new[]
        {
            await operations.GetRecentOutcomesAsync(default, _conversationId, limit: 3),
            await operations.GetRecentOutcomesAsync(CoachHistorySamples.Owner, "", limit: 3),
            await operations.GetRecentOutcomesAsync(CoachHistorySamples.Owner, _conversationId, limit: 0)
        };

        results.Should().OnlyContain(r => r.Count == 0, "every degenerate input fails closed");
    }

    /// <summary>The lookback is clamped however much a caller asks for.</summary>
    [PostgresFact]
    public async Task The_lookback_is_clamped()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        for (var i = 0; i < 3; i++)
        {
            await CompleteAsync(
                operations,
                $"idem-clamp-{i}",
                JsonSerializer.Serialize(
                    new CoachStoredTurnOutcome(Answer($"turn-{i}"), null, null), OutcomeJson),
                schemaVersion: 3);
        }

        var asked = await operations.GetRecentOutcomesAsync(
            CoachHistorySamples.Owner, _conversationId, limit: int.MaxValue);

        asked.Count.Should().BeLessThanOrEqualTo(
            CoachTurnOperationStore.MaxRecentOutcomes,
            "an unbounded scan on the front of every turn is the cost this clamp exists to cap");

        asked.Should().NotBeEmpty("and it still returns the recent history it was asked for");
    }

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
