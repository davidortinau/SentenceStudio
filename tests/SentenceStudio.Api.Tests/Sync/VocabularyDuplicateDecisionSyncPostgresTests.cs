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
/// Duplicate decisions (docs/duplicate-merge-ai-plan.md, Captain 2026-11-14) through the real API
/// sync endpoints on disposable PostgreSQL: a decision made on one device reaches the owner's other
/// devices, an undo follows it, and another learner never receives, changes or plants one.
/// </summary>
public sealed class VocabularyDuplicateDecisionSyncPostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private static readonly DateTime Day = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public VocabularyDuplicateDecisionSyncPostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task ADecisionAndItsUndo_ReachTheOwnersOtherDevice()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var id = SyncIds.VocabularyDuplicateDecision(owner, VocabularyDuplicateDecisionKind.KeptApart, "달",
            ["word-moon", "word-month"]);
        using var client = _fixture.CreateClient();
        var storeB = Guid.NewGuid();
        var initial = await _fixture.StartDownloadAsync(client, owner, storeId: storeB);
        using (var saved = await _fixture.SaveVersionAsync(client, owner, storeB, initial.SourceAnchor.Version))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Device A keeps 달 "moon" and "month" apart.
        var storeA = Guid.NewGuid();
        using (var a = await UploadFromStoreAsync(client, owner, storeA, Decision(id, owner, undoneAt: null)))
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        var toB = await DownloadAsync(client, owner, storeB);
        var row = Assert.Single(toB, item => Id(item) == id);
        Assert.Equal("KeptApart", row.Values["Kind"].Value?.ToString());
        Assert.Equal("word-month,word-moon", row.Values["CopyIds"].Value?.ToString());

        // A then undoes it: the stored row and B's next download carry the undo time.
        using (var undo = await UploadFromStoreAsync(client, owner, storeA,
            Decision(id, owner, undoneAt: Day.AddHours(1), ChangeType.Update)))
            Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(Day.AddHours(1), (await StoredAsync(id)).UndoneAt);
        var again = await DownloadAsync(client, owner, storeB);
        Assert.Contains(again, item => Id(item) == id && item.Values["UndoneAt"].Value is not null);
    }

    [ApplicationOperationPostgresFact]
    public async Task AnotherLearner_NeverReceivesOrChangesTheDecision_AndCannotUploadOneAsTheOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var id = SyncIds.VocabularyDuplicateDecision(owner, VocabularyDuplicateDecisionKind.KeptApart, "약",
            ["word-medicine", "word-about"]);
        await using (var db = _fixture.NewContext())
        {
            db.VocabularyDuplicateDecisions.Add(new VocabularyDuplicateDecision
            {
                Id = id, UserProfileId = owner, TermKey = "약", Kind = VocabularyDuplicateDecisionKind.KeptApart,
                CopyIds = SyncIds.DuplicateDecisionCopies(["word-medicine", "word-about"]),
                DecidedBy = VocabularyDuplicateDecider.Ai, Reason = "medicine and approximately", DecidedAt = Day
            });
            await db.SaveChangesAsync();
        }
        using var client = _fixture.CreateClient();

        Assert.DoesNotContain(await DownloadAsync(client, foreign, Guid.NewGuid()),
            item => item.TableName == "VocabularyDuplicateDecision" && Id(item) == id);
        Assert.Contains(await DownloadAsync(client, owner, Guid.NewGuid()),
            item => item.TableName == "VocabularyDuplicateDecision" && Id(item) == id);

        // Overwriting the owner's row (claiming it as the foreign learner's) is never applied.
        foreach (var changeType in new[] { ChangeType.Insert, ChangeType.Update })
        {
            using var stolen = await UploadFromStoreAsync(client, foreign, Guid.NewGuid(),
                Decision(id, foreign, Day.AddDays(1), changeType));
            Assert.Equal(HttpStatusCode.OK, stolen.StatusCode);
        }
        // Writing a row in the owner's name is refused.
        var planted = SyncIds.VocabularyDuplicateDecision(owner, VocabularyDuplicateDecisionKind.KeptApart, "눈",
            ["word-eye", "word-snow"]);
        using (var named = await UploadFromStoreAsync(client, foreign, Guid.NewGuid(),
            Decision(planted, owner, undoneAt: null)))
            Assert.Equal(HttpStatusCode.Forbidden, named.StatusCode);
        // A row with no owner is left out and stays on the device.
        var ownerless = SyncIds.VocabularyDuplicateDecision(owner, VocabularyDuplicateDecisionKind.KeptApart, "말",
            ["word-horse", "word-speech"]);
        using (var unowned = await UploadFromStoreAsync(client, owner, Guid.NewGuid(),
            Decision(ownerless, string.Empty, undoneAt: null)))
            Assert.Equal(HttpStatusCode.OK, unowned.StatusCode);

        var stored = await StoredAsync(id);
        Assert.Equal(owner, stored.UserProfileId);
        Assert.Null(stored.UndoneAt);
        await using var check = _fixture.NewContext();
        Assert.False(await check.VocabularyDuplicateDecisions.AnyAsync(row => row.Id == planted));
        Assert.False(await check.VocabularyDuplicateDecisions.AnyAsync(row => row.Id == ownerless));
    }

    [ApplicationOperationPostgresFact]
    public async Task Migration_UpCreatesTheTable_DownDropsOnlyIt_UpAgainIsClean()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("duplicate_decision_migration");
        await using (var db = database.NewContext())
        {
            Assert.Contains("20261114203658_AddVocabularyDuplicateDecision",
                await db.Database.GetAppliedMigrationsAsync());
            db.UserProfiles.Add(new UserProfile { Id = "owner-a", Email = "a@example.test" });
            await db.SaveChangesAsync();
            db.VocabularyDuplicateDecisions.Add(new VocabularyDuplicateDecision
            {
                Id = "dd:test", UserProfileId = "owner-a", TermKey = "소리", Kind = VocabularyDuplicateDecisionKind.Merged,
                CopyIds = "word-a,word-b", KeeperId = "word-a", DecidedBy = VocabularyDuplicateDecider.Learner,
                Before = "{}", DecidedAt = Day
            });
            await db.SaveChangesAsync();
        }
        await using (var reread = database.NewContext())
        {
            var row = await reread.VocabularyDuplicateDecisions.AsNoTracking().SingleAsync();
            Assert.Equal(VocabularyDuplicateDecisionKind.Merged, row.Kind);
            Assert.Equal(VocabularyDuplicateDecider.Learner, row.DecidedBy);
            Assert.Equal(Day, row.DecidedAt);
            Assert.Equal(DateTimeKind.Utc, row.DecidedAt.Kind);
        }

        await using (var down = database.NewContext())
            await down.GetService<IMigrator>().MigrateAsync("20261104162953_AddSyncDeletionLog");
        await using (var check = database.NewContext())
        {
            var connection = check.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('\"VocabularyDuplicateDecision\"') IS NULL";
            Assert.Equal(true, await command.ExecuteScalarAsync());
            Assert.Equal(1, await check.UserProfiles.CountAsync());
        }

        await using (var up = database.NewContext())
        {
            await up.Database.MigrateAsync();
            Assert.Empty(await up.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await up.VocabularyDuplicateDecisions.CountAsync());
        }
    }

    private static SyncItem Decision(string id, string owner, DateTime? undoneAt,
        ChangeType changeType = ChangeType.Insert) =>
        new("VocabularyDuplicateDecision", changeType, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner, ["TermKey"] = "달", ["Kind"] = "KeptApart",
            ["CopyIds"] = "word-month,word-moon", ["KeeperId"] = null, ["DecidedBy"] = "Ai",
            ["Reason"] = "moon and month are different words", ["Before"] = null,
            ["DecidedAt"] = Day, ["UndoneAt"] = undoneAt
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

    private async Task<VocabularyDuplicateDecision> StoredAsync(string id)
    {
        await using var db = _fixture.NewContext();
        return await db.VocabularyDuplicateDecisions.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private static string? Id(SyncItem item) =>
        item.Values.TryGetValue("Id", out var value) ? value.Value?.ToString() : null;
}
