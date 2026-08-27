using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Exercises the durable ledger against a real PostgreSQL server, with real concurrency.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite suite already proves the ledger's logic. What it cannot prove is the part that only
/// exists on a server: several connections contending for the same conversation at the same
/// instant. SQLite serializes writers for you, so a sequence-allocation bug that would corrupt a
/// transcript under PostgreSQL passes there without complaint.
/// </para>
/// <para>
/// These tests therefore always use one <see cref="CoachDbContext"/> per simulated worker, which
/// means one connection per worker, which means the database is genuinely arbitrating.
/// </para>
/// </remarks>
public sealed class CoachPostgresMessageLedgerTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;
    private string _conversationId = string.Empty;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("ledger");

        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);
        var created = await conversations.CreateAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.CreateConversation());
        created.Status.Should().Be(CoachHistoryStatus.Success);
        _conversationId = created.Conversation!.Id;
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Concurrent_appends_from_independent_connections_produce_a_gap_free_sequence()
    {
        // Three writers is comfortably inside the store's five-attempt retry budget: the worst a
        // writer can do is lose two races before it wins, so every append must succeed. Six would
        // sit right on the edge of the budget and make the test a measure of machine load rather
        // than of the store. Contention past the budget is covered by its own test below.
        //
        // Even three is past what production allows: the turn-operation lease gives a conversation
        // one writer at a time, so genuine contention here comes from a retry racing the worker it
        // thought had died.
        const int Writers = 3;

        var results = await AppendConcurrentlyAsync(Writers, i => CoachHistorySamples.LearnerText($"turn-{i}"));

        results.Should().OnlyContain(r => r.Status == CoachHistoryStatus.Success);

        var sequences = results.Select(r => r.Message!.Sequence).OrderBy(s => s).ToArray();
        sequences.Should().OnlyHaveUniqueItems("the unique index makes a duplicate sequence unrepresentable");
        sequences.Should().Equal(Enumerable.Range(1, Writers).Select(i => (long)i),
            "an append that retried after a collision must still land on the next free slot, not skip one");

        // And the same is true of what is actually on disk, not just what the store returned.
        var stored = await _harness.StringsAsync(
            $"SELECT \"Sequence\"::text FROM \"CoachMessage\" WHERE \"ConversationId\" = '{_conversationId}' ORDER BY \"Sequence\"");
        stored.Select(long.Parse).Should().Equal(sequences);
    }

    [PostgresFact]
    public async Task Contention_past_the_retry_budget_is_refused_honestly_rather_than_corrupting_the_ledger()
    {
        // Thirty writers on one conversation cannot all win inside a bounded retry budget, and
        // the store is right not to spin forever. What matters is the shape of the failure: the
        // losers must be told they lost, and the ledger must still be a clean 1..n with no gap,
        // no duplicate, and no orphan row from a rolled-back attempt.
        const int Writers = 30;

        var results = await AppendConcurrentlyAsync(Writers, i => CoachHistorySamples.LearnerText($"turn-{i}"));

        var succeeded = results.Where(r => r.Status == CoachHistoryStatus.Success).ToArray();
        var refused = results.Where(r => r.Status != CoachHistoryStatus.Success).ToArray();

        succeeded.Should().NotBeEmpty();
        refused.Should().OnlyContain(r => r.Status == CoachHistoryStatus.Conflict,
            "a writer that lost the race must be told to retry, not handed a different error");
        refused.Should().OnlyContain(r => r.Message == null,
            "a refused append must not hand back a message that was never committed");

        var sequences = succeeded.Select(r => r.Message!.Sequence).OrderBy(s => s).ToArray();
        sequences.Should().Equal(Enumerable.Range(1, succeeded.Length).Select(i => (long)i),
            "the ledger stays contiguous no matter how many writers were refused");

        var rowCount = await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM \"CoachMessage\" WHERE \"ConversationId\" = '{_conversationId}'");
        rowCount.Should().Be(succeeded.Length,
            "every rolled-back attempt must leave nothing behind");

        // And the refused writers succeed once the storm passes, which is what makes Conflict a
        // back-pressure signal rather than data loss.
        await using var db = _harness.NewContext();
        var store = _harness.NewMessageStore(db);
        var retried = await store.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText("after-the-storm")));

        retried.Status.Should().Be(CoachHistoryStatus.Success);
        retried.Message!.Sequence.Should().Be(succeeded.Length + 1);
    }

    [PostgresFact]
    public async Task Concurrent_appends_leave_the_conversation_counters_consistent_with_the_ledger()
    {
        const int Writers = 6;

        var results = await AppendConcurrentlyAsync(
            Writers,
            i => CoachHistorySamples.CoachText($"reply-{i}"),
            CoachMessageRole.Coach);

        // Six writers against a five-attempt retry budget is deliberately close to the edge: the
        // last writer to get a turn has to survive five collisions, so demanding that all six win
        // would be asserting a scheduling accident rather than a property of the store. What must
        // hold no matter how the races fall is that the conversation's denormalized head agrees
        // exactly with the ledger it summarizes. A racing update that read a stale value would
        // leave the two permanently out of step, and the next append would then try to reuse a
        // taken sequence forever.
        var succeeded = results.Count(r => r.Status == CoachHistoryStatus.Success);
        succeeded.Should().BeGreaterThan(0);
        results.Should().OnlyContain(
            r => r.Status == CoachHistoryStatus.Success || r.Status == CoachHistoryStatus.Conflict,
            "a writer either appends or is told it ran out of retries; there is no third outcome");

        var lastSequence = await _harness.ScalarAsync<long>(
            $"SELECT \"LastSequence\" FROM \"CoachConversation\" WHERE \"Id\" = '{_conversationId}'");
        var maxSequence = await _harness.ScalarAsync<long>(
            $"SELECT max(\"Sequence\") FROM \"CoachMessage\" WHERE \"ConversationId\" = '{_conversationId}'");
        var rowCount = await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM \"CoachMessage\" WHERE \"ConversationId\" = '{_conversationId}'");

        lastSequence.Should().Be(succeeded, "the head counts exactly the appends that were accepted");
        maxSequence.Should().Be(succeeded);
        rowCount.Should().Be(succeeded, "the ledger is gap-free, so its height equals its highest sequence");
    }

    /// <summary>
    /// Releases <paramref name="writers"/> appends at the same instant, each on its own context
    /// and therefore its own connection.
    /// </summary>
    private async Task<CoachMessageAppendResult[]> AppendConcurrentlyAsync(
        int writers,
        Func<int, CoachMessagePayload> payload,
        CoachMessageRole role = CoachMessageRole.Learner)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appends = Enumerable.Range(0, writers).Select(async i =>
        {
            // One context per writer: a shared context would serialize on a single connection and
            // the race this test exists to run would never happen.
            await using var db = new CoachDbContext(_harness.DbOptions);
            var store = _harness.NewMessageStore(db);
            await start.Task;
            return await store.AppendAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Append(_conversationId, payload(i), role));
        }).ToArray();

        start.SetResult();
        return await Task.WhenAll(appends);
    }

    [PostgresFact]
    public async Task Paging_backwards_with_the_cursor_walks_the_whole_transcript_exactly_once()
    {
        await using var db = _harness.NewContext();
        var store = _harness.NewMessageStore(db);

        for (var i = 1; i <= 25; i++)
        {
            var appended = await store.AppendAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText($"m{i}")));
            appended.Status.Should().Be(CoachHistoryStatus.Success);
        }

        var seen = new List<long>();
        var page = await store.GetLatestAsync(CoachHistorySamples.Owner, _conversationId, pageSize: 10);
        page.Status.Should().Be(CoachHistoryStatus.Success);

        while (true)
        {
            page.Items.Select(m => m.Sequence).Should().BeInAscendingOrder("a page is always chronological");
            seen.InsertRange(0, page.Items.Select(m => m.Sequence));

            if (page.PreviousCursor is null)
            {
                break;
            }

            page = await store.GetBeforeAsync(
                CoachHistorySamples.Owner, _conversationId, page.PreviousCursor, pageSize: 10);
            page.Status.Should().Be(CoachHistoryStatus.Success);
        }

        seen.Should().Equal(Enumerable.Range(1, 25).Select(i => (long)i));
        seen.Should().OnlyHaveUniqueItems("a cursor walk must not repeat a message");
    }

    [PostgresFact]
    public async Task A_range_read_returns_exactly_the_messages_one_turn_appended()
    {
        await using var db = _harness.NewContext();
        var store = _harness.NewMessageStore(db);

        for (var i = 1; i <= 8; i++)
        {
            await store.AppendAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText($"m{i}")));
        }

        var range = await store.GetRangeAsync(CoachHistorySamples.Owner, _conversationId, 3, 5);
        range.Status.Should().Be(CoachHistoryStatus.Success);
        range.Items.Select(m => m.Sequence).Should().Equal(3L, 4L, 5L);
    }

    [PostgresFact]
    public async Task Another_learner_can_neither_read_nor_append_to_this_conversation()
    {
        await using var db = _harness.NewContext();
        var store = _harness.NewMessageStore(db);
        var conversations = _harness.NewConversationStore(db);

        await store.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText()));

        var intruderAppend = await store.AppendAsync(
            CoachHistorySamples.Intruder,
            CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText("stolen")));
        intruderAppend.Status.Should().Be(CoachHistoryStatus.NotFound,
            "a conversation another learner owns must be indistinguishable from one that does not exist");

        var intruderRead = await store.GetLatestAsync(CoachHistorySamples.Intruder, _conversationId);
        intruderRead.Status.Should().Be(CoachHistoryStatus.NotFound);
        intruderRead.Items.Should().BeEmpty();

        var intruderGet = await conversations.GetAsync(CoachHistorySamples.Intruder, _conversationId);
        intruderGet.Status.Should().Be(CoachHistoryStatus.NotFound);

        // An owner with no authority at all is refused before any query runs.
        var anonymous = await store.GetLatestAsync(CoachHistorySamples.Empty, _conversationId);
        anonymous.Status.Should().Be(CoachHistoryStatus.NoOwner);

        // Nothing the intruder did reached the database.
        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachMessage\"")).Should().Be(1);
    }

    [PostgresFact]
    public async Task The_same_authority_from_a_different_tenant_still_owns_its_own_conversations()
    {
        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);

        // Tenant is a hint, not part of ownership: a learner who signs in through a different
        // tenant context is still the same learner and must still see their own history.
        var found = await conversations.GetAsync(CoachHistorySamples.OwnerOtherTenant, _conversationId);
        found.Status.Should().Be(CoachHistoryStatus.Success);
    }

    [PostgresFact]
    public async Task Soft_delete_hides_the_conversation_before_the_purge_removes_its_rows()
    {
        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);
        var messages = _harness.NewMessageStore(db);

        await messages.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText()));

        (await conversations.SoftDeleteAsync(CoachHistorySamples.Owner, _conversationId))
            .Should().Be(CoachHistoryStatus.Success);

        // Hidden from every read path immediately...
        (await conversations.GetAsync(CoachHistorySamples.Owner, _conversationId)).Status
            .Should().Be(CoachHistoryStatus.NotFound);
        (await conversations.ListAsync(CoachHistorySamples.Owner)).Items.Should().BeEmpty();
        (await messages.GetLatestAsync(CoachHistorySamples.Owner, _conversationId)).Status
            .Should().Be(CoachHistoryStatus.NotFound);

        // ...but still physically present, which is what makes an accidental delete recoverable.
        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachConversation\"")).Should().Be(1);
        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachMessage\"")).Should().Be(1);

        // A repeat delete reports NotFound rather than Success. That is consistent with every
        // other read path — the row is already invisible to this owner — and it is what the
        // interface documents; only SetClosedAsync claims idempotency.
        (await conversations.SoftDeleteAsync(CoachHistorySamples.Owner, _conversationId))
            .Should().Be(CoachHistoryStatus.NotFound);

        (await conversations.PurgeAsync(CoachHistorySamples.Owner, _conversationId))
            .Should().Be(CoachHistoryStatus.Success);

        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachConversation\"")).Should().Be(0);
        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachMessage\"")).Should().Be(0,
            "the composite foreign key cascades, so no orphan message can outlive its conversation");
    }

    [PostgresFact]
    public async Task An_intruder_cannot_soft_delete_or_purge_someone_elses_conversation()
    {
        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);

        (await conversations.SoftDeleteAsync(CoachHistorySamples.Intruder, _conversationId))
            .Should().Be(CoachHistoryStatus.NotFound);
        (await conversations.PurgeAsync(CoachHistorySamples.Intruder, _conversationId))
            .Should().Be(CoachHistoryStatus.NotFound);

        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachConversation\"")).Should().Be(1);
    }

    [PostgresFact]
    public async Task Export_streams_the_owners_transcript_and_nobody_elses()
    {
        await using var db = _harness.NewContext();
        var messages = _harness.NewMessageStore(db);
        var conversations = _harness.NewConversationStore(db);
        var export = _harness.NewExportReader(db);

        await messages.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText("first")));
        await messages.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(_conversationId, CoachHistorySamples.StructuredAnswer(), CoachMessageRole.Coach));

        var intruderConversation = await conversations.CreateAsync(
            CoachHistorySamples.Intruder, CoachHistorySamples.CreateConversation("theirs"));
        await messages.AppendAsync(
            CoachHistorySamples.Intruder,
            CoachHistorySamples.Append(intruderConversation.Conversation!.Id, CoachHistorySamples.LearnerText("theirs")));

        var exported = new List<CoachConversationRecord>();
        await foreach (var conversation in export.StreamConversationsAsync(CoachHistorySamples.Owner))
        {
            exported.Add(conversation);
        }

        exported.Should().ContainSingle().Which.Id.Should().Be(_conversationId);

        var exportedMessages = new List<CoachMessageRecord>();
        await foreach (var message in export.StreamMessagesAsync(CoachHistorySamples.Owner, _conversationId))
        {
            exportedMessages.Add(message);
        }

        exportedMessages.Select(m => m.Sequence).Should().Equal(1L, 2L);
        exportedMessages.Should().OnlyContain(m => m.IsReadable, "an export the learner cannot read is not an export");
        exportedMessages[0].Payload!.Text.Should().Be("first");
        exportedMessages[1].Payload!.Answer!.PlainText.Should().Be("Use the polite form.");

        // An export must never be able to reach across owners even when handed a real id.
        var crossOwner = new List<CoachMessageRecord>();
        await foreach (var message in export.StreamMessagesAsync(CoachHistorySamples.Owner, intruderConversation.Conversation!.Id))
        {
            crossOwner.Add(message);
        }

        crossOwner.Should().BeEmpty();
    }
}
