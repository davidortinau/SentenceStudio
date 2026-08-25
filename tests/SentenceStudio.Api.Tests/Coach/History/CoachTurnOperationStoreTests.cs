using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Durable turn processing: idempotency, the single-writer slot, lease takeover, fencing, and
/// cancellation. These are the properties that keep a crashed or duplicated turn from producing
/// two answers in one transcript.
/// </summary>
public sealed class CoachTurnOperationStoreTests
{
    private static async Task<string> NewConversationAsync(CoachPersistenceHarness harness, CoachDbContext db)
    {
        var store = harness.NewConversationStore(db);
        var created = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());
        return created.Conversation!.Id;
    }

    [Fact]
    public async Task ClaimAsync_GrantsTheSlotAndStartsTheLease()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);

        var result = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        Assert.Equal(CoachTurnClaimOutcome.Claimed, result.Outcome);
        var operation = Assert.IsType<CoachTurnOperationRecord>(result.Operation);
        Assert.Equal(CoachTurnOperationStatus.Running, operation.Status);
        Assert.Equal("worker-a", operation.LeaseOwner);
        Assert.Equal(1, operation.AttemptCount);
        Assert.False(operation.CancelRequested);
        Assert.True(result.FencingVersion > 0);
    }

    [Fact]
    public async Task ClaimAsync_RefusesAnEmptyOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);

        var result = await store.ClaimAsync(CoachHistorySamples.Empty, CoachHistorySamples.Claim(conversationId));

        Assert.Equal(CoachTurnClaimOutcome.NoOwner, result.Outcome);
        Assert.Empty(await db.CoachTurnOperations.ToListAsync());
    }

    [Fact]
    public async Task ClaimAsync_RefusesAConversationOwnedByAnotherLearner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);

        var result = await store.ClaimAsync(CoachHistorySamples.Intruder, CoachHistorySamples.Claim(conversationId));

        Assert.Equal(CoachTurnClaimOutcome.ConversationNotFound, result.Outcome);
        Assert.Empty(await db.CoachTurnOperations.ToListAsync());
    }

    [Fact]
    public async Task ClaimAsync_ReportsInProgressForTheSameKeyAndPayload()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        var retry = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        Assert.Equal(CoachTurnClaimOutcome.InProgress, retry.Outcome);
        Assert.Single(await db.CoachTurnOperations.ToListAsync());
    }

    /// <summary>
    /// The same retry key carrying different content is a client bug or an attack, never a
    /// legitimate retry. It must be detected from the protected digest alone.
    /// </summary>
    [Fact]
    public async Task ClaimAsync_DetectsTheSameKeyWithADifferentPayload()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId, payload: "{\"text\":\"hello\"}"));

        var conflict = await store.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(conversationId, payload: "{\"text\":\"transfer my money\"}"));

        Assert.Equal(CoachTurnClaimOutcome.PayloadConflict, conflict.Outcome);
        Assert.Single(await db.CoachTurnOperations.ToListAsync());
    }

    [Fact]
    public async Task ClaimAsync_ReplaysTheStoredOutcomeForACompletedKey()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));
        await store.CompleteAsync(
            CoachHistorySamples.Owner,
            claim.Operation!.Id,
            "worker-a",
            claim.FencingVersion,
            "{\"answer\":\"done\"}",
            CoachHistorySchema.TurnOutcomeVersion,
            1,
            2);

        var replay = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        Assert.Equal(CoachTurnClaimOutcome.ReplayCompleted, replay.Outcome);
        Assert.Equal("{\"answer\":\"done\"}", replay.StoredOutcome);
        Assert.Equal(CoachHistorySchema.TurnOutcomeVersion, replay.StoredOutcomeSchemaVersion);
        Assert.Equal(1, replay.Operation!.FirstResponseSequence);
        Assert.Equal(2, replay.Operation.LastResponseSequence);
    }

    [Fact]
    public async Task ClaimAsync_ReportsTerminalForAFailedKey()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));
        await store.FailAsync(CoachHistorySamples.Owner, claim.Operation!.Id, "worker-a", claim.FencingVersion, "upstream_timeout");

        var replay = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        Assert.Equal(CoachTurnClaimOutcome.ReplayTerminal, replay.Outcome);
        Assert.Equal("upstream_timeout", replay.Operation!.ErrorCode);
    }

    [Fact]
    public async Task ClaimAsync_HoldsTheSingleWriterSlotAgainstADifferentKey()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId, key: "idem-1"));

        var second = await store.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(conversationId, key: "idem-2", leaseOwner: "worker-b"));

        Assert.Equal(CoachTurnClaimOutcome.ConversationBusy, second.Outcome);
    }

    [Fact]
    public async Task ClaimAsync_AllowsANewTurnOnceThePreviousOneCompleted()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var first = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId, key: "idem-1"));
        await store.CompleteAsync(
            CoachHistorySamples.Owner, first.Operation!.Id, "worker-a", first.FencingVersion,
            "{}", CoachHistorySchema.TurnOutcomeVersion, 1, 1);

        var second = await store.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(conversationId, key: "idem-2", leaseOwner: "worker-b"));

        Assert.Equal(CoachTurnClaimOutcome.Claimed, second.Outcome);
    }

    /// <summary>
    /// The crash path: a worker dies holding the slot. Once its lease expires another worker
    /// takes over, and the fencing token must move so the dead worker cannot come back and write.
    /// </summary>
    [Fact]
    public async Task ClaimAsync_TakesOverAnExpiredLeaseAndAdvancesFencing()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var first = await store.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(conversationId, lease: TimeSpan.FromSeconds(30)));

        harness.Time.Advance(TimeSpan.FromMinutes(5));
        var takeover = await store.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(conversationId, leaseOwner: "worker-b"));

        Assert.Equal(CoachTurnClaimOutcome.Claimed, takeover.Outcome);
        Assert.Equal("worker-b", takeover.Operation!.LeaseOwner);
        Assert.Equal(2, takeover.Operation.AttemptCount);
        Assert.True(takeover.FencingVersion > first.FencingVersion);
    }

    [Fact]
    public async Task CompleteAsync_RejectsASupersededWorker()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var first = await store.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(conversationId, lease: TimeSpan.FromSeconds(30)));
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId, leaseOwner: "worker-b"));

        var stale = await store.CompleteAsync(
            CoachHistorySamples.Owner, first.Operation!.Id, "worker-a", first.FencingVersion,
            "{\"answer\":\"stale\"}", CoachHistorySchema.TurnOutcomeVersion, 1, 1);

        Assert.Equal(CoachTurnFinalizeOutcome.LeaseLost, stale.Outcome);
        var current = await store.GetAsync(CoachHistorySamples.Owner, first.Operation.Id);
        Assert.Equal(CoachTurnOperationStatus.Running, current!.Status);
    }

    [Fact]
    public async Task RenewLeaseAsync_ExtendsTheHoldersLeaseAndRejectsOthers()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));
        var originalExpiry = claim.Operation!.LeaseExpiresAt;

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var renewed = await store.RenewLeaseAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-a", claim.FencingVersion, TimeSpan.FromMinutes(5));
        var impostor = await store.RenewLeaseAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-b", claim.FencingVersion, TimeSpan.FromMinutes(5));

        Assert.Equal(CoachTurnFinalizeOutcome.Success, renewed.Outcome);
        Assert.True(renewed.Operation!.LeaseExpiresAt > originalExpiry);
        Assert.Equal(CoachTurnFinalizeOutcome.LeaseLost, impostor.Outcome);
    }

    [Fact]
    public async Task RequestCancelAsync_FlagsARunningOperationWithoutEndingIt()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        var cancelled = await store.RequestCancelAsync(CoachHistorySamples.Owner, claim.Operation!.Id);

        Assert.Equal(CoachTurnFinalizeOutcome.Success, cancelled.Outcome);
        Assert.True(cancelled.Operation!.CancelRequested);
        Assert.Equal(CoachTurnOperationStatus.Running, cancelled.Operation.Status);
    }

    [Fact]
    public async Task RequestCancelAsync_RefusesAnotherOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        var result = await store.RequestCancelAsync(CoachHistorySamples.Intruder, claim.Operation!.Id);

        Assert.Equal(CoachTurnFinalizeOutcome.NotFound, result.Outcome);
        Assert.False((await store.GetAsync(CoachHistorySamples.Owner, claim.Operation.Id))!.CancelRequested);
    }

    [Fact]
    public async Task CompleteAsync_IsRejectedOnceTerminal()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));
        await store.CompleteAsync(
            CoachHistorySamples.Owner, claim.Operation!.Id, "worker-a", claim.FencingVersion,
            "{}", CoachHistorySchema.TurnOutcomeVersion, 1, 1);

        var again = await store.CompleteAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-a", claim.FencingVersion,
            "{\"answer\":\"second\"}", CoachHistorySchema.TurnOutcomeVersion, 3, 4);

        Assert.Equal(CoachTurnFinalizeOutcome.AlreadyTerminal, again.Outcome);
    }

    [Fact]
    public async Task FailAsync_TruncatesTheErrorCodeToItsBound()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        var failed = await store.FailAsync(
            CoachHistorySamples.Owner, claim.Operation!.Id, "worker-a", claim.FencingVersion,
            new string('e', CoachHistoryLimits.ErrorCodeMaxLength + 40));

        Assert.Equal(CoachTurnFinalizeOutcome.Success, failed.Outcome);
        Assert.Equal(CoachHistoryLimits.ErrorCodeMaxLength, failed.Operation!.ErrorCode!.Length);
    }

    [Fact]
    public async Task ListExpiredAsync_ReturnsOnlyNonTerminalExpiredOperationsForTheOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var store = harness.NewTurnOperationStore(db);

        var mine = (await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("mine"))).Conversation!.Id;
        var theirs = (await conversations.CreateAsync(CoachHistorySamples.Intruder, CoachHistorySamples.CreateConversation("theirs"))).Conversation!.Id;
        var stranded = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(mine, lease: TimeSpan.FromSeconds(30)));
        await store.ClaimAsync(CoachHistorySamples.Intruder, CoachHistorySamples.Claim(theirs, lease: TimeSpan.FromSeconds(30)));

        var finished = (await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("done"))).Conversation!.Id;
        var completed = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(finished, key: "idem-done", lease: TimeSpan.FromSeconds(30)));
        await store.CompleteAsync(
            CoachHistorySamples.Owner, completed.Operation!.Id, "worker-a", completed.FencingVersion,
            "{}", CoachHistorySchema.TurnOutcomeVersion, 1, 1);

        harness.Time.Advance(TimeSpan.FromMinutes(10));
        var expired = await store.ListExpiredAsync(CoachHistorySamples.Owner);

        Assert.Equal(new[] { stranded.Operation!.Id }, expired.Select(o => o.Id));
    }

    [Fact]
    public async Task ListExpiredAsync_RefusesAnEmptyOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId, lease: TimeSpan.FromSeconds(1)));
        harness.Time.Advance(TimeSpan.FromMinutes(10));

        Assert.Empty(await store.ListExpiredAsync(CoachHistorySamples.Empty));
    }

    /// <summary>
    /// The idempotency key is bound to its conversation, so the same client key used on two
    /// conversations must not be mistaken for a retry.
    /// </summary>
    [Fact]
    public async Task ClaimAsync_ScopesTheIdempotencyKeyToItsConversation()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var store = harness.NewTurnOperationStore(db);
        var a = (await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("a"))).Conversation!.Id;
        var b = (await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("b"))).Conversation!.Id;

        var first = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(a, key: "shared"));
        await store.CompleteAsync(
            CoachHistorySamples.Owner, first.Operation!.Id, "worker-a", first.FencingVersion,
            "{}", CoachHistorySchema.TurnOutcomeVersion, 1, 1);

        var second = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(b, key: "shared"));

        Assert.Equal(CoachTurnClaimOutcome.Claimed, second.Outcome);
    }

    [Fact]
    public async Task GetAsync_DoesNotLeakAcrossOwners()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewTurnOperationStore(db);
        var claim = await store.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(conversationId));

        Assert.Null(await store.GetAsync(CoachHistorySamples.Intruder, claim.Operation!.Id));
        Assert.Null(await store.GetAsync(CoachHistorySamples.Empty, claim.Operation.Id));
        Assert.NotNull(await store.GetAsync(CoachHistorySamples.Owner, claim.Operation.Id));
    }
}
