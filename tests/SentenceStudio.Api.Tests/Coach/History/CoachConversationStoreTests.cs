using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Conversation metadata: ownership, ordering, cursor integrity, and the delete/purge split.
/// </summary>
public sealed class CoachConversationStoreTests
{
    [Fact]
    public async Task CreateAsync_StoresAnOwnedActiveConversation()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);

        var result = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        Assert.Equal(CoachHistoryStatus.Success, result.Status);
        var conversation = Assert.IsType<CoachConversationRecord>(result.Conversation);
        Assert.Equal("Morning practice", conversation.Title);
        Assert.Equal(CoachConversationStatus.Active, conversation.Status);
        Assert.Equal(CoachConversationTitleSource.Generated, conversation.TitleSource);
        Assert.Equal("ko", conversation.TargetLanguageCode);
        Assert.Equal(0, conversation.LastSequence);
        Assert.NotEmpty(conversation.Id);
    }

    [Fact]
    public async Task CreateAsync_RefusesAnEmptyOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);

        var result = await store.CreateAsync(CoachHistorySamples.Empty, CoachHistorySamples.CreateConversation());

        Assert.Equal(CoachHistoryStatus.NoOwner, result.Status);
        Assert.Null(result.Conversation);
        Assert.Empty(await db.CoachConversations.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_RejectsAnOverlongTitle()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);

        var request = CoachHistorySamples.CreateConversation(new string('x', CoachHistoryLimits.TitleMaxLength + 1));
        var result = await store.CreateAsync(CoachHistorySamples.Owner, request);

        Assert.Equal(CoachHistoryStatus.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task GetAsync_DoesNotLeakAcrossOwners()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        var created = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        var intruder = await store.GetAsync(CoachHistorySamples.Intruder, created.Conversation!.Id);
        var empty = await store.GetAsync(CoachHistorySamples.Empty, created.Conversation.Id);

        Assert.Equal(CoachHistoryStatus.NotFound, intruder.Status);
        Assert.Equal(CoachHistoryStatus.NoOwner, empty.Status);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheCallersConversations()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);

        await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("mine"));
        await store.CreateAsync(CoachHistorySamples.Intruder, CoachHistorySamples.CreateConversation("theirs"));

        var page = await store.ListAsync(CoachHistorySamples.Owner);

        Assert.Equal(CoachHistoryStatus.Success, page.Status);
        Assert.Equal(new[] { "mine" }, page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task ListAsync_RefusesAnEmptyOwnerRatherThanReturningEverything()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        var page = await store.ListAsync(CoachHistorySamples.Empty);

        Assert.Equal(CoachHistoryStatus.NoOwner, page.Status);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ListAsync_OrdersByUpdatedAtDescendingThenIdDescending()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);

        var first = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("first"));
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("second"));

        var page = await store.ListAsync(CoachHistorySamples.Owner);

        Assert.Equal(new[] { second.Conversation!.Id, first.Conversation!.Id }, page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ListAsync_PagesForwardWithoutGapsOrRepeats()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);

        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation($"c{i}"));
            harness.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.ListAsync(CoachHistorySamples.Owner, pageSize: 2, cursor: cursor);
            Assert.Equal(CoachHistoryStatus.Success, page.Status);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task ListAsync_ClampsThePageSizeToTheMaximum()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);

        for (var i = 0; i < CoachHistoryLimits.ConversationPageMax + 3; i++)
        {
            await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation($"c{i}"));
        }

        var page = await store.ListAsync(CoachHistorySamples.Owner, pageSize: 10_000);

        Assert.Equal(CoachHistoryLimits.ConversationPageMax, page.Items.Count);
    }

    [Fact]
    public async Task ListAsync_RejectsATamperedCursorInsteadOfSilentlyRestarting()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("a"));
        await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("b"));

        var page = await store.ListAsync(CoachHistorySamples.Owner, pageSize: 1);
        var cursor = Assert.IsType<string>(page.NextCursor);
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "BB" : "AA");

        var result = await store.ListAsync(CoachHistorySamples.Owner, pageSize: 1, cursor: tampered);

        Assert.Equal(CoachHistoryStatus.InvalidCursor, result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ListAsync_RejectsAnotherOwnersCursor()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("a"));
        await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation("b"));
        await store.CreateAsync(CoachHistorySamples.Intruder, CoachHistorySamples.CreateConversation("x"));
        await store.CreateAsync(CoachHistorySamples.Intruder, CoachHistorySamples.CreateConversation("y"));

        var mine = await store.ListAsync(CoachHistorySamples.Owner, pageSize: 1);
        var stolen = await store.ListAsync(CoachHistorySamples.Intruder, pageSize: 1, cursor: mine.NextCursor);

        Assert.Equal(CoachHistoryStatus.InvalidCursor, stolen.Status);
    }

    [Fact]
    public async Task RenameAsync_MarksTheTitleLearnerAuthoredAndBumpsVersion()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        var created = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        harness.Time.Advance(TimeSpan.FromMinutes(5));
        var renamed = await store.RenameAsync(CoachHistorySamples.Owner, created.Conversation!.Id, "Trip prep");

        Assert.Equal(CoachHistoryStatus.Success, renamed.Status);
        Assert.Equal("Trip prep", renamed.Conversation!.Title);
        Assert.Equal(CoachConversationTitleSource.Learner, renamed.Conversation.TitleSource);
        Assert.True(renamed.Conversation.Version > created.Conversation.Version);
    }

    [Fact]
    public async Task RenameAsync_RefusesAnotherOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        var created = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        var result = await store.RenameAsync(CoachHistorySamples.Intruder, created.Conversation!.Id, "hijacked");

        Assert.Equal(CoachHistoryStatus.NotFound, result.Status);
        var reread = await store.GetAsync(CoachHistorySamples.Owner, created.Conversation.Id);
        Assert.Equal("Morning practice", reread.Conversation!.Title);
    }

    [Fact]
    public async Task SoftDeleteAsync_HidesTheConversationFromEveryReadPath()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        var created = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());

        var status = await store.SoftDeleteAsync(CoachHistorySamples.Owner, created.Conversation!.Id);

        Assert.Equal(CoachHistoryStatus.Success, status);
        Assert.Equal(CoachHistoryStatus.NotFound, (await store.GetAsync(CoachHistorySamples.Owner, created.Conversation.Id)).Status);
        Assert.Empty((await store.ListAsync(CoachHistorySamples.Owner)).Items);

        // The row still exists, waiting for the purge pass.
        Assert.Single(await db.CoachConversations.ToListAsync());
    }

    [Fact]
    public async Task PurgeAsync_RemovesTheConversationAndItsChildren()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var turns = harness.NewTurnOperationStore(db);

        var created = await conversations.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());
        var id = created.Conversation!.Id;
        await messages.AppendAsync(CoachHistorySamples.Owner, CoachHistorySamples.Append(id, CoachHistorySamples.LearnerText()));
        await turns.ClaimAsync(CoachHistorySamples.Owner, CoachHistorySamples.Claim(id));

        await conversations.SoftDeleteAsync(CoachHistorySamples.Owner, id);
        var status = await conversations.PurgeAsync(CoachHistorySamples.Owner, id);

        Assert.Equal(CoachHistoryStatus.Success, status);
        Assert.Empty(await db.CoachConversations.ToListAsync());
        Assert.Empty(await db.CoachMessages.ToListAsync());
        Assert.Empty(await db.CoachTurnOperations.ToListAsync());
    }

    [Fact]
    public async Task PurgeAsync_RefusesAnotherOwner()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var store = harness.NewConversationStore(db);
        var created = await store.CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());
        await store.SoftDeleteAsync(CoachHistorySamples.Owner, created.Conversation!.Id);

        var status = await store.PurgeAsync(CoachHistorySamples.Intruder, created.Conversation.Id);

        Assert.Equal(CoachHistoryStatus.NotFound, status);
        Assert.Single(await db.CoachConversations.ToListAsync());
    }
}
