using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Export streaming and the deletion contributor Kaylee's coordinator discovers.
/// </summary>
public sealed class CoachHistoryExportAndDeletionTests
{
    private static async Task<(string ConversationId, string OperationId)> SeedAsync(
        CoachPersistenceHarness harness,
        CoachDbContext db,
        CoachOwner owner,
        string title)
    {
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var turns = harness.NewTurnOperationStore(db);

        var created = await conversations.CreateAsync(owner, CoachHistorySamples.CreateConversation(title));
        var id = created.Conversation!.Id;

        for (var i = 1; i <= 3; i++)
        {
            await messages.AppendAsync(owner, CoachHistorySamples.Append(id, CoachHistorySamples.LearnerText($"{title}-{i}")));
        }

        var claim = await turns.ClaimAsync(owner, CoachHistorySamples.Claim(id, key: $"idem-{title}"));
        return (id, claim.Operation!.Id);
    }

    [Fact]
    public async Task StreamConversationsAsync_ReturnsOnlyTheOwnersActiveConversations()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");
        var hidden = await SeedAsync(harness, db, CoachHistorySamples.Owner, "hidden");
        await SeedAsync(harness, db, CoachHistorySamples.Intruder, "theirs");
        await conversations.SoftDeleteAsync(CoachHistorySamples.Owner, hidden.ConversationId);

        var reader = harness.NewExportReader(db);
        var titles = new List<string?>();
        await foreach (var conversation in reader.StreamConversationsAsync(CoachHistorySamples.Owner))
        {
            titles.Add(conversation.Title);
        }

        Assert.Equal(new[] { "mine" }, titles);
    }

    [Fact]
    public async Task StreamConversationsAsync_YieldsNothingForAnEmptyOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");

        var reader = harness.NewExportReader(db);
        var count = 0;
        await foreach (var _ in reader.StreamConversationsAsync(CoachHistorySamples.Empty))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task StreamMessagesAsync_YieldsChronologicalDecryptedMessages()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var seeded = await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");

        var reader = harness.NewExportReader(db);
        var sequences = new List<long>();
        var texts = new List<string?>();
        await foreach (var message in reader.StreamMessagesAsync(CoachHistorySamples.Owner, seeded.ConversationId))
        {
            sequences.Add(message.Sequence);
            texts.Add(message.Payload?.Text);
        }

        Assert.Equal(new long[] { 1, 2, 3 }, sequences);
        Assert.Equal(new[] { "mine-1", "mine-2", "mine-3" }, texts);
    }

    [Fact]
    public async Task StreamMessagesAsync_YieldsNothingForAnotherOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var seeded = await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");

        var reader = harness.NewExportReader(db);
        var count = 0;
        await foreach (var _ in reader.StreamMessagesAsync(CoachHistorySamples.Intruder, seeded.ConversationId))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteAllAsync_RemovesEveryHistoryRowForTheOwnerOnly()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");
        await SeedAsync(harness, db, CoachHistorySamples.Intruder, "theirs");

        var contributor = harness.NewDeletionContributor(db);
        var removed = await contributor.DeleteAllAsync(CoachHistorySamples.Owner);

        // 1 conversation + 3 messages + 1 operation.
        Assert.Equal(5, removed);
        Assert.Empty(await db.CoachConversations.Where(c => c.UserProfileId == CoachPersistenceSamples.OwnerUserId).ToListAsync());
        Assert.Empty(await db.CoachMessages.Where(m => m.UserProfileId == CoachPersistenceSamples.OwnerUserId).ToListAsync());
        Assert.Empty(await db.CoachTurnOperations.Where(o => o.UserProfileId == CoachPersistenceSamples.OwnerUserId).ToListAsync());

        Assert.Single(await db.CoachConversations.ToListAsync());
        Assert.Equal(3, (await db.CoachMessages.ToListAsync()).Count);
        Assert.Single(await db.CoachTurnOperations.ToListAsync());
    }

    [Fact]
    public async Task DeleteAllAsync_RemovesSoftDeletedConversationsToo()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var seeded = await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");
        await harness.NewConversationStore(db).SoftDeleteAsync(CoachHistorySamples.Owner, seeded.ConversationId);

        var removed = await harness.NewDeletionContributor(db).DeleteAllAsync(CoachHistorySamples.Owner);

        Assert.Equal(5, removed);
        Assert.Empty(await db.CoachConversations.ToListAsync());
    }

    [Fact]
    public async Task DeleteAllAsync_RefusesAnEmptyOwnerRatherThanWipingTheTable()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");

        var removed = await harness.NewDeletionContributor(db).DeleteAllAsync(CoachHistorySamples.Empty);

        Assert.Equal(0, removed);
        Assert.Single(await db.CoachConversations.ToListAsync());
    }

    /// <summary>
    /// The plan revision audit is deliberately outside this contributor's reach: it is the record
    /// of what changed a learner's plan, and deleting conversation history must not erase it.
    /// </summary>
    [Fact]
    public async Task DeleteAllAsync_LeavesTheSessionRevisionAndUsageTablesAlone()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var sessions = harness.NewSessionStore(db);
        var usage = harness.NewUsageStore(db);

        var session = await sessions.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await sessions.AppendRevisionAsync(
            CoachPersistenceSamples.OwnerUserId,
            session.Id,
            CoachPersistenceSamples.RevisionInput());
        await usage.RecordRunAsync(CoachPersistenceSamples.OwnerUserId, new DateOnly(2026, 8, 14), 10, 20, 0.01m);
        await SeedAsync(harness, db, CoachHistorySamples.Owner, "mine");

        await harness.NewDeletionContributor(db).DeleteAllAsync(CoachHistorySamples.Owner);

        Assert.Single(await db.CoachSessions.ToListAsync());
        Assert.Single(await db.CoachPlanRevisions.ToListAsync());
        Assert.Single(await db.CoachUsages.ToListAsync());
    }

    [Fact]
    public void TheContributorAdvertisesAStableName()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        Assert.Equal("CoachConversationHistory", harness.NewDeletionContributor(db).Name);
    }
}
