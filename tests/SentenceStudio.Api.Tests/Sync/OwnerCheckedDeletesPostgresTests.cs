using System.Net;
using System.Net.Http.Json;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SentenceStudio.Api.Sync;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// Owner-checked deletes (Captain 2026-11-04, decision 2) on throwaway PostgreSQL, with two
/// learners and several devices: the server applies a device delete only for the learner's own
/// row, logs every delete with its owner (web, cascade or device), and sends it only to that
/// learner's other devices.
/// </summary>
public sealed class OwnerCheckedDeletesPostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public OwnerCheckedDeletesPostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task OwnDeviceDelete_IsApplied_AndReachesOnlyTheOwnersOtherDevice()
    {
        var (owner, other) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        using var client = _fixture.CreateClient();
        var phone = new Device(_fixture, client, owner);
        var tablet = new Device(_fixture, client, owner);
        var otherPhone = new Device(_fixture, client, other);
        await phone.DownloadAsync();
        await tablet.DownloadAsync();
        await otherPhone.DownloadAsync();

        // Removing a word from a resource, deleting a minimal pair, a scene image, a skill and
        // a lookup (lookups have no delete in the apps yet; the same owner rule covers them).
        using (var response = await phone.UploadAsync(
            Delete("ResourceVocabularyMapping", graph.Mapping),
            Delete("MinimalPair", graph.Pair),
            Delete("SceneImage", graph.Image),
            Delete("SkillProfile", graph.Skill),
            Delete("VocabularyLookup", graph.Lookup)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using (var db = _fixture.NewContext())
        {
            Assert.False(await db.ResourceVocabularyMappings.AnyAsync(row => row.Id == graph.Mapping));
            Assert.False(await db.MinimalPairs.AnyAsync(row => row.Id == graph.Pair));
            Assert.False(await db.SceneImages.AnyAsync(row => row.Id == graph.Image));
            Assert.False(await db.SkillProfiles.AnyAsync(row => row.Id == graph.Skill));
            Assert.False(await db.VocabularyLookups.AnyAsync(row => row.Id == graph.Lookup));
            Assert.True(await db.LearningResources.AnyAsync(row => row.Id == graph.Resource));
            Assert.True(await db.VocabularyWords.AnyAsync(row => row.Id == graph.Word));
        }
        Assert.Equal(owner, await LoggedOwnerAsync("ResourceVocabularyMapping", graph.Mapping));
        Assert.Equal(owner, await LoggedOwnerAsync("MinimalPair", graph.Pair));
        Assert.Equal(owner, await LoggedOwnerAsync("SceneImage", graph.Image));

        var onTablet = await tablet.DownloadAsync();
        Assert.Contains(onTablet, item => IsDelete(item, "ResourceVocabularyMapping", graph.Mapping));
        Assert.Contains(onTablet, item => IsDelete(item, "MinimalPair", graph.Pair));
        Assert.Contains(onTablet, item => IsDelete(item, "SceneImage", graph.Image));
        Assert.Contains(onTablet, item => IsDelete(item, "SkillProfile", graph.Skill));
        Assert.Contains(onTablet, item => IsDelete(item, "VocabularyLookup", graph.Lookup));
        Assert.DoesNotContain(await otherPhone.DownloadAsync(), item => graph.Ids.Contains(Id(item)));
        // The device that deleted them is not sent its own deletes back.
        Assert.DoesNotContain(await phone.DownloadAsync(), item => item.ChangeType == ChangeType.Delete
            && graph.Ids.Contains(Id(item)));
    }

    /// <summary>
    /// No client deletes an answer on its own; one that arrives without its progress row is a
    /// cascade a device logged as its own (an older build, after a downloaded delete). Duotopia
    /// 2026-11-13: after a web merge moved the answers to the kept word, such deletes removed 527.
    /// </summary>
    [ApplicationOperationPostgresFact]
    public async Task AnswerDelete_IsAppliedOnlyWithItsProgressRow()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        var (kept, removed) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var keptAnswers = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var removedAnswer = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        await using (var db = _fixture.NewContext())
        {
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress { Id = kept, UserId = owner, VocabularyWordId = graph.Word,
                    FirstSeenAt = now, LastPracticedAt = now, CreatedAt = now, UpdatedAt = now },
                new VocabularyProgress { Id = removed, UserId = owner, VocabularyWordId = graph.SecondWord,
                    FirstSeenAt = now, LastPracticedAt = now, CreatedAt = now, UpdatedAt = now });
            db.VocabularyLearningContexts.AddRange(keptAnswers.Select(id => Answer(id, kept, now))
                .Append(Answer(removedAnswer, removed, now)));
            await db.SaveChangesAsync();
        }
        // The filter itself (the database's own cascade would hide a refused delete below).
        await using (var db = _fixture.NewContext())
        {
            var withProgress = await NativeOwnerSyncProvider.OwnDeletesAsync(db,
                [Delete("VocabularyLearningContext", removedAnswer), Delete("VocabularyProgress", removed)],
                owner, NullLogger.Instance, CancellationToken.None);
            Assert.Contains(withProgress, item => Id(item) == removedAnswer);
            var echo = await NativeOwnerSyncProvider.OwnDeletesAsync(db,
                [Delete("VocabularyProgress", Guid.NewGuid().ToString()), Delete("VocabularyLearningContext", keptAnswers[0])],
                owner, NullLogger.Instance, CancellationToken.None);
            Assert.DoesNotContain(echo, item => Id(item) == keptAnswers[0]);
        }

        using var client = _fixture.CreateClient();
        var phone = new Device(_fixture, client, owner);
        await phone.DownloadAsync();
        // A cascade logged on a device: answers, with a progress row the server no longer has.
        using (var response = await phone.UploadAsync(
            Delete("VocabularyProgress", Guid.NewGuid().ToString()),
            Delete("VocabularyLearningContext", keptAnswers[0]),
            Delete("VocabularyLearningContext", keptAnswers[1])))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A word's progress removed on the device: its answer goes with it.
        using (var response = await phone.UploadAsync(
            Delete("VocabularyLearningContext", removedAnswer),
            Delete("VocabularyProgress", removed)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.Equal(2, await verified.VocabularyLearningContexts.CountAsync(row => keptAnswers.Contains(row.Id)));
        Assert.False(await verified.VocabularyLearningContexts.AnyAsync(row => row.Id == removedAnswer));
        Assert.False(await verified.VocabularyProgresses.AnyAsync(row => row.Id == removed));
        Assert.True(await verified.VocabularyProgresses.AnyAsync(row => row.Id == kept));
    }

    private static VocabularyLearningContext Answer(string id, string progress, DateTime at) => new()
    {
        Id = id, VocabularyProgressId = progress, Activity = "VocabularyQuiz", InputMode = "MultipleChoice",
        WasCorrect = true, LearnedAt = at, CreatedAt = at, UpdatedAt = at
    };

    [ApplicationOperationPostgresFact]
    public async Task WebDelete_AndItsCascade_ReachTheOwnersDevices_AndNoOneElses()
    {
        var (owner, other) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        using var client = _fixture.CreateClient();
        var phone = new Device(_fixture, client, owner);
        var otherPhone = new Device(_fixture, client, other);
        await phone.DownloadAsync();
        await otherPhone.DownloadAsync();

        // The web app deletes the resource; PostgreSQL cascades its word links.
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Remove(await db.LearningResources.SingleAsync(row => row.Id == graph.Resource));
            await db.SaveChangesAsync();
            Assert.False(await db.ResourceVocabularyMappings.AnyAsync(row => row.ResourceId == graph.Resource));
        }
        Assert.Equal(owner, await LoggedOwnerAsync("LearningResource", graph.Resource));
        Assert.Equal(owner, await LoggedOwnerAsync("ResourceVocabularyMapping", graph.Mapping));

        var onPhone = await phone.DownloadAsync();
        Assert.Contains(onPhone, item => IsDelete(item, "LearningResource", graph.Resource));
        Assert.Contains(onPhone, item => IsDelete(item, "ResourceVocabularyMapping", graph.Mapping));
        Assert.DoesNotContain(await otherPhone.DownloadAsync(), item => graph.Ids.Contains(Id(item)));
    }

    [ApplicationOperationPostgresFact]
    public async Task AnotherLearnersDelete_IsLeftOut_AndTheRestOfTheUploadApplies()
    {
        var (owner, other) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        using var client = _fixture.CreateClient();
        var otherPhone = new Device(_fixture, client, other);
        await otherPhone.DownloadAsync();

        var theirNewResource = Guid.NewGuid().ToString();
        using (var response = await otherPhone.UploadAsync(
            Delete("LearningResource", graph.Resource),
            Delete("ResourceVocabularyMapping", graph.Mapping),
            Delete("MinimalPair", graph.Pair),
            Delete("VocabularyLookup", graph.Lookup),
            Resource(theirNewResource, other)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewContext();
        Assert.True(await db.LearningResources.AnyAsync(row => row.Id == graph.Resource));
        Assert.True(await db.ResourceVocabularyMappings.AnyAsync(row => row.Id == graph.Mapping));
        Assert.True(await db.MinimalPairs.AnyAsync(row => row.Id == graph.Pair));
        Assert.True(await db.VocabularyLookups.AnyAsync(row => row.Id == graph.Lookup));
        Assert.True(await db.LearningResources.AnyAsync(row =>
            row.Id == theirNewResource && row.UserProfileId == other));
        Assert.Null(await LoggedOwnerAsync("LearningResource", graph.Resource));
    }

    [ApplicationOperationPostgresFact]
    public async Task DeletesTheServerCannotProve_AreLeftOut()
    {
        var (owner, other) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        var ownerlessSkill = Guid.NewGuid().ToString();
        var sharedWord = Guid.NewGuid().ToString();
        var otherResource = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.SkillProfiles.Add(new SkillProfile { Id = ownerlessSkill });
            // The owner's word, also linked from another learner's resource.
            db.VocabularyWords.Add(new VocabularyWord { Id = sharedWord, UserProfileId = owner, TargetLanguageTerm = "밥" });
            db.LearningResources.Add(new LearningResource { Id = otherResource, UserProfileId = other });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(), ResourceId = otherResource, VocabularyWordId = sharedWord
            });
            await db.SaveChangesAsync();
        }
        using var client = _fixture.CreateClient();
        var phone = new Device(_fixture, client, owner);
        await phone.DownloadAsync();

        using (var response = await phone.UploadAsync(
            Delete("SkillProfile", ownerlessSkill),
            Delete("UserProfile", owner),
            Delete("VocabularyWord", sharedWord),
            // Deleted, then saved again on the device before this sync: the row stays.
            Delete("LearningResource", graph.Resource),
            Resource(graph.Resource, owner, "Saved again"),
            Delete("MinimalPair", Guid.NewGuid().ToString())))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.True(await verified.SkillProfiles.AnyAsync(row => row.Id == ownerlessSkill));
        Assert.True(await verified.UserProfiles.AnyAsync(row => row.Id == owner));
        Assert.True(await verified.VocabularyWords.AnyAsync(row => row.Id == sharedWord));
        Assert.True(await verified.ResourceVocabularyMappings.AnyAsync(row =>
            row.ResourceId == otherResource && row.VocabularyWordId == sharedWord));
        Assert.Equal("Saved again", (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == graph.Resource)).Title);
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnWordDelete_WithItsMinimalPairAnswers_RemovesTheAnswersFirst()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        var attempt = await SeedAttemptAsync(owner, graph.Pair, graph.Word, graph.SecondWord);
        using var client = _fixture.CreateClient();
        var phone = new Device(_fixture, client, owner);
        await phone.DownloadAsync();

        // The device sends the word first; answers reference it with ON DELETE RESTRICT.
        using (var response = await phone.UploadAsync(
            Delete("VocabularyWord", graph.Word),
            Delete("MinimalPairAttempt", attempt)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewContext();
        Assert.False(await db.VocabularyWords.AnyAsync(row => row.Id == graph.Word));
        Assert.False(await db.MinimalPairAttempts.AnyAsync(row => row.Id == attempt));
        Assert.False(await db.MinimalPairs.AnyAsync(row => row.Id == graph.Pair));
        Assert.True(await db.VocabularyWords.AnyAsync(row => row.Id == graph.SecondWord));
        Assert.Equal(owner, await LoggedOwnerAsync("VocabularyWord", graph.Word));
    }

    [ApplicationOperationPostgresFact]
    public async Task WordDelete_ThatAnAnswerStillUses_IsLeftOut_AndTheRestApplies()
    {
        var (owner, other) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        // The owner's own answer, not deleted with the word, and another learner's answer on
        // the owner's second word.
        var ownAttempt = await SeedAttemptAsync(owner, graph.Pair, graph.Word, graph.Word);
        var otherPair = SyncIds.MinimalPair(other, graph.SecondWord, graph.Word);
        await using (var db = _fixture.NewContext())
        {
            db.MinimalPairs.Add(new MinimalPair
            {
                Id = otherPair, UserId = other, VocabularyWordAId = graph.SecondWord,
                VocabularyWordBId = graph.Word, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var otherAttempt = await SeedAttemptAsync(other, otherPair, graph.SecondWord, graph.SecondWord);
        using var client = _fixture.CreateClient();
        var phone = new Device(_fixture, client, owner);
        await phone.DownloadAsync();

        var newResource = Guid.NewGuid().ToString();
        using (var response = await phone.UploadAsync(
            Delete("VocabularyWord", graph.Word),
            Delete("VocabularyWord", graph.SecondWord),
            Resource(newResource, owner)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.True(await verified.VocabularyWords.AnyAsync(row => row.Id == graph.Word));
        Assert.True(await verified.VocabularyWords.AnyAsync(row => row.Id == graph.SecondWord));
        Assert.True(await verified.MinimalPairAttempts.AnyAsync(row => row.Id == ownAttempt));
        Assert.True(await verified.MinimalPairAttempts.AnyAsync(row => row.Id == otherAttempt));
        Assert.True(await verified.LearningResources.AnyAsync(row => row.Id == newResource));
    }

    [ApplicationOperationPostgresFact]
    public async Task DeletionLog_IsReadThroughItsTableAndRowIndex()
    {
        // The download's own query. With a tiny log PostgreSQL would scan it anyway, so the scan
        // is switched off: the plan must then reach rows through the index by table (a query by
        // row ID alone can only match "RowId" across the whole index).
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await off.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(
            "EXPLAIN " + SentenceStudio.Api.Sync.NativeOwnerSyncProvider.LatestLoggedDeletionsSql, connection);
        command.Parameters.AddWithValue("table", "MinimalPair");
        command.Parameters.AddWithValue("ids", new[] { "a", "b" });
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        var text = string.Join("\n", plan);
        Assert.True(text.Contains("IX_SyncDeletion_TableName_RowId"), text);
        Assert.True(plan.Any(line => line.Contains("Index Cond") && line.Contains("\"TableName\" =")), text);
    }

    private async Task<string> SeedAttemptAsync(string owner, string pair, string prompt, string selected)
    {
        var now = DateTime.UtcNow;
        var session = Guid.NewGuid().ToString();
        var attempt = Guid.NewGuid().ToString();
        await using var db = _fixture.NewContext();
        db.MinimalPairSessions.Add(new MinimalPairSession
        {
            Id = session, UserId = owner, StartedAt = now, CreatedAt = now, UpdatedAt = now
        });
        db.MinimalPairAttempts.Add(new MinimalPairAttempt
        {
            Id = attempt, UserId = owner, SessionId = session, PairId = pair,
            PromptWordId = prompt, SelectedWordId = selected, SequenceNumber = 1, CreatedAt = now
        });
        await db.SaveChangesAsync();
        return attempt;
    }

    [ApplicationOperationPostgresFact]
    public async Task RowThatExistsAgain_IsNotDeletedOnDevices()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var graph = await SeedOwnedGraphAsync(owner);
        using var client = _fixture.CreateClient();
        var phone = new Device(_fixture, client, owner);
        await phone.DownloadAsync();

        // A fixed-ID row deleted and made again (the same minimal pair, recreated on the web).
        await using (var db = _fixture.NewContext())
        {
            var pair = await db.MinimalPairs.SingleAsync(row => row.Id == graph.Pair);
            db.MinimalPairs.Remove(pair);
            await db.SaveChangesAsync();
            db.MinimalPairs.Add(new MinimalPair
            {
                Id = graph.Pair, UserId = owner, VocabularyWordAId = graph.Word,
                VocabularyWordBId = graph.SecondWord, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var onPhone = await phone.DownloadAsync();
        Assert.DoesNotContain(onPhone, item => IsDelete(item, "MinimalPair", graph.Pair));
        Assert.Contains(onPhone, item => item.TableName == "MinimalPair"
            && item.ChangeType != ChangeType.Delete && Id(item) == graph.Pair);
    }

    [ApplicationOperationPostgresFact]
    public async Task Migration_IsDiscoveredAndApplied_LogsDeletes_AndRollsBack()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync(
            "sync_deletion_log", migrate: true);
        await using var db = database.NewContext();
        Assert.Contains("20261104162953_AddSyncDeletionLog", await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(28L, await TriggerCountAsync(database.ConnectionString));

        var owner = Guid.NewGuid().ToString();
        var resource = Guid.NewGuid().ToString();
        var word = Guid.NewGuid().ToString();
        var mapping = Guid.NewGuid().ToString();
        db.UserProfiles.Add(new UserProfile { Id = owner, Email = $"{owner}@example.test" });
        db.LearningResources.Add(new LearningResource { Id = resource, UserProfileId = owner });
        db.VocabularyWords.Add(new VocabularyWord { Id = word, TargetLanguageTerm = "물", UserProfileId = owner });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = mapping, ResourceId = resource, VocabularyWordId = word
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"LearningResource\" WHERE \"Id\" = {0}", resource);
        Assert.Equal(owner, await LoggedOwnerAsync(database.ConnectionString, "LearningResource", resource));
        Assert.Equal(owner, await LoggedOwnerAsync(database.ConnectionString, "ResourceVocabularyMapping", mapping));
        // Saving the link through EF recorded the word's owner, so its delete is logged for them.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"VocabularyWord\" WHERE \"Id\" = {0}", word);
        Assert.Equal(owner, await LoggedOwnerAsync(database.ConnectionString, "VocabularyWord", word));

        // A word saved before words had an owner takes the one learner its link names, read
        // before the cascade removes the link (written with SQL so nothing records an owner).
        var keptResource = Guid.NewGuid().ToString();
        var legacyWord = Guid.NewGuid().ToString();
        var legacyLink = Guid.NewGuid().ToString();
        db.LearningResources.Add(new LearningResource { Id = keptResource, UserProfileId = owner });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "VocabularyWord" ("Id", "TargetLanguageTerm", "CreatedAt", "UpdatedAt",
                "LexicalUnitType") VALUES ({0}, 'legacy', now(), now(), 0);
            INSERT INTO "ResourceVocabularyMapping" ("Id", "ResourceId", "VocabularyWordId")
                VALUES ({1}, {2}, {0});
            """, legacyWord, legacyLink, keptResource);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"VocabularyWord\" WHERE \"Id\" = {0}", legacyWord);
        Assert.Equal(owner, await LoggedOwnerAsync(database.ConnectionString, "VocabularyWord", legacyWord));
        Assert.Equal(owner, await LoggedOwnerAsync(database.ConnectionString, "ResourceVocabularyMapping", legacyLink));

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261104153858_AddVocabularyLookup");
        Assert.Equal(0L, await TriggerCountAsync(database.ConnectionString));
        Assert.False(await TableExistsAsync(database.ConnectionString));
        await migrator.MigrateAsync();
        Assert.Equal(28L, await TriggerCountAsync(database.ConnectionString));
        Assert.True(await TableExistsAsync(database.ConnectionString));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private async Task<OwnedGraph> SeedOwnedGraphAsync(string owner)
    {
        var graph = new OwnedGraph(owner);
        var now = DateTime.UtcNow;
        await using var db = _fixture.NewContext();
        db.LearningResources.Add(new LearningResource { Id = graph.Resource, UserProfileId = owner, Title = "Mine" });
        db.VocabularyWords.AddRange(
            new VocabularyWord { Id = graph.Word, UserProfileId = owner, TargetLanguageTerm = "물" },
            new VocabularyWord { Id = graph.SecondWord, UserProfileId = owner, TargetLanguageTerm = "불" });
        db.ResourceVocabularyMappings.AddRange(
            new ResourceVocabularyMapping { Id = graph.Mapping, ResourceId = graph.Resource, VocabularyWordId = graph.Word },
            new ResourceVocabularyMapping { Id = graph.SecondMapping, ResourceId = graph.Resource, VocabularyWordId = graph.SecondWord });
        db.MinimalPairs.Add(new MinimalPair
        {
            Id = graph.Pair, UserId = owner, VocabularyWordAId = graph.Word,
            VocabularyWordBId = graph.SecondWord, CreatedAt = now, UpdatedAt = now
        });
        db.SceneImages.Add(new SceneImage { Id = graph.Image, UserProfileId = owner, Url = "https://example.test/a.jpg" });
        db.SkillProfiles.Add(new SkillProfile { Id = graph.Skill, UserProfileId = owner });
        db.VocabularyLookups.Add(new VocabularyLookup
        {
            Id = graph.Lookup, UserProfileId = owner, Text = "물", Translation = "water",
            Language = "Korean", Activity = "Reading", FirstLookedUpAt = now, LastLookedUpAt = now
        });
        await db.SaveChangesAsync();
        return graph;
    }

    private Task<string?> LoggedOwnerAsync(string table, string id) =>
        LoggedOwnerAsync(_fixture.ConnectionString, table, id);

    private static async Task<string?> LoggedOwnerAsync(string connectionString, string table, string id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "OwnerId" FROM "SyncDeletion" WHERE "TableName" = @table AND "RowId" = @id
            ORDER BY "Id" DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<long> TriggerCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_trigger WHERE tgname = 'sync_deletion_log' AND NOT tgisinternal",
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.\"SyncDeletion\"') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static SyncItem Delete(string table, string id) =>
        new(table, ChangeType.Delete, new Dictionary<string, object?> { ["Id"] = id });

    private static SyncItem Resource(string id, string owner, string title = "New") =>
        new("LearningResource", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner, ["Title"] = title,
            ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow, ["IsSmartResource"] = false
        });

    private static string? Id(SyncItem item) =>
        item.Values.TryGetValue("Id", out var id) ? id.Value?.ToString() : null;

    private static bool IsDelete(SyncItem item, string table, string id) =>
        item.TableName == table && item.ChangeType == ChangeType.Delete && Id(item) == id;

    private sealed class OwnedGraph(string owner)
    {
        public string Owner { get; } = owner;
        public string Resource { get; } = Guid.NewGuid().ToString();
        public string Word { get; } = Guid.NewGuid().ToString();
        public string SecondWord { get; } = Guid.NewGuid().ToString();
        public string Mapping { get; } = Guid.NewGuid().ToString();
        public string SecondMapping { get; } = Guid.NewGuid().ToString();
        public string Pair => SyncIds.MinimalPair(Owner, Word, SecondWord);
        public string Image { get; } = Guid.NewGuid().ToString();
        public string Skill { get; } = Guid.NewGuid().ToString();
        public string Lookup { get; } = Guid.NewGuid().ToString();
        public IReadOnlySet<string> Ids => new HashSet<string>
            { Resource, Word, SecondWord, Mapping, SecondMapping, Pair, Image, Skill, Lookup };
    }

    /// <summary>One device: its own store, and the server version it last downloaded.</summary>
    private sealed class Device(NativeOwnerSyncPostgresFixture fixture, HttpClient client, string owner)
    {
        private readonly Guid _storeId = Guid.NewGuid();
        private SyncAnchor _seenServer = SyncAnchor.Null;

        public async Task<SyncItem[]> DownloadAsync()
        {
            var session = await fixture.StartDownloadAsync(client, owner, storeId: _storeId);
            var items = Array.Empty<SyncItem>();
            if (session.TotalChanges > 0)
            {
                using var chunk = await fixture.GetAsync(client,
                    $"/api/sync-agent/changes-bulk-item/{session.SessionId}/0/{session.TotalChanges}", owner);
                Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
                items = await chunk.Content.ReadFromJsonAsync<SyncItem[]>() ?? [];
            }
            using (var saved = await fixture.SaveVersionAsync(
                client, owner, _storeId, session.SourceAnchor.Version))
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            _seenServer = session.SourceAnchor;
            return items;
        }

        public async Task<HttpResponseMessage> UploadAsync(params SyncItem[] items)
        {
            var sessionId = Guid.NewGuid();
            using (var begin = await fixture.BeginUploadAsync(client, owner, sessionId, _storeId,
                items.Length, seenServer: _seenServer))
                Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            using (var chunk = await fixture.UploadChunkAsync(client, owner, sessionId, items))
                Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
            return await fixture.CompleteUploadAsync(client, owner, sessionId);
        }
    }
}
