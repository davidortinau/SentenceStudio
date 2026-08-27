using SentenceStudio.Contracts.LearnerMemory;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Api.Tests.Coach.Memory;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Learner memory against a real PostgreSQL server, where the partial unique index that keeps one
/// active fact per slot is actually enforced rather than emulated.
/// </summary>
/// <remarks>
/// The SQLite suite can show that the store's own logic is coherent. It cannot show that the
/// database will refuse a second active fact when two approvals arrive at once, because the
/// guarantee lives in a filtered unique index and in PostgreSQL's statement-level evaluation of
/// it. That is the property these tests exist to demonstrate.
/// </remarks>
public sealed class CoachPostgresMemoryStoreTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("memory");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private CoachMemoryStore NewStore(CoachDbContext db) => _harness.NewMemoryStore(db, new RecordingNotifier());

    private async Task<string> CreateCandidateAsync(
        CoachOwner owner,
        CoachMemoryStoredValue? value = null,
        string? conversationId = "conv-1")
    {
        await using var db = _harness.NewContext();
        var created = await NewStore(db).CreateCandidateAsync(
            owner,
            CoachMemorySamples.Candidate(value: value, conversationId: conversationId));

        created.Status.Should().Be(CoachMemoryStatusCode.Success);
        return created.Fact!.Id;
    }

    [PostgresFact]
    public async Task The_database_itself_refuses_a_second_active_fact_in_the_same_slot()
    {
        var first = await CreateCandidateAsync(CoachHistorySamples.Owner);

        await using var db = _harness.NewContext();
        var approved = await NewStore(db).ApproveAsync(CoachHistorySamples.Owner, first, expectedVersion: 1);
        approved.Status.Should().Be(CoachMemoryStatusCode.Success);

        var slot = await _harness.StringsAsync(
            $"""
             SELECT "Kind"::text || '|' || "ScopeKey"
             FROM "CoachMemoryFact"
             WHERE "Id" = '{approved.Fact!.Id}'
             """);
        var parts = slot.Single().Split('|');

        // Reach past the store and try to write a second active row into the same slot directly.
        // The store's own checks are not in play here; only the index is.
        var duplicate = async () => await _harness.ExecuteAsync(
            $"""
             INSERT INTO "CoachMemoryFact"
             SELECT 'fact-shadow' AS "Id", {ColumnsAfterId()}
             FROM "CoachMemoryFact" WHERE "Id" = '{approved.Fact.Id}'
             """);

        await duplicate.Should().ThrowAsync<Npgsql.PostgresException>(
            "the partial unique index on (owner, kind, scope) WHERE status = active is the real "
            + "guarantee; without it two approvals could both leave an active fact behind");

        parts.Should().HaveCount(2);
    }

    private static string ColumnsAfterId() =>
        """
        "UserProfileId","Kind","ScopeKey","Status","ProtectedValue","ValueSchemaVersion",
        "ContentProtectionVersion","Confidence","SourceCount","FirstObservedAt","LastObservedAt",
        "CreatedAt","UpdatedAt","Version","SourceConversationId","SourceMessageId","TargetLanguageCode",
        "ApprovedAt","LastUsedAt","ExpiresAt","SupersededByFactId","SupersededAt"
        """;

    [PostgresFact]
    public async Task Two_approvals_racing_for_one_slot_leave_exactly_one_active_fact()
    {
        // Both candidates occupy the same kind and scope, so only one may end up active. The store
        // demotes any incumbent and promotes the winner inside a single transaction precisely
        // because the index is evaluated per statement: doing it in two steps would leave a window
        // where both rows are active and the second statement would fail.
        var a = await CreateCandidateAsync(CoachHistorySamples.Owner);
        var b = await CreateCandidateAsync(CoachHistorySamples.Owner);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<CoachMemoryResult> ApproveAsync(string factId)
        {
            await using var db = _harness.NewContext();
            var store = NewStore(db);
            await gate.Task;
            return await store.ApproveAsync(CoachHistorySamples.Owner, factId, expectedVersion: 1);
        }

        var racers = new[] { ApproveAsync(a), ApproveAsync(b) };
        gate.SetResult();
        var results = await Task.WhenAll(racers);

        results.Should().Contain(r => r.Status == CoachMemoryStatusCode.Success,
            "at least one learner approval must take effect");

        var active = await _harness.ScalarAsync<long>(
            $"""
             SELECT count(*) FROM "CoachMemoryFact"
             WHERE "UserProfileId" = '{CoachHistorySamples.Owner.UserProfileId}' AND "Status" = 1
             """);

        active.Should().Be(1,
            "the slot holds one active fact no matter how the two approvals interleave");
    }

    [PostgresFact]
    public async Task A_superseded_fact_keeps_its_lineage_rather_than_vanishing()
    {
        var first = await CreateCandidateAsync(CoachHistorySamples.Owner);
        await using (var db = _harness.NewContext())
        {
            await NewStore(db).ApproveAsync(CoachHistorySamples.Owner, first, expectedVersion: 1);
        }

        var second = await CreateCandidateAsync(CoachHistorySamples.Owner);
        await using (var db = _harness.NewContext())
        {
            var approved = await NewStore(db).ApproveAsync(CoachHistorySamples.Owner, second, expectedVersion: 1);
            approved.Status.Should().Be(CoachMemoryStatusCode.Success);
        }

        var pointer = await _harness.StringsAsync(
            $"""
             SELECT coalesce("SupersedesId", '') FROM "CoachMemoryFact" WHERE "Id" = '{second}'
             """);

        pointer.Single().Should().Be(first,
            "the replacement records what it replaced, so the learner can be shown why their old "
            + "preference stopped applying instead of it silently disappearing");
    }

    [PostgresFact]
    public async Task One_learners_memory_is_invisible_and_untouchable_to_another()
    {
        var mine = await CreateCandidateAsync(CoachHistorySamples.Owner);

        await using var db = _harness.NewContext();
        var store = NewStore(db);

        (await store.GetAsync(CoachHistorySamples.Intruder, mine)).Status
            .Should().Be(CoachMemoryStatusCode.NotFound);
        (await store.ApproveAsync(CoachHistorySamples.Intruder, mine, 1)).Status
            .Should().Be(CoachMemoryStatusCode.NotFound);
        (await store.ForgetAsync(CoachHistorySamples.Intruder, mine, 1))
            .Should().Be(CoachMemoryStatusCode.NotFound);

        var survived = await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM \"CoachMemoryFact\" WHERE \"Id\" = '{mine}'");
        survived.Should().Be(1, "a refusal must not have been a deletion");
    }

    [PostgresFact]
    public async Task Forgetting_a_fact_removes_the_row_and_not_merely_its_status()
    {
        var factId = await CreateCandidateAsync(CoachHistorySamples.Owner);

        await using var db = _harness.NewContext();
        (await NewStore(db).ForgetAsync(CoachHistorySamples.Owner, factId, expectedVersion: 1))
            .Should().Be(CoachMemoryStatusCode.Success);

        var remaining = await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM \"CoachMemoryFact\" WHERE \"Id\" = '{factId}'");

        remaining.Should().Be(0,
            "forgetting is the one operation where the learner is promised the words are gone; a "
            + "tombstone row would keep the ciphertext on disk");
    }

    [PostgresFact]
    public async Task Deleting_a_conversation_removes_the_memory_it_alone_produced()
    {
        var fromDoomed = await CreateCandidateAsync(CoachHistorySamples.Owner, conversationId: "conv-doomed");
        var fromOther = await CreateCandidateAsync(CoachHistorySamples.Owner, conversationId: "conv-keep");

        await using var db = _harness.NewContext();
        var removed = await NewStore(db).DeleteForSourceConversationAsync(CoachHistorySamples.Owner, "conv-doomed");

        removed.Should().Be(1);

        var survivors = await _harness.StringsAsync(
            $"""
             SELECT "Id" FROM "CoachMemoryFact"
             WHERE "UserProfileId" = '{CoachHistorySamples.Owner.UserProfileId}'
             """);

        survivors.Should().ContainSingle().Which.Should().Be(fromOther);
        survivors.Should().NotContain(fromDoomed);
    }

    [PostgresFact]
    public async Task An_empty_owner_reads_nothing_rather_than_everything()
    {
        await CreateCandidateAsync(CoachHistorySamples.Owner);

        await using var db = _harness.NewContext();
        var store = NewStore(db);

        var page = await store.ListAsync(CoachHistorySamples.Empty, new CoachMemoryListFilter());
        page.Status.Should().Be(CoachMemoryStatusCode.NoOwner);
        page.Items.Should().BeEmpty(
            "an absent owner must never be treated as a wildcard against a multi-tenant table");

        (await store.ListEligibleForContextAsync(CoachHistorySamples.Empty)).Should().BeEmpty();
        (await store.DeleteAllForOwnerAsync(CoachHistorySamples.Empty)).Should().Be(0);

        var untouched = await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachMemoryFact\"");
        untouched.Should().Be(1, "the refusal must not have deleted the real owner's row");
    }

    [PostgresFact]
    public async Task The_selector_caps_what_it_hands_to_the_prompt()
    {
        for (var i = 0; i < 6; i++)
        {
            var id = await CreateCandidateAsync(CoachHistorySamples.Owner, conversationId: $"conv-{i}");
            await using var approveDb = _harness.NewContext();
            await NewStore(approveDb).ApproveAsync(CoachHistorySamples.Owner, id, expectedVersion: 1);
        }

        await using var db = _harness.NewContext();
        var store = NewStore(db);
        var selector = _harness.NewMemorySelector(
            store,
            new CoachMemoryOptions { Enabled = true, MaxContextFacts = 2 });

        var selected = await selector.SelectAsync(new CoachMemoryContextRequest(
            CoachHistorySamples.Owner,
            CoachMemorySamples.Korean,
            CoachMemoryTurnCategory.Unspecified));

        selected.Items.Count.Should().BeLessThanOrEqualTo(2,
            "the prompt budget is a hard ceiling; exceeding it silently would inflate every turn's cost");
    }
}
