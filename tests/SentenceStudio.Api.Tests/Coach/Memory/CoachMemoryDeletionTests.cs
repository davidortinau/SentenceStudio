using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Deletion has to actually delete: when a conversation goes, what it produced goes with it, and
/// when an account goes, nothing is left behind.
/// </summary>
public sealed class CoachMemoryDeletionTests
{
    private static async Task<string> SeedAsync(
        CoachMemoryHarness harness,
        ICoachMemoryStore store,
        CoachOwner owner,
        string conversationId,
        CoachMemoryStoredValue? value = null,
        bool approve = false,
        string evidence = "please keep explanations concise")
    {
        harness.Time.Advance(TimeSpan.FromMinutes(1));

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(
            value: value ?? CoachMemorySamples.Depth(),
            conversationId: conversationId,
            evidence: evidence,
            message: $"For future sessions {evidence}."));

        candidate.Status.Should().Be(CoachMemoryStatusCode.Success);

        if (!approve)
        {
            return candidate.Fact!.Id;
        }

        var approved = await store.ApproveAsync(owner, candidate.Fact!.Id, candidate.Fact.Version);
        approved.Status.Should().Be(CoachMemoryStatusCode.Success);
        return approved.Fact!.Id;
    }

    // ---------------------------------------------------------------- source conversation

    [Fact]
    public async Task DeletingAConversationRemovesBothItsCandidatesAndItsActiveFacts()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        await SeedAsync(harness, store, owner, "conv-doomed", approve: true);
        await SeedAsync(
            harness,
            store,
            owner,
            "conv-doomed",
            CoachMemorySamples.Register(),
            evidence: "keep examples casual please");

        var handler = harness.NewSourceDeletionHandler(store);
        var deleted = await handler.OnConversationDeletedAsync(owner, "conv-doomed");

        deleted.Should().Be(2);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeletingAConversationLeavesOtherConversationsFactsAlone()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        await SeedAsync(harness, store, owner, "conv-doomed", approve: true);
        await SeedAsync(
            harness,
            store,
            owner,
            "conv-kept",
            CoachMemorySamples.Register(),
            approve: true,
            evidence: "keep examples casual please");

        var deleted = await harness.NewSourceDeletionHandler(store)
            .OnConversationDeletedAsync(owner, "conv-doomed");

        deleted.Should().Be(1);

        var remaining = await store.ListAsync(owner, CoachMemoryListFilter.All);
        remaining.Items.Should().ContainSingle();
        remaining.Items[0].Kind.Should().Be(CoachMemoryKind.ExampleRegister);
    }

    [Fact]
    public async Task DeletingAConversationCannotReachAnotherOwnersFacts()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        // Same conversation id in two accounts. Owner scoping, not the id, decides what goes.
        await SeedAsync(harness, store, CoachMemorySamples.Owner(), "conv-shared", approve: true);
        await SeedAsync(harness, store, CoachMemorySamples.Other(), "conv-shared", approve: true);

        var deleted = await harness.NewSourceDeletionHandler(store)
            .OnConversationDeletedAsync(CoachMemorySamples.Owner(), "conv-shared");

        deleted.Should().Be(1);
        (await db.CoachMemoryFacts.CountAsync(f => f.UserProfileId == CoachMemorySamples.OtherUserId))
            .Should().Be(1);
    }

    [Fact]
    public async Task DeletingAConversationTellsTheCheckpointOwner()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        await SeedAsync(harness, store, owner, "conv-doomed", approve: true);
        harness.Notifier.Clear();

        await harness.NewSourceDeletionHandler(store).OnConversationDeletedAsync(owner, "conv-doomed");

        // Without this signal a forgotten preference survives inside an already serialized session.
        harness.Notifier.Changes.Should().Contain(c => c.Change == CoachMemoryChangeKind.SourceDeleted);
    }

    [Fact]
    public async Task SourceDeletionRefusesAnEmptyOwner()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        await SeedAsync(harness, store, CoachMemorySamples.Owner(), "conv-1", approve: true);

        var deleted = await harness.NewSourceDeletionHandler(store)
            .OnConversationDeletedAsync(CoachMemorySamples.Empty(), "conv-1");

        deleted.Should().Be(0);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SourceDeletionWorksEvenWhenTheFeatureIsOff()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var owner = CoachMemorySamples.Owner();

        var enabled = harness.NewStore(db);
        await SeedAsync(harness, enabled, owner, "conv-doomed", approve: true);

        harness.Options.Enabled = false;
        var disabled = harness.NewStore(db);

        var deleted = await harness.NewSourceDeletionHandler(disabled)
            .OnConversationDeletedAsync(owner, "conv-doomed");

        deleted.Should().Be(1);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
    }

    // ---------------------------------------------------------------- account deletion

    [Fact]
    public async Task TheAccountContributorRemovesEverythingForOneOwner()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        await SeedAsync(harness, store, owner, "conv-1", approve: true);
        await SeedAsync(
            harness,
            store,
            owner,
            "conv-2",
            CoachMemorySamples.Register(),
            evidence: "keep examples casual please");
        await SeedAsync(harness, store, CoachMemorySamples.Other(), "conv-3", approve: true);

        var contributor = harness.NewDeletionContributor(store);
        var deleted = await contributor.DeleteAllAsync(owner);

        deleted.Should().Be(2);
        (await db.CoachMemoryFacts.CountAsync(f => f.UserProfileId == CoachMemorySamples.OtherUserId))
            .Should().Be(1);
    }

    [Fact]
    public async Task TheAccountContributorIsIdempotent()
    {
        // The deletion coordinator runs every contributor twice in one transaction and rolls back
        // if the second pass still finds work. A non-zero second pass fails the whole deletion.
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        await SeedAsync(harness, store, owner, "conv-1", approve: true);

        var contributor = harness.NewDeletionContributor(store);

        (await contributor.DeleteAllAsync(owner)).Should().Be(1);
        (await contributor.DeleteAllAsync(owner)).Should().Be(0);
    }

    [Fact]
    public async Task TheAccountContributorRefusesAnEmptyOwner()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        await SeedAsync(harness, store, CoachMemorySamples.Owner(), "conv-1", approve: true);

        var deleted = await harness.NewDeletionContributor(store).DeleteAllAsync(CoachMemorySamples.Empty());

        deleted.Should().Be(0);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public void TheAccountContributorIsNamedForItsTable()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();

        harness.NewDeletionContributor(harness.NewStore(db)).Name.Should().Be("CoachMemoryFact");
    }
}
