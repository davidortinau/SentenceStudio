using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Api.Tests.Coach.Memory;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// What a learner's coach content actually looks like on a PostgreSQL disk, and whether a
/// restarted process can still read it.
/// </summary>
/// <remarks>
/// <para>
/// Encryption tests that stay inside the store only prove a round trip, which an implementation
/// that stored plaintext would also pass. The claim worth proving is the one an operator or a
/// database backup would falsify: that no learner-authored text appears in any text column of any
/// coach table. So these tests write through the production stores and then read every text
/// column back over a raw ADO connection, with no EF, no protector, and no model in the way.
/// </para>
/// <para>
/// The second claim is durability across a restart. The default fixture uses an ephemeral key
/// ring, which is right for isolation but hides the failure mode that matters in deployment: if
/// keys are not persisted, every restart silently orphans every row written before it. That is
/// proven here with a real file-backed key ring inside the worktree, using two independently
/// constructed providers to stand in for two processes.
/// </para>
/// </remarks>
public sealed class CoachPostgresDataProtectionTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;
    private string _keyRingRoot = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        // Inside the worktree on purpose: scratch key material must never land in a shared
        // temporary directory another user or agent could read.
        _keyRingRoot = Path.Combine(
            AppContext.BaseDirectory,
            "coach-pg-keyring",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keyRingRoot);

        _harness = await CoachPostgresHarness.CreateAsync("protect");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }

        if (_keyRingRoot is not null && Directory.Exists(_keyRingRoot))
        {
            Directory.Delete(_keyRingRoot, recursive: true);
        }
    }

    [PostgresFact]
    public async Task No_learner_text_reaches_any_text_column_of_any_coach_table()
    {
        await WriteEverySensitiveShapeAsync();

        var leaks = await ScanForPlaintextAsync();

        leaks.Should().BeEmpty(
            "learner-authored text must never be readable in a database backup, a replica, or an "
            + "operator's psql session; found plaintext in: " + string.Join(", ", leaks));
    }

    [PostgresFact]
    public async Task The_plaintext_scan_actually_detects_plaintext()
    {
        // A scan that can only ever pass proves nothing. This plants exactly the leak the test
        // above is looking for and requires the scan to name it -- so a future change that
        // narrowed the column list, or quoted an identifier wrongly, is caught as a broken
        // detector rather than reported as a clean bill of health.
        var conversationId = await WriteEverySensitiveShapeAsync();

        await _harness.ExecuteAsync(
            $"""
             UPDATE "CoachConversation"
             SET "ProtectedTitle" = '{CoachPersistenceSamples.LearnerSentinel} leaked'
             WHERE "Id" = '{conversationId}'
             """);

        var leaks = await ScanForPlaintextAsync();

        leaks.Should().ContainSingle()
            .Which.Should().StartWith("CoachConversation.ProtectedTitle");
    }

    [PostgresFact]
    public async Task The_stored_bytes_are_ciphertext_that_still_reads_back_through_the_store()
    {
        var conversationId = await WriteEverySensitiveShapeAsync();

        var stored = await _harness.StringsAsync(
            $"""
             SELECT "ProtectedPayload" FROM "CoachMessage"
             WHERE "ConversationId" = '{conversationId}'
             """);

        var ciphertext = stored.Should().ContainSingle().Subject;
        ciphertext.Should().NotContain(CoachPersistenceSamples.LearnerSentinel);
        ciphertext.Length.Should().BeGreaterThan(
            CoachPersistenceSamples.LearnerSentinel.Length,
            "an envelope carries key id, version, and a MAC, so it is always longer than the "
            + "text it protects -- a value the same size would suggest an encoding, not encryption");

        // And the same row is still readable through the production path, so the ciphertext is
        // not merely opaque but correct.
        await using var db = _harness.NewContext();
        var page = await _harness.NewMessageStore(db).GetLatestAsync(CoachHistorySamples.Owner, conversationId, pageSize: 20);

        page.Items.Should().ContainSingle()
            .Which.Payload.Text.Should().Contain(CoachPersistenceSamples.LearnerSentinel);
    }

    [PostgresFact]
    public async Task A_restarted_process_can_still_read_rows_written_before_the_restart()
    {
        // Two providers built independently over the same persisted key ring: the closest thing
        // a test can get to "the container was recycled".
        await using var firstHarness = await CoachPostgresHarness.CreateAsync(
            "protect_restart",
            dataProtection: BuildPersistedProvider());

        string conversationId;

        await using (var db = firstHarness.NewContext())
        {
            var conversation = await firstHarness.NewConversationStore(db)
                .CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());
            conversationId = conversation.Conversation!.Id;

            await firstHarness.NewMessageStore(db).AppendAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText()));
        }

        var restarted = firstHarness.WithDataProtection(BuildPersistedProvider());

        await using var afterRestart = restarted.NewContext();
        var page = await restarted.NewMessageStore(afterRestart).GetLatestAsync(CoachHistorySamples.Owner, conversationId, pageSize: 20);

        page.Items.Should().ContainSingle()
            .Which.Payload.Text.Should().Contain(
                CoachPersistenceSamples.LearnerSentinel,
                "a key ring that is not persisted turns every restart into silent, permanent data "
                + "loss for every conversation written before it");
    }

    [PostgresFact]
    public async Task A_row_encrypted_under_a_different_key_ring_is_reported_unreadable_not_crashed()
    {
        var conversationId = await WriteEverySensitiveShapeAsync();

        // A different key ring is what a restored backup with a lost key vault looks like.
        var stranger = _harness.WithDataProtection(new EphemeralDataProtectionProvider());

        await using var db = stranger.NewContext();
        var page = await stranger.NewMessageStore(db).GetLatestAsync(CoachHistorySamples.Owner, conversationId, pageSize: 20);

        // The contract is that an unreadable payload surfaces as a recoverable state rather than
        // an exception that takes the request -- and the learner's whole history view -- down.
        page.Status.Should().Be(CoachHistoryStatus.Success);
        page.Items.Should().ContainSingle()
            .Which.Payload.Should().BeNull(
                "the store returns an unreadable row with a null payload rather than inventing "
                + "empty content, so the client can show a safe recovery state instead of "
                + "pretending the learner said nothing");
    }

    [PostgresFact]
    public void Content_bound_to_one_record_cannot_be_unprotected_as_another()
    {
        var protector = _harness.ContentProtector;

        var context = new CoachProtectionContext(
            CoachHistorySamples.Owner,
            CoachProtectedContentKind.MessagePayload,
            "msg-1",
            protector.CurrentVersion);

        var ciphertext = protector.Protect(context, CoachPersistenceSamples.LearnerSentinel);

        protector.TryUnprotect(context, ciphertext, out var mine).Should().BeTrue();
        mine.Should().Be(CoachPersistenceSamples.LearnerSentinel);

        // Same key ring, different record: the purpose chain must refuse it. Without this, a row
        // copied between records -- or between learners -- would decrypt cleanly.
        var otherRecord = context with { RecordId = "msg-2" };
        protector.TryUnprotect(otherRecord, ciphertext, out var stolen).Should().BeFalse();
        stolen.Should().BeNull();

        var otherOwner = context with { Owner = CoachHistorySamples.Intruder };
        protector.TryUnprotect(otherOwner, ciphertext, out var crossTenant).Should().BeFalse();
        crossTenant.Should().BeNull();

        var otherKind = context with { Kind = CoachProtectedContentKind.ConversationTitle };
        protector.TryUnprotect(otherKind, ciphertext, out var wrongKind).Should().BeFalse();
        wrongKind.Should().BeNull();
    }

    [PostgresFact]
    public async Task Garbage_in_a_protected_column_is_refused_rather_than_thrown()
    {
        var conversationId = await WriteEverySensitiveShapeAsync();

        await _harness.ExecuteAsync(
            $"""
             UPDATE "CoachMessage" SET "ProtectedPayload" = 'not-an-envelope'
             WHERE "ConversationId" = '{conversationId}'
             """);

        await using var db = _harness.NewContext();
        var page = await _harness.NewMessageStore(db).GetLatestAsync(CoachHistorySamples.Owner, conversationId, pageSize: 20);

        page.Status.Should().Be(
            CoachHistoryStatus.Success,
            "one corrupt row must not deny the learner access to the rest of their history");
        page.Items.Should().ContainSingle().Which.Payload.Should().BeNull();
    }

    /// <summary>
    /// Reads every text column of every coach table over a raw connection and reports any that
    /// contains learner-authored sentinel text. No EF, no protector, no model: this is what an
    /// operator or a stolen backup would see.
    /// </summary>
    private async Task<List<string>> ScanForPlaintextAsync()
    {
        var sentinels = new[]
        {
            CoachPersistenceSamples.LearnerSentinel,
            CoachMemorySamples.ValueSentinel,
        };

        var columns = await _harness.StringsAsync(
            """
            SELECT table_name || '|' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name LIKE 'Coach%'
              AND data_type IN ('text', 'character varying', 'character')
            ORDER BY table_name, column_name
            """);

        columns.Should().NotBeEmpty("the scan is only meaningful if it found columns to scan");

        var leaks = new List<string>();

        foreach (var column in columns)
        {
            var parts = column.Split('|', 2);
            var predicate = string.Join(
                " OR ",
                sentinels.Select(s => $"""coalesce("{parts[1]}", '') LIKE '%{s}%'"""));

            var hits = await _harness.ScalarAsync<long>(
                $"""SELECT count(*) FROM "{parts[0]}" WHERE {predicate}""");

            if (hits > 0)
            {
                leaks.Add($"{parts[0]}.{parts[1]} ({hits} row(s))");
            }
        }

        return leaks;
    }

    private IDataProtectionProvider BuildPersistedProvider() =>
        new ServiceCollection()
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(_keyRingRoot))
            .SetApplicationName("SentenceStudio.Coach.Tests")
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();

    /// <summary>
    /// Writes one row of every shape that carries learner text, so the scan below has something
    /// to find if any of them ever stops protecting its content.
    /// </summary>
    private async Task<string> WriteEverySensitiveShapeAsync()
    {
        await using var db = _harness.NewContext();

        var conversation = await _harness.NewConversationStore(db)
            .CreateAsync(CoachHistorySamples.Owner, CoachHistorySamples.CreateConversation());
        var conversationId = conversation.Conversation!.Id;

        await _harness.NewMessageStore(db).AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText()));

        await _harness.NewTurnOperationStore(db).ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(
                conversationId,
                payload: $"{{\"text\":\"{CoachPersistenceSamples.LearnerSentinel}\"}}"));

        var memory = _harness.NewMemoryStore(db, new RecordingNotifier());
        var candidate = await memory.CreateCandidateAsync(
            CoachHistorySamples.Owner,
            CoachMemorySamples.Candidate(conversationId: conversationId));
        candidate.Fact.Should().NotBeNull();

        return conversationId;
    }
}
