using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The message ledger: sequence integrity, chronological paging, and owner scoping.
/// </summary>
public sealed class CoachMessageStoreTests
{
    private static async Task<string> NewConversationAsync(CoachPersistenceHarness harness, CoachDbContext db)
    {
        var store = harness.NewConversationStore(db);
        var created = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());
        return created.Conversation!.Id;
    }

    [Fact]
    public async Task AppendAsync_AllocatesContiguousSequencesFromOne()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);

        var sequences = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var result = await store.AppendAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText($"turn {i}")));
            Assert.Equal(CoachHistoryStatus.Success, result.Status);
            sequences.Add(result.Message!.Sequence);
        }

        Assert.Equal(new long[] { 1, 2, 3, 4 }, sequences);
    }

    [Fact]
    public async Task AppendAsync_AdvancesTheConversationCounterAndTimestamp()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var created = await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());
        var store = harness.NewMessageStore(db);

        harness.Time.Advance(TimeSpan.FromMinutes(3));
        await store.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(created.Conversation!.Id, CoachHistorySamples.LearnerText()));

        var reread = await conversations.GetAsync(CoachHistorySamples.Owner, created.Conversation.Id);
        Assert.Equal(1, reread.Conversation!.LastSequence);
        Assert.True(reread.Conversation.UpdatedAt > created.Conversation.UpdatedAt);
    }

    /// <summary>
    /// The ledger, not the cached counter, is the authority. If a row exists beyond
    /// <c>LastSequence</c> — the shape a crash between insert and counter update leaves behind —
    /// the next append must step past it rather than collide.
    /// </summary>
    [Fact]
    public async Task AppendAsync_RecoversWhenTheLedgerIsAheadOfTheCounter()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);

        var orphan = new CoachMessage
        {
            Id = "out-of-band",
            UserProfileId = CoachPersistenceSamples.OwnerUserId,
            ConversationId = conversationId,
            Sequence = 5,
            Role = CoachMessageRole.Coach,
            Kind = CoachMessageKind.Text,
            ProtectedPayload = harness.ContentProtector.Protect(
                new CoachProtectionContext(
                    CoachHistorySamples.Owner,
                    CoachProtectedContentKind.MessagePayload,
                    "out-of-band",
                    harness.ContentProtector.CurrentVersion),
                CoachMessagePayloadSerializer.Serialize(CoachHistorySamples.CoachText())),
            ContentSchemaVersion = CoachHistorySchema.MessagePayloadVersion,
            ContentProtectionVersion = harness.ContentProtector.CurrentVersion,
            CreatedAt = harness.Time.GetUtcNow().UtcDateTime
        };
        db.CoachMessages.Add(orphan);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = harness.NewMessageStore(db);
        var result = await store.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText()));

        Assert.Equal(CoachHistoryStatus.Success, result.Status);
        Assert.Equal(6, result.Message!.Sequence);

        var stored = await db.CoachMessages.Select(m => m.Sequence).ToListAsync();
        Assert.Equal(stored.Count, stored.Distinct().Count());
    }

    [Fact]
    public async Task AppendAsync_RefusesAnEmptyOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);

        var result = await store.AppendAsync(
            CoachHistorySamples.Empty,
            CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText()));

        Assert.Equal(CoachHistoryStatus.NoOwner, result.Status);
        Assert.Empty(await db.CoachMessages.ToListAsync());
    }

    [Fact]
    public async Task AppendAsync_RefusesAConversationOwnedByAnotherLearner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);

        var result = await store.AppendAsync(
            CoachHistorySamples.Intruder,
            CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText()));

        Assert.Equal(CoachHistoryStatus.NotFound, result.Status);
        Assert.Empty(await db.CoachMessages.ToListAsync());
    }

    [Fact]
    public async Task AppendAsync_RejectsAPayloadOverTheByteBound()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);

        var oversized = CoachHistorySamples.LearnerText(new string('x', CoachHistoryLimits.TextMaxLength + 1));
        var result = await store.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(conversationId, oversized));

        Assert.Equal(CoachHistoryStatus.InvalidRequest, result.Status);
        Assert.Empty(await db.CoachMessages.ToListAsync());
    }

    [Fact]
    public async Task AppendAsync_RejectsADuplicateMessageId()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);

        var first = await store.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText(), messageId: "fixed-id"));
        var second = await store.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(conversationId, CoachHistorySamples.CoachText(), messageId: "fixed-id"));

        Assert.Equal(CoachHistoryStatus.Success, first.Status);
        Assert.Equal(CoachHistoryStatus.Conflict, second.Status);
        Assert.Single(await db.CoachMessages.ToListAsync());
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsChronologicalOrderWithDecryptedPayloads()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);

        await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText("first")));
        await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.CoachText("second"), CoachMessageRole.Coach));

        var page = await store.GetLatestAsync(CoachHistorySamples.Owner, conversationId);

        Assert.Equal(CoachHistoryStatus.Success, page.Status);
        Assert.Equal(new long[] { 1, 2 }, page.Items.Select(m => m.Sequence));
        Assert.Equal(new[] { "first", "second" }, page.Items.Select(m => m.Payload!.Text));
        Assert.Equal(0, page.UnreadableCount);
        Assert.All(page.Items, m => Assert.True(m.IsReadable));
    }

    [Fact]
    public async Task GetLatestAsync_AnchorsToTheEndAndWalksBackwardWithoutGaps()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);

        for (var i = 1; i <= 7; i++)
        {
            await store.AppendAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText($"m{i}")));
        }

        var latest = await store.GetLatestAsync(CoachHistorySamples.Owner, conversationId, pageSize: 3);
        Assert.Equal(new long[] { 5, 6, 7 }, latest.Items.Select(m => m.Sequence));

        var seen = latest.Items.Select(m => m.Sequence).ToList();
        var cursor = latest.PreviousCursor;
        while (cursor is not null)
        {
            var page = await store.GetBeforeAsync(CoachHistorySamples.Owner, conversationId, cursor, pageSize: 3);
            Assert.Equal(CoachHistoryStatus.Success, page.Status);
            seen.InsertRange(0, page.Items.Select(m => m.Sequence));
            cursor = page.PreviousCursor;
        }

        Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), seen);
    }

    [Fact]
    public async Task GetBeforeAsync_RejectsATamperedCursor()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);
        for (var i = 0; i < 4; i++)
        {
            await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText($"m{i}")));
        }

        var latest = await store.GetLatestAsync(CoachHistorySamples.Owner, conversationId, pageSize: 2);
        var cursor = Assert.IsType<string>(latest.PreviousCursor);
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "BB" : "AA");

        var page = await store.GetBeforeAsync(CoachHistorySamples.Owner, conversationId, tampered, pageSize: 2);

        Assert.Equal(CoachHistoryStatus.InvalidCursor, page.Status);
        Assert.Empty(page.Items);
    }

    /// <summary>
    /// A message cursor is bound to its conversation, so replaying one against a different
    /// conversation the same learner owns must not read across the boundary.
    /// </summary>
    [Fact]
    public async Task GetBeforeAsync_RejectsACursorFromAnotherConversation()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var store = harness.NewMessageStore(db);

        var a = (await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("a"))).Conversation!.Id;
        var b = (await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("b"))).Conversation!.Id;
        for (var i = 0; i < 3; i++)
        {
            await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(a, CoachHistorySamples.LearnerText($"a{i}")));
            await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(b, CoachHistorySamples.LearnerText($"b{i}")));
        }

        var fromA = await store.GetLatestAsync(CoachHistorySamples.Owner, a, pageSize: 1);
        var page = await store.GetBeforeAsync(CoachHistorySamples.Owner, b, fromA.PreviousCursor!, pageSize: 1);

        Assert.Equal(CoachHistoryStatus.InvalidCursor, page.Status);
    }

    [Fact]
    public async Task GetLatestAsync_RefusesAnEmptyOwnerAndAnotherOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);
        await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText()));

        Assert.Equal(CoachHistoryStatus.NoOwner, (await store.GetLatestAsync(CoachHistorySamples.Empty, conversationId)).Status);
        Assert.Equal(CoachHistoryStatus.NotFound, (await store.GetLatestAsync(CoachHistorySamples.Intruder, conversationId)).Status);
    }

    [Fact]
    public async Task GetLatestAsync_ClampsThePageSizeToTheMaximum()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);
        for (var i = 0; i < CoachHistoryLimits.MessagePageMax + 5; i++)
        {
            await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText($"m{i}")));
        }

        var page = await store.GetLatestAsync(CoachHistorySamples.Owner, conversationId, pageSize: 5_000);

        Assert.Equal(CoachHistoryLimits.MessagePageMax, page.Items.Count);
    }

    [Fact]
    public async Task GetRangeAsync_ReturnsExactlyTheRequestedInclusiveSpan()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);
        for (var i = 0; i < 6; i++)
        {
            await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText($"m{i}")));
        }

        var page = await store.GetRangeAsync(CoachHistorySamples.Owner, conversationId, 2, 4);

        Assert.Equal(new long[] { 2, 3, 4 }, page.Items.Select(m => m.Sequence));
    }

    /// <summary>
    /// An unreadable row is still returned so the transcript keeps its shape; silently dropping
    /// it would make a decryption failure look like a turn that never happened.
    /// </summary>
    [Fact]
    public async Task GetLatestAsync_SurfacesUnreadableRowsInsteadOfDroppingThem()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await NewConversationAsync(harness, db);
        var store = harness.NewMessageStore(db);
        await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText("readable")));
        var damaged = await store.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.CoachText("lost")));

        var row = await db.CoachMessages.SingleAsync(m => m.Id == damaged.Message!.Id);
        row.ProtectedPayload = "corrupted-ciphertext";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var page = await store.GetLatestAsync(CoachHistorySamples.Owner, conversationId);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1, page.UnreadableCount);
        Assert.False(page.Items[1].IsReadable);
        Assert.Null(page.Items[1].Payload);
    }
}
