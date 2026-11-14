using CoreSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;
using static SentenceStudio.Native.Tests.NativeConcurrentProgressMergeTests;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// A delete a device downloads must not come back as the device's own. CoreSync.Sqlite 0.1.129
/// marked only the newest change-log entry of an applied row as the server's, and SQLite logs a
/// delete's foreign-key cascade before it, so the cascade uploaded as this device's deletes.
/// Duotopia 2026-11-13: a duplicate merge on the web moved a word's answers to the kept word and
/// deleted the duplicate; the phone's next sync deleted those answers on the server
/// (docs/progress-count-repair-2026-11-13.md). CoreSync.Sqlite 0.1.129-local4 (upstream 429c330)
/// marks every entry an apply writes. SQLite to SQLite, as the app syncs.
/// </summary>
public sealed class NativeSyncCascadeEchoTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Resource = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Kept = "dddddddd-dddd-dddd-dddd-dddddddddd01";
    private const string Duplicate = "dddddddd-dddd-dddd-dddd-dddddddddd02";
    private const string Other = "dddddddd-dddd-dddd-dddd-dddddddddd03";
    private const string KeptProgress = "ffffffff-ffff-ffff-ffff-ffffffffff01";
    private const string DuplicateProgress = "ffffffff-ffff-ffff-ffff-ffffffffff02";
    private const string OtherProgress = "ffffffff-ffff-ffff-ffff-ffffffffff03";
    private static readonly string[] MovedAnswers =
        ["eeeeeeee-eeee-eeee-eeee-eeeeeeeeee01", "eeeeeeee-eeee-eeee-eeee-eeeeeeeeee02"];
    private const string KeptAnswer = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeee03";
    private const string OtherAnswer = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeee04";
    private static readonly DateTime T0 = new(2026, 11, 13, 9, 0, 0);

    [Fact]
    public async Task AMergeOnTheServer_DeletesNoMovedAnswer_WhenADeviceDownloadsIt()
    {
        await using var server = await Store.CreateAsync(ProviderMode.Remote);
        await SeedServerAsync(server);
        await using var phone = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
        var device = NativeConcurrentProgressMergeTests.Native(phone, server.Provider);
        await SyncLikeAppAsync(device, server.Provider);
        Assert.Equal(4, await AnswersAsync(phone));

        await MergeOnServerAsync(server);
        // The download applies the merge; the next sync is the one that uploaded the cascade.
        await SyncLikeAppAsync(device, server.Provider);
        await SyncLikeAppAsync(device, server.Provider);

        foreach (var store in new[] { server, phone })
        {
            await using var db = store.OpenContext();
            var answers = await db.VocabularyLearningContexts.AsNoTracking()
                .ToDictionaryAsync(context => context.Id, context => context.VocabularyProgressId);
            Assert.Equal(KeptProgress, answers[KeptAnswer]);
            foreach (var moved in MovedAnswers)
                Assert.Equal(KeptProgress, answers[moved]);
            Assert.Equal(OtherProgress, answers[OtherAnswer]);
        }
        // Nothing of the download waits to upload.
        var pending = await device.GetChangesAsync(await server.Provider.GetStoreIdAsync(),
            syncDirection: SyncDirection.UploadOnly);
        Assert.DoesNotContain(pending.Items, item => item.ChangeType == ChangeType.Delete);
    }

    [Fact]
    public async Task AWordDeletedOnTheDevice_StillDeletesItsProgressAndAnswersOnTheServer()
    {
        await using var server = await Store.CreateAsync(ProviderMode.Remote);
        await SeedServerAsync(server);
        await using var phone = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
        var device = NativeConcurrentProgressMergeTests.Native(phone, server.Provider);
        await SyncLikeAppAsync(device, server.Provider);

        // Only a download's own writes are marked: the cascade of a word the learner removes
        // afterwards is the learner's and uploads (the server's cascade would hide a lost upload,
        // so the device's pending upload is checked first).
        await MergeOnServerAsync(server);
        await SyncLikeAppAsync(device, server.Provider);
        await using (var db = phone.OpenContext())
        {
            db.VocabularyWords.Remove(await db.VocabularyWords.SingleAsync(word => word.Id == Other));
            await db.SaveChangesAsync();
        }
        var pending = await device.GetChangesAsync(await server.Provider.GetStoreIdAsync(),
            syncDirection: SyncDirection.UploadOnly);
        foreach (var (table, id) in new[] { ("VocabularyWord", Other), ("VocabularyProgress", OtherProgress),
                     ("VocabularyLearningContext", OtherAnswer) })
            Assert.Contains(pending.Items, item => item.TableName == table && item.ChangeType == ChangeType.Delete
                && item.Values["Id"].Value?.ToString() == id);
        await SyncLikeAppAsync(device, server.Provider);

        await using var verified = server.OpenContext();
        Assert.False(await verified.VocabularyWords.AnyAsync(word => word.Id == Other));
        Assert.False(await verified.VocabularyProgresses.AnyAsync(row => row.Id == OtherProgress));
        Assert.False(await verified.VocabularyLearningContexts.AnyAsync(context => context.Id == OtherAnswer));
        Assert.True(await verified.VocabularyLearningContexts.AnyAsync(context => context.Id == KeptAnswer));
    }

    [Fact]
    public async Task ASyncStoppedRightAfterTheDownload_StillUploadsNoCascade()
    {
        // The marks are written inside CoreSync's apply transaction: a sync cancelled (or an app
        // stopped) after the download committed leaves nothing for the next upload.
        await using var server = await Store.CreateAsync(ProviderMode.Remote);
        await SeedServerAsync(server);
        await using var phone = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
        var device = NativeConcurrentProgressMergeTests.Native(phone, server.Provider);
        await SyncLikeAppAsync(device, server.Provider);
        await MergeOnServerAsync(server);

        var stopped = new NativeLocalOnlyMappingExportProvider(phone.Provider, server.Provider, () => Owner,
            NullLogger<NativeLocalOnlyMappingExportProvider>.Instance,
            applyLocalOverride: async (changes, resolve, ct) =>
            {
                await phone.Provider.ApplyChangesAsync(changes, resolve, ct);
                throw new OperationCanceledException("Stopped after the download committed.");
            });
        var stop = await Assert.ThrowsAsync<SynchronizationException>(() => SyncLikeAppAsync(stopped, server.Provider));
        Assert.IsType<OperationCanceledException>(stop.InnerException);
        await using (var db = phone.OpenContext())
            Assert.False(await db.VocabularyProgresses.AnyAsync(row => row.Id == DuplicateProgress));

        var pending = await device.GetChangesAsync(await server.Provider.GetStoreIdAsync(),
            syncDirection: SyncDirection.UploadOnly);
        Assert.DoesNotContain(pending.Items, item => item.ChangeType == ChangeType.Delete);
        await SyncLikeAppAsync(device, server.Provider);
        await using (var db = server.OpenContext())
            Assert.Equal(2, await db.VocabularyLearningContexts.CountAsync(context => MovedAnswers.Contains(context.Id)));
    }

    private static async Task<int> AnswersAsync(Store store)
    {
        await using var db = store.OpenContext();
        return await db.VocabularyLearningContexts.CountAsync();
    }

    /// <summary>
    /// The web merge's history move (LearningResourceRepository): answers re-parented to the kept
    /// word's progress, then the duplicate's progress and word removed.
    /// </summary>
    private static async Task MergeOnServerAsync(Store server)
    {
        await using var db = server.OpenContext();
        foreach (var context in await db.VocabularyLearningContexts
                     .Where(context => context.VocabularyProgressId == DuplicateProgress).ToListAsync())
        {
            context.VocabularyProgressId = KeptProgress;
            context.UpdatedAt = T0.AddHours(1);
        }
        await db.SaveChangesAsync();
        db.VocabularyProgresses.Remove(await db.VocabularyProgresses.SingleAsync(row => row.Id == DuplicateProgress));
        db.VocabularyWords.Remove(await db.VocabularyWords.SingleAsync(word => word.Id == Duplicate));
        await db.SaveChangesAsync();
    }

    private static async Task SeedServerAsync(Store server)
    {
        await using var db = server.OpenContext();
        db.UserProfiles.Add(new UserProfile { Id = Owner, Email = "owner@example.test" });
        db.LearningResources.Add(new LearningResource { Id = Resource, UserProfileId = Owner, Title = "Mine" });
        foreach (var (word, term, progress) in new[]
                 { (Kept, "먹다", KeptProgress), (Duplicate, "먹다", DuplicateProgress), (Other, "마시다", OtherProgress) })
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = word, UserProfileId = Owner, TargetLanguageTerm = term, NativeLanguageTerm = "to eat"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(), ResourceId = Resource, VocabularyWordId = word
            });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = progress, UserId = Owner, VocabularyWordId = word, TotalAttempts = 1, CorrectAttempts = 1,
                FirstSeenAt = T0, CreatedAt = T0, LastPracticedAt = T0, UpdatedAt = T0
            });
        }
        db.VocabularyLearningContexts.AddRange(
            Answer(KeptAnswer, KeptProgress), Answer(MovedAnswers[0], DuplicateProgress),
            Answer(MovedAnswers[1], DuplicateProgress), Answer(OtherAnswer, OtherProgress));
        await db.SaveChangesAsync();
    }

    private static VocabularyLearningContext Answer(string id, string progress) => new()
    {
        Id = id, VocabularyProgressId = progress, LearningResourceId = Resource, Activity = "VocabularyQuiz",
        InputMode = "MultipleChoice", WasCorrect = true, LearnedAt = T0, CreatedAt = T0, UpdatedAt = T0
    };
}
