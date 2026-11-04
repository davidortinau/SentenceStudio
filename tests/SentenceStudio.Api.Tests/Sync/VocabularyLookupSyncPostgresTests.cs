using System.Net;
using System.Net.Http.Json;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// Lookup history (Captain 2026-11-04, decision 4 B) through the real API sync endpoints on
/// disposable PostgreSQL: two devices that looked up the same text offline upload the same row
/// ID; the copy looked up most recently wins, the earliest first lookup is kept, and neither
/// upload is refused. Another learner never receives or changes the row.
/// </summary>
public sealed class VocabularyLookupSyncPostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private static readonly DateTime Day = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public VocabularyLookupSyncPostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task TwoDevicesOffline_SameLookup_NewestWins_EarliestFirstKept_NeitherRefused()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var id = SyncIds.VocabularyLookup(owner, "Korean", VocabularyLookupDirection.ToNative, "물");
        using var client = _fixture.CreateClient();
        // Devices B and C synced before any lookup existed, then went offline.
        var storeB = Guid.NewGuid();
        var storeC = Guid.NewGuid();
        foreach (var store in new[] { storeB, storeC })
        {
            var initial = await _fixture.StartDownloadAsync(client, owner, storeId: store);
            using var saved = await _fixture.SaveVersionAsync(
                client, owner, store, initial.SourceAnchor.Version);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        // Device A looked up 물 at 9:00 and 9:10 and syncs first.
        using (var a = await UploadFromStoreAsync(client, owner, Guid.NewGuid(),
            Lookup(id, owner, "water (A)", Day, Day.AddMinutes(10))))
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);

        // Device B looked up 물 at 9:20 and 9:30 offline: its copy is newer, but A's first lookup
        // is earlier. B also sends a lookup of its own that is new to the server.
        var other = SyncIds.VocabularyLookup(owner, "Korean", VocabularyLookupDirection.ToNative, "불");
        using (var b = await UploadFromStoreAsync(client, owner, storeB,
            Lookup(id, owner, "water (B)", Day.AddMinutes(20), Day.AddMinutes(30)),
            Lookup(other, owner, "fire", Day.AddMinutes(25), Day.AddMinutes(25))))
            Assert.Equal(HttpStatusCode.OK, b.StatusCode);

        var merged = await StoredAsync(id);
        Assert.Equal("water (B)", merged.Translation);
        Assert.Equal(Day.AddMinutes(30), merged.LastLookedUpAt);
        Assert.Equal(Day, merged.FirstLookedUpAt);
        Assert.Equal("fire", (await StoredAsync(other)).Translation);

        // B's next download carries the merged row (its translation, A's first lookup), and not
        // B's own new lookup.
        // (CoreSync sends one item per logged operation on the row; each carries its current values.)
        var toB = await DownloadAsync(client, owner, storeB);
        Assert.NotEmpty(toB.Where(item => Id(item) == id));
        Assert.All(toB.Where(item => Id(item) == id), row =>
        {
            Assert.Equal("water (B)", row.Values["Translation"].Value?.ToString());
            Assert.Equal(Day, Utc(row.Values["FirstLookedUpAt"].Value));
        });
        Assert.DoesNotContain(toB, item => Id(item) == other);

        // Device C saved an older copy (9:05) with an even earlier first lookup (8:00): the stored
        // copy wins and keeps its translation, the first time moves to 8:00, and C is sent the row.
        using (var c = await UploadFromStoreAsync(client, owner, storeC,
            Lookup(id, owner, "water (C)", Day.AddHours(-1), Day.AddMinutes(5))))
            Assert.Equal(HttpStatusCode.OK, c.StatusCode);
        merged = await StoredAsync(id);
        Assert.Equal("water (B)", merged.Translation);
        Assert.Equal(Day.AddMinutes(30), merged.LastLookedUpAt);
        Assert.Equal(Day.AddHours(-1), merged.FirstLookedUpAt);
        var toC = await DownloadAsync(client, owner, storeC);
        Assert.NotEmpty(toC.Where(item => Id(item) == id));
        Assert.All(toC.Where(item => Id(item) == id), row =>
        {
            Assert.Equal("water (B)", row.Values["Translation"].Value?.ToString());
            Assert.Equal(Day.AddHours(-1), Utc(row.Values["FirstLookedUpAt"].Value));
        });
    }

    [ApplicationOperationPostgresFact]
    public async Task AnotherLearner_NeverReceivesOrChangesTheLookup_AndCannotUploadOneAsTheOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var id = SyncIds.VocabularyLookup(owner, "Korean", VocabularyLookupDirection.ToNative, "바다");
        await using (var db = _fixture.NewContext())
        {
            db.VocabularyLookups.Add(new VocabularyLookup
            {
                Id = id, UserProfileId = owner, Text = "바다", Translation = "sea",
                Language = "Korean", Direction = VocabularyLookupDirection.ToNative,
                Activity = VocabularyLookupActivity.Reading,
                FirstLookedUpAt = Day, LastLookedUpAt = Day
            });
            await db.SaveChangesAsync();
        }
        using var client = _fixture.CreateClient();

        Assert.DoesNotContain(await DownloadAsync(client, foreign, Guid.NewGuid()),
            item => item.TableName == "VocabularyLookup" && Id(item) == id);
        Assert.Contains(await DownloadAsync(client, owner, Guid.NewGuid()),
            item => item.TableName == "VocabularyLookup" && Id(item) == id);

        // Overwriting the owner's row (claiming it as the foreign learner's) is refused, even
        // with a newer lookup time, and the row is unchanged.
        foreach (var changeType in new[] { ChangeType.Insert, ChangeType.Update })
        {
            using var stolen = await UploadFromStoreAsync(client, foreign, Guid.NewGuid(),
                Lookup(id, foreign, "stolen", Day, Day.AddDays(1), changeType));
            Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
        }
        // Writing a row in the owner's name is refused too.
        var planted = SyncIds.VocabularyLookup(owner, "Korean", VocabularyLookupDirection.ToNative, "산");
        using (var named = await UploadFromStoreAsync(client, foreign, Guid.NewGuid(),
            Lookup(planted, owner, "mountain", Day, Day)))
            Assert.Equal(HttpStatusCode.Forbidden, named.StatusCode);
        // A row with no owner is never applied: it is left out and stays on the device
        // (decision 1: a row the server cannot place no longer refuses the whole upload).
        var ownerless = SyncIds.VocabularyLookup(owner, "Korean", VocabularyLookupDirection.ToNative, "강");
        using (var unowned = await UploadFromStoreAsync(client, owner, Guid.NewGuid(),
            Lookup(ownerless, string.Empty, "river", Day, Day)))
            Assert.Equal(HttpStatusCode.OK, unowned.StatusCode);

        var stored = await StoredAsync(id);
        Assert.Equal(owner, stored.UserProfileId);
        Assert.Equal("sea", stored.Translation);
        await using var check = _fixture.NewContext();
        Assert.False(await check.VocabularyLookups.AnyAsync(row => row.Id == planted));
        Assert.False(await check.VocabularyLookups.AnyAsync(row => row.Id == ownerless));
    }

    [ApplicationOperationPostgresFact]
    public async Task Migration_UpCreatesTheTable_DownDropsOnlyIt_UpAgainIsClean()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("lookup_migration");
        await using (var db = database.NewContext())
        {
            Assert.Contains("20261104153858_AddVocabularyLookup",
                await db.Database.GetAppliedMigrationsAsync());
            db.UserProfiles.Add(new UserProfile { Id = "owner-a", Email = "a@example.test" });
            await db.SaveChangesAsync();
            db.VocabularyLookups.Add(new VocabularyLookup
            {
                Id = "lk:test", UserProfileId = "owner-a", Text = "물", Translation = "water",
                Language = "Korean", Direction = VocabularyLookupDirection.ToTarget,
                Activity = VocabularyLookupActivity.Writing,
                FirstLookedUpAt = Day, LastLookedUpAt = Day.AddMinutes(1)
            });
            await db.SaveChangesAsync();
        }
        await using (var reread = database.NewContext())
        {
            var row = await reread.VocabularyLookups.AsNoTracking().SingleAsync();
            Assert.Equal(VocabularyLookupDirection.ToTarget, row.Direction);
            Assert.Equal(Day.AddMinutes(1), row.LastLookedUpAt);
            Assert.Equal(DateTimeKind.Utc, row.LastLookedUpAt.Kind);
        }

        await using (var down = database.NewContext())
            await down.GetService<IMigrator>().MigrateAsync("20261103230000_SyncOwnersPhase3");
        await using (var check = database.NewContext())
        {
            var connection = check.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('\"VocabularyLookup\"') IS NULL";
            Assert.Equal(true, await command.ExecuteScalarAsync());
            Assert.Equal(1, await check.UserProfiles.CountAsync());
        }

        await using (var up = database.NewContext())
        {
            await up.Database.MigrateAsync();
            Assert.Empty(await up.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await up.VocabularyLookups.CountAsync());
        }
    }

    private static SyncItem Lookup(string id, string owner, string translation,
        DateTime first, DateTime last, ChangeType changeType = ChangeType.Insert) =>
        new("VocabularyLookup", changeType, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner, ["Text"] = "물", ["Translation"] = translation,
            ["Language"] = "Korean", ["Direction"] = "ToNative",
            ["Activity"] = VocabularyLookupActivity.Reading, ["ResourceId"] = null,
            ["FirstLookedUpAt"] = first, ["LastLookedUpAt"] = last
        });

    private async Task<HttpResponseMessage> UploadFromStoreAsync(
        HttpClient client, string owner, Guid storeId, params SyncItem[] items)
    {
        var sessionId = Guid.NewGuid();
        using (var begin = await _fixture.BeginUploadAsync(client, owner, sessionId, storeId, items.Length))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var chunk = await _fixture.UploadChunkAsync(client, owner, sessionId, items))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        return await _fixture.CompleteUploadAsync(client, owner, sessionId);
    }

    private async Task<SyncItem[]> DownloadAsync(HttpClient client, string owner, Guid storeId)
    {
        var session = await _fixture.StartDownloadAsync(client, owner, storeId: storeId);
        using var chunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item/{session.SessionId}/0/5000", owner);
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        return await chunk.Content.ReadFromJsonAsync<SyncItem[]>() ?? [];
    }

    private async Task<VocabularyLookup> StoredAsync(string id)
    {
        await using var db = _fixture.NewContext();
        return await db.VocabularyLookups.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private static string? Id(SyncItem item) =>
        item.Values.TryGetValue("Id", out var value) ? value.Value?.ToString() : null;

    /// <summary>
    /// A downloaded timestamp read the way a device stores it: a zone-less value is UTC (the
    /// fixture's EnsureCreated schema, made under the legacy timestamp switch, has zone-less
    /// columns; migrated databases send UTC).
    /// </summary>
    private static DateTime Utc(object? value)
    {
        var date = value switch
        {
            DateTime parsed => parsed,
            DateTimeOffset offset => offset.UtcDateTime,
            System.Text.Json.JsonElement json => json.GetDateTime(),
            _ => throw new InvalidOperationException($"Not a timestamp: {value}")
        };
        return date.Kind == DateTimeKind.Local ? date.ToUniversalTime()
            : DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }
}
