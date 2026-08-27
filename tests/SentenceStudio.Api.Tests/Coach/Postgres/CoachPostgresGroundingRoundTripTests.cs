using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The grounding section through the real protected column, on a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// A protected-column change proven only in memory is not proven. The payload takes a different
/// path here: protected, written to a <c>text</c> column through Npgsql, read back, unprotected.
/// The failure this catches is the one where a payload that grew a fourth nested section
/// round-trips fine against an in-memory provider and truncates, re-encodes, or fails to decrypt
/// against the real one.
/// </para>
/// <para>
/// The whole point of R0 is that a report filed hours later can read what the honesty layer did.
/// A grounding section that does not survive the protected column is a report that cannot be
/// written, which is the dependency every later W9 stage rests on.
/// </para>
/// </remarks>
public sealed class CoachPostgresGroundingRoundTripTests : IAsyncLifetime
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

        _harness = await CoachPostgresHarness.CreateAsync("grounding");

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

    /// <summary>A full grounding summary survives protect, persist, unprotect and read.</summary>
    [PostgresFact]
    public async Task A_grounding_summary_round_trips_through_the_protected_column()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer("turn-with-grounding"),
                CoachGroundingSectionSchemaTests.Trace(),
                CoachGroundingSectionSchemaTests.Dispute(),
                CoachGroundingSectionSchemaTests.Summary()),
            OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-grounding", payload, schemaVersion: 3);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored.Should().NotBeNull();
        stored!.IsReadable.Should().BeTrue("the protected payload decrypted");
        stored.SchemaVersion.Should().Be(
            3,
            "R0 adds a section, not a version; the stored label must not have moved");

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read.Should().NotBeNull();
        read!.Answer!.TurnId.Should().Be("turn-with-grounding");
        read.Trace.Should().NotBeNull("the sibling sections must survive alongside the new one");
        read.Dispute.Should().NotBeNull();

        var grounding = read.Grounding.Should().NotBeNull().And.Subject as CoachGroundingTurnSummary;

        grounding!.RequestedStage.Should().Be(CoachGroundingStage.Enforce);
        grounding.SubstitutionAllowed.Should().BeFalse();
        grounding.Refused.Should().BeTrue();
        grounding.RepairSuppressedForLanguage.Should().BeTrue();
        grounding.FindingCount.Should().Be(3);
        grounding.LimitationCode.Should().Be(CoachLimitationCode.WouldRemoveLearningValue);
        grounding.ShadowLabel.Should().Be(CoachShadowRouteLabel.LearnerState);

        grounding.RuleCounts.Should().HaveCount(
            2,
            "per-rule counts are the reportable content; a column populated from a summary that "
            + "lost them would report a turn as clean when it was not");
    }

    /// <summary>A row with no grounding still reads, and the section is null.</summary>
    [PostgresFact]
    public async Task A_row_without_grounding_reads_with_a_null_section()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer("turn-section-absent"),
                CoachGroundingSectionSchemaTests.Trace()),
            OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-grounding-absent", payload, schemaVersion: 3);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        // The property KEY, not the bare word: a turn marker containing "grounding" would make a
        // substring assertion fail for a reason that has nothing to do with the section.
        stored!.Payload.Should().NotContain(
            "\"grounding\":",
            "until R2 populates it the property is omitted entirely, so a row this build writes is "
            + "byte-identical to one a pre-W9 build wrote");

        var read = CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion);

        read!.Answer.Should().NotBeNull();
        read.Trace.Should().NotBeNull();
        read.Grounding.Should().BeNull();
    }

    /// <summary>
    /// The persisted payload carries no text, index or identifier from the grounding section.
    /// </summary>
    /// <remarks>
    /// Read back out of the real column and searched. The summary was built from a real rule
    /// vocabulary, so if a span index or an answer fragment could reach the durable record, it
    /// would be in this string.
    /// </remarks>
    [PostgresFact]
    public async Task The_persisted_grounding_section_is_content_free()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer("turn-privacy"),
                null,
                null,
                CoachGroundingSectionSchemaTests.Summary()),
            OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-grounding-privacy", payload, schemaVersion: 3);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored!.Payload.Should().NotBeNull();
        stored.Payload!.Should().Contain(
            "UnverifiedLearnerStateClaim",
            "the closed rule codes are what R0 exists to persist; without them the fixture is vacuous");

        foreach (var forbidden in new[] { "blockIndex", "spanIndex", "languageTag", "userProfileId" })
        {
            stored.Payload.Should().NotContain(
                forbidden,
                "an index into an answer is a pointer at a sentence, and the stored answer sits in "
                + "the same payload");
        }
    }

    /// <summary>Version 4 remains unreadable even with a well-formed grounding section.</summary>
    [PostgresFact]
    public async Task A_version_four_row_is_still_unreadable()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer("turn-v4"),
                null,
                null,
                CoachGroundingSectionSchemaTests.Summary()),
            OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-grounding-v4", payload, schemaVersion: 4);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);

        stored!.SchemaVersion.Should().Be(4);

        CoachConversationService.ReadOutcome(stored.Payload, stored.SchemaVersion).Should().BeNull(
            "R0 does not add a version-4 arm. Nothing writes 4, and a row claiming it was not "
            + "written by this system");
    }

    /// <summary>
    /// A refused turn persists its grounding summary. The rows an operator most wants to read.
    /// </summary>
    /// <remarks>
    /// The summary is captured before the refusal branch in <c>ApplyGroundingAsync</c>, so a turn
    /// the layer refused still records why. Writing it only on the success path would make the
    /// report table blind to every refusal — the exact population the gate is measuring.
    /// </remarks>
    [PostgresFact]
    public async Task A_refused_turn_persists_its_grounding_summary()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var refused = SentenceStudio.Api.Coach.Validation.Claims.CoachGroundingTurnProjection.Project(
            new SentenceStudio.Api.Coach.Validation.Claims.CoachClaimTurnRecord(
                CoachGroundingStage.Enforce,
                [
                    new SentenceStudio.Api.Coach.Validation.Claims.CoachClaimFinding(
                        SentenceStudio.Api.Coach.Validation.Claims.CoachClaimRuleCode.WithheldNotDisclosed,
                        SentenceStudio.Api.Coach.Validation.Claims.CoachClaimRepairAction.Refused)
                ],
                Refused: true,
                AnswerAltered: false,
                ShadowLabel: CoachShadowRouteLabel.LearnerState,
                Limitation: null,
                RepairSuppressedForLanguage: true))!;

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer("turn-refused"), null, null, refused),
            OutcomeJson);

        var operationId = await CompleteAsync(operations, "idem-grounding-refused", payload, schemaVersion: 3);

        var stored = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);
        var read = CoachConversationService.ReadOutcome(stored!.Payload, stored.SchemaVersion);

        read!.Grounding.Should().NotBeNull();
        read.Grounding!.Refused.Should().BeTrue();
        read.Grounding.RepairSuppressedForLanguage.Should().BeTrue(
            "the Korean Enforce shape: refused, and never softened first. Both facts, not one");
        read.Grounding.RequestedStage.Should().Be(
            CoachGroundingStage.Enforce,
            "the rung the deployment asked for, never a collapsed value");
    }

    /// <summary>
    /// A replayed operation returns the same grounding section it stored.
    /// </summary>
    /// <remarks>
    /// The idempotency path. A retry that returned the stored answer without its grounding would
    /// make the report for a retried turn read as though the layer never ran, and retries are
    /// exactly when a turn is under stress.
    /// </remarks>
    [PostgresFact]
    public async Task A_replayed_operation_returns_the_stored_grounding_section()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer("turn-retried"),
                null,
                null,
                CoachGroundingSectionSchemaTests.Summary()),
            OutcomeJson);

        await CompleteAsync(operations, "idem-grounding-retry", payload, schemaVersion: 3);

        // The same idempotency key again: the store replays rather than re-running the turn.
        var replay = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-grounding-retry"));

        replay.Outcome.Should().Be(CoachTurnClaimOutcome.ReplayCompleted);

        var read = CoachConversationService.ReadOutcome(
            replay.StoredOutcome, replay.StoredOutcomeSchemaVersion);

        read.Should().NotBeNull();
        read!.Answer!.TurnId.Should().Be("turn-retried");
        read.Grounding.Should().NotBeNull(
            "a retry that lost the grounding section would make the report for a retried turn read "
            + "as though the layer never ran");
        read.Grounding!.FindingCount.Should().Be(3);
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
}
