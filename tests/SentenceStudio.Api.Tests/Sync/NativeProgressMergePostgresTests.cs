using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// Live two-device regression: for a word both devices had already practiced (one shared
/// VocabularyProgress Id), each device added one attempt offline. Last-writer-wins left the
/// server at 2 attempts instead of 3 while all 3 learning contexts survived.
/// </summary>
public sealed class NativeProgressMergePostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private static readonly DateTime T0 = new(2026, 10, 31, 9, 0, 0);
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public NativeProgressMergePostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task TwoDevicesOffline_OnExistingProgress_KeepEveryAttemptOnce() =>
        await TwoDevicesOfflineAsync(legacyAttempts: 0);

    [ApplicationOperationPostgresFact]
    public async Task TwoDevicesOffline_OnLegacyProgress_KeepPreContextHistory() =>
        await TwoDevicesOfflineAsync(legacyAttempts: 9);

    private async Task TwoDevicesOfflineAsync(int legacyAttempts)
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var seeded = await SeedPracticedWordAsync(owner, legacyAttempts);
        using var client = _fixture.CreateClient();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();

        // Both devices start from the synced row; B has downloaded everything so far.
        var baseline = await _fixture.StartDownloadAsync(client, owner, storeId: deviceB);
        using (var saved = await _fixture.SaveVersionAsync(
            client, owner, deviceB, baseline.SourceAnchor.Version))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var a = OfflineAttempt(seeded, T0.AddMinutes(10), correct: true);
        var b = OfflineAttempt(seeded, T0.AddMinutes(20), correct: false);

        await UploadFromAsync(client, deviceA, owner, a.Progress, a.Context);
        await AssertServerAsync(seeded.ProgressId, legacyAttempts + 2, legacyAttempts + 2, 2);

        // B downloads A's row while its own attempt is pending (the native client keeps its
        // pending row on that conflict), so B's anchor now covers A's change.
        var afterA = await _fixture.StartDownloadAsync(client, owner, storeId: deviceB);
        Assert.Contains(await ItemsAsync(client, owner, afterA), item =>
            item.TableName == "VocabularyProgress" && Id(item) == seeded.ProgressId);
        using (var saved = await _fixture.SaveVersionAsync(
            client, owner, deviceB, afterA.SourceAnchor.Version))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        await UploadFromAsync(client, deviceB, owner, b.Progress, b.Context);
        await AssertServerAsync(seeded.ProgressId, legacyAttempts + 3, legacyAttempts + 2, 3);

        // A lost response makes B re-send the same change set: nothing is counted twice.
        await UploadFromAsync(client, deviceB, owner,
            Clone(b.Progress), Clone(b.Context));
        await AssertServerAsync(seeded.ProgressId, legacyAttempts + 3, legacyAttempts + 2, 3);

        // B receives the merged row in the same sync, although the merged write is its own.
        var afterB = await _fixture.StartDownloadAsync(client, owner, storeId: deviceB);
        var mergedRow = Assert.Single(await ItemsAsync(client, owner, afterB), item =>
            item.TableName == "VocabularyProgress" && Id(item) == seeded.ProgressId);
        Assert.Equal((legacyAttempts + 3).ToString(CultureInfo.InvariantCulture),
            mergedRow.Values["TotalAttempts"].Value?.ToString());

        // A later attempt from A, made on top of the merged row, is applied as-is.
        await using (var db = _fixture.NewContext())
        {
            var current = await db.VocabularyProgresses.AsNoTracking()
                .SingleAsync(row => row.Id == seeded.ProgressId);
            var next = OfflineAttempt(seeded with { Row = current }, T0.AddMinutes(30), correct: true);
            await UploadFromAsync(client, deviceA, owner, next.Progress, next.Context);
        }
        await AssertServerAsync(seeded.ProgressId, legacyAttempts + 4, legacyAttempts + 3, 4);
    }

    [ApplicationOperationPostgresFact]
    public async Task WebAttemptThenOfflineDeviceAttempt_BothAreKept()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var seeded = await SeedPracticedWordAsync(owner, legacyAttempts: 0);
        var device = OfflineAttempt(seeded, T0.AddMinutes(20), correct: true);

        // The web app writes the stored row and its context directly.
        await using (var db = _fixture.NewContext())
        {
            var row = await db.VocabularyProgresses.SingleAsync(item => item.Id == seeded.ProgressId);
            var attempt = Attempt(seeded, correct: true);
            VocabularyProgressService.ApplyAttemptScoring(row, attempt, T0.AddMinutes(10));
            db.VocabularyLearningContexts.Add(VocabularyProgressService.CreateLearningContext(
                row.Id, attempt, T0.AddMinutes(10)));
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClient();
        await UploadFromAsync(client, Guid.NewGuid(), owner, device.Progress, device.Context);
        await AssertServerAsync(seeded.ProgressId, 3, 3, 3);
    }

    private async Task AssertServerAsync(string progressId, int total, int correct, int contexts)
    {
        await using var db = _fixture.NewContext();
        var row = await db.VocabularyProgresses.AsNoTracking().SingleAsync(item => item.Id == progressId);
        Assert.Equal(total, row.TotalAttempts);
        Assert.Equal(correct, row.CorrectAttempts);
#pragma warning disable CS0618
        Assert.Equal(total, row.RecognitionAttempts);
        Assert.Equal(correct, row.MultipleChoiceCorrect);
#pragma warning restore CS0618
        Assert.Equal(correct, row.QuizRecognitionDemonstrations);
        Assert.Equal(contexts, await db.VocabularyLearningContexts.AsNoTracking()
            .CountAsync(context => context.VocabularyProgressId == progressId));
        Assert.Equal(1, await db.VocabularyProgresses.AsNoTracking()
            .CountAsync(item => item.VocabularyWordId == row.VocabularyWordId && item.UserId == row.UserId));
    }

    private async Task<Seeded> SeedPracticedWordAsync(string owner, int legacyAttempts)
    {
        var seeded = new Seeded(owner, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(), null!);
        var attempt = Attempt(seeded, correct: true);
        var row = new VocabularyProgress
        {
            Id = seeded.ProgressId, UserId = owner, VocabularyWordId = seeded.WordId,
            FirstSeenAt = T0, CreatedAt = T0, LastPracticedAt = T0, UpdatedAt = T0,
            // History that predates per-attempt contexts.
            TotalAttempts = legacyAttempts, CorrectAttempts = legacyAttempts,
#pragma warning disable CS0618
            RecognitionAttempts = legacyAttempts, MultipleChoiceCorrect = legacyAttempts,
#pragma warning restore CS0618
            QuizRecognitionDemonstrations = legacyAttempts
        };
        VocabularyProgressService.ApplyAttemptScoring(row, attempt, T0);
        await using var db = _fixture.NewContext();
        db.LearningResources.Add(new LearningResource { Id = seeded.ResourceId, UserProfileId = owner });
        db.VocabularyWords.Add(new VocabularyWord { Id = seeded.WordId, TargetLanguageTerm = "반찬" });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(), ResourceId = seeded.ResourceId, VocabularyWordId = seeded.WordId
        });
        db.VocabularyProgresses.Add(row);
        db.VocabularyLearningContexts.Add(
            VocabularyProgressService.CreateLearningContext(row.Id, attempt, T0));
        await db.SaveChangesAsync();
        return seeded with { Row = VocabularyProgressConcurrentMergeCopy(row) };
    }

    /// <summary>Exactly what the native writer does: score the device's copy and add one context.</summary>
    private static (SyncItem Progress, SyncItem Context) OfflineAttempt(
        Seeded seeded, DateTime at, bool correct)
    {
        var row = VocabularyProgressConcurrentMergeCopy(seeded.Row);
        var attempt = Attempt(seeded, correct);
        VocabularyProgressService.ApplyAttemptScoring(row, attempt, at);
        var context = VocabularyProgressService.CreateLearningContext(row.Id, attempt, at);
        context.CreatedAt = context.UpdatedAt = at;
        return (ToItem("VocabularyProgress", ChangeType.Update, row),
            ToItem("VocabularyLearningContext", ChangeType.Insert, context));
    }

    private static VocabularyAttempt Attempt(Seeded seeded, bool correct) => new()
    {
        VocabularyWordId = seeded.WordId, UserId = seeded.Owner, Activity = "VocabularyQuiz",
        InputMode = "MultipleChoice", ContextType = "Isolated", WasCorrect = correct,
        DifficultyWeight = 1, LearningResourceId = seeded.ResourceId, ResponseTimeMs = 900
    };

    private static VocabularyProgress VocabularyProgressConcurrentMergeCopy(VocabularyProgress row)
    {
        var copy = new VocabularyProgress();
        foreach (var property in typeof(VocabularyProgress).GetProperties()
            .Where(property => property.CanWrite
                && (property.PropertyType.IsValueType || property.PropertyType == typeof(string))))
            property.SetValue(copy, property.GetValue(row));
        return copy;
    }

    /// <summary>Uploads like the native SQLite client: zone-less date text, every column.</summary>
    private static SyncItem ToItem(string table, ChangeType type, object row)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in row.GetType().GetProperties()
            .Where(property => property.CanWrite
                && (property.PropertyType.IsValueType || property.PropertyType == typeof(string))))
        {
            values[property.Name] = property.GetValue(row) switch
            {
                DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                Enum value => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                var value => value
            };
        }
        return new SyncItem(table, type, values);
    }

    private static SyncItem Clone(SyncItem item) => new(item.TableName, item.ChangeType,
        item.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Value));

    private async Task UploadFromAsync(
        HttpClient client, Guid store, string owner, params SyncItem[] items)
    {
        var session = Guid.NewGuid();
        using (var begin = await _fixture.BeginUploadAsync(client, owner, session, store, items.Length))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var chunk = await _fixture.UploadChunkAsync(client, owner, session, items))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using var complete = await _fixture.CompleteUploadAsync(client, owner, session);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    private async Task<SyncItem[]> ItemsAsync(
        HttpClient client, string owner, CoreSync.Http.BulkSyncChangeSet download)
    {
        using var chunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item/{download.SessionId}/0/1000", owner);
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        return await chunk.Content.ReadFromJsonAsync<SyncItem[]>() ?? [];
    }

    private static string? Id(SyncItem item) => item.Values["Id"].Value?.ToString();

    private sealed record Seeded(
        string Owner, string WordId, string ProgressId, string ResourceId, VocabularyProgress Row);
}
