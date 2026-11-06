using CoreSync;
using CoreSync.Http.Client;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeLocalOnlyMappingExportProviderTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OtherOwner = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Resource = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Word = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const string ServerMapping = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";

    [Fact]
    public async Task OnlyADownloadThatAppliedRowsMovesTheSyncDownloadCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var downloads = new NativeSyncDownloads();
        var native = new NativeLocalOnlyMappingExportProvider(
            fixture.Local.Provider, fixture.Server, () => Owner,
            NullLogger<NativeLocalOnlyMappingExportProvider>.Instance, downloads: downloads);

        // A resource made on another device downloads: the count moves.
        await using (var db = fixture.Remote.OpenContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = Guid.NewGuid().ToString(), UserProfileId = Owner, Title = "From the web"
            });
            await db.SaveChangesAsync();
        }
        await fixture.SynchronizeLikeAppAsync(native);
        var afterDownload = downloads.Generation;
        Assert.True(afterDownload > 0);

        // An empty download applies nothing: readers keep their rows.
        var empty = new NativeLocalOnlyMappingExportProvider(
            fixture.Local.Provider, fixture.Server, () => Owner,
            NullLogger<NativeLocalOnlyMappingExportProvider>.Instance,
            (changes, _, _) => Task.FromResult(changes.TargetAnchor), downloads);
        await empty.ApplyChangesAsync(new SyncChangeSet(new SyncAnchor(), new SyncAnchor(), []));
        Assert.Equal(afterDownload, downloads.Generation);
    }

    [Fact]
    public async Task LocalImportUndo_OriginalDeleteIsRejected_OnlyGhostDeletesAreCoalesced()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ghost1 = Guid.NewGuid().ToString();
        var ghost2 = Guid.NewGuid().ToString();
        var newResource = Guid.NewGuid().ToString();
        var liveMapping = Guid.NewGuid().ToString();
        var baselineAnchor = await fixture.LocalAnchorAsync();

        await fixture.InsertMappingAsync(ghost1);
        await fixture.InsertMappingAsync(ghost2);
        await fixture.DeleteMappingAsync(ghost1);
        await fixture.DeleteMappingAsync(ghost2);
        await using (var db = fixture.Local.OpenContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = newResource, UserProfileId = Owner, Title = "New resource"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = liveMapping, ResourceId = newResource, VocabularyWordId = Word
            });
            await db.SaveChangesAsync();
        }

        var original = await fixture.Local.Provider.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Equal([ghost1, ghost2],
            original.Items.Where(IsMappingDelete)
                .Select(item => item.Values["Id"].Value?.ToString()));

        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Local.Provider, fixture.Server).SynchronizeAsync());
        Assert.Equal(baselineAnchor, await fixture.LocalAnchorAsync());
        await using (var db = fixture.Remote.OpenContext())
            Assert.False(await db.LearningResources.AnyAsync(row => row.Id == newResource));

        var exported = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.DoesNotContain(exported.Items, IsMappingDelete);
        Assert.Contains(exported.Items, item => item.TableName == "LearningResource"
            && item.Values["Id"].Value?.ToString() == newResource);
        Assert.Contains(exported.Items, item => item.TableName == "ResourceVocabularyMapping"
            && item.ChangeType == ChangeType.Insert
            && item.Values["Id"].Value?.ToString() == liveMapping);
        Assert.Equal(original.SourceAnchor.Version, exported.SourceAnchor.Version);

        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        Assert.Equal(exported.SourceAnchor.Version, await fixture.LocalAnchorAsync());
        await using var remote = fixture.Remote.OpenContext();
        Assert.True(await remote.LearningResources.AnyAsync(row =>
            row.Id == newResource && row.UserProfileId == Owner));
        Assert.True(await remote.ResourceVocabularyMappings.AnyAsync(row =>
            row.Id == liveMapping && row.ResourceId == newResource));
        Assert.False(await remote.ResourceVocabularyMappings.AnyAsync(row =>
            row.Id == ghost1 || row.Id == ghost2));
    }

    [Fact]
    public async Task LaterCycles_AdvanceOnlyOnSuccessfulUpload_AndPreserveExistingDeletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(first);
        await fixture.DeleteMappingAsync(first);
        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        var firstAnchor = await fixture.LocalAnchorAsync();

        var second = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(second);
        await fixture.DeleteMappingAsync(second);
        fixture.Server.FailNextUpload = true;
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync());
        Assert.Equal(firstAnchor, await fixture.LocalAnchorAsync());

        var retry = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.DoesNotContain(retry.Items, IsMappingDelete);
        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        Assert.Equal(retry.SourceAnchor.Version, await fixture.LocalAnchorAsync());
        Assert.True(await fixture.LocalAnchorAsync() > firstAnchor);

        var alreadyUploaded = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(alreadyUploaded);
        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        await fixture.DeleteMappingAsync(alreadyUploaded);
        var later = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(later.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == alreadyUploaded);
    }

    [Fact]
    public async Task CommittedRemoteInsertWithoutLocalAnchorSave_CannotBecomeLocalOnlyUndo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(id);
        var before = await fixture.LocalAnchorAsync();
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(new FailLocalSave(fixture.Native), fixture.Server)
                .SynchronizeAsync());
        Assert.Equal(before, await fixture.LocalAnchorAsync());
        await using (var remote = fixture.Remote.OpenContext())
            Assert.True(await remote.ResourceVocabularyMappings.AnyAsync(row => row.Id == id));

        await fixture.DeleteMappingAsync(id);
        var exported = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(exported.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == id);
    }

    [Fact]
    public async Task DownloadedServerMappingDeletion_RemainsQueuedAndServerRowIntact()
    {
        await using var fixture = await Fixture.CreateAsync(seedRemoteMapping: true);
        var anchor = await fixture.LocalAnchorAsync();
        await fixture.DeleteMappingAsync(ServerMapping);

        var exported = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(exported.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == ServerMapping);
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync());
        Assert.Equal(anchor, await fixture.LocalAnchorAsync());
        await using var remote = fixture.Remote.OpenContext();
        Assert.True(await remote.ResourceVocabularyMappings.AnyAsync(row =>
            row.Id == ServerMapping));
    }

    [Fact]
    public async Task ForeignResourcesOrAmbiguousMappingHistory_DoNotSilentlyDiscardDeletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reused = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(reused);
        await fixture.DeleteMappingAsync(reused);
        await fixture.InsertMappingAsync(reused);
        await fixture.DeleteMappingAsync(reused);
        var ambiguous = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(ambiguous.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == reused);

        var foreignResourceId = Guid.NewGuid().ToString();
        await using (var db = fixture.Local.OpenContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = foreignResourceId, UserProfileId = OtherOwner
            });
            await db.SaveChangesAsync();
        }
        var wrongOwner = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(wrongOwner, foreignResourceId);
        await fixture.DeleteMappingAsync(wrongOwner);
        var foreignParentPossible = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(foreignParentPossible.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == wrongOwner);
    }

    [Fact]
    public async Task MissingOwnerOrRemoteReceipt_NeverAcknowledgesUnrelatedPendingResource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString();
        var newResource = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(id);
        await fixture.DeleteMappingAsync(id);
        await using (var db = fixture.Local.OpenContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = newResource, UserProfileId = Owner
            });
            await db.SaveChangesAsync();
        }

        var noOwner = new NativeLocalOnlyMappingExportProvider(
            fixture.Local.Provider, fixture.Server, () => null,
            NullLogger<NativeLocalOnlyMappingExportProvider>.Instance);
        var unchanged = await noOwner.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(unchanged.Items, IsMappingDelete);

        var anchor = await fixture.LocalAnchorAsync();
        fixture.Server.FailNextPreflight = true;
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync());
        Assert.Equal(anchor, await fixture.LocalAnchorAsync());
        await using (var db = fixture.Remote.OpenContext())
            Assert.False(await db.LearningResources.AnyAsync(row => row.Id == newResource));
    }

    [Fact]
    public async Task ConcurrentFirstProgress_AdoptsServerId_AndKeepsBothDevicesHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var serverProgress = Guid.NewGuid().ToString();
        var serverContext = Guid.NewGuid().ToString();
        var localProgress = Guid.NewGuid().ToString();
        var localContext = Guid.NewGuid().ToString();
        var t0 = new DateTime(2026, 10, 31, 14, 53, 35);
        await using (var db = fixture.Remote.OpenContext())
        {
            db.VocabularyProgresses.Add(Progress(serverProgress, Owner, t0));
            db.VocabularyLearningContexts.Add(Attempt(serverContext, serverProgress, t0));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Local.OpenContext())
        {
            db.VocabularyProgresses.Add(Progress(localProgress, Owner, t0.AddMinutes(2)));
            db.VocabularyLearningContexts.Add(Attempt(localContext, localProgress, t0.AddMinutes(2)));
            await db.SaveChangesAsync();
        }

        // Upload: the server already holds (word, owner), so it drops the device row.
        await fixture.SynchronizeLikeAppAsync();
        await using (var db = fixture.Local.OpenContext())
        {
            var row = await db.VocabularyProgresses.SingleAsync();
            Assert.Equal(serverProgress, row.Id);
            Assert.Equal(2, row.TotalAttempts);
            Assert.Equal(2, row.QuizRecognitionDemonstrations);
            Assert.Equal(t0, row.FirstSeenAt);
            Assert.Equal(
                new[] { localContext, serverContext }.Order(),
                (await db.VocabularyLearningContexts
                    .Where(context => context.VocabularyProgressId == serverProgress)
                    .Select(context => context.Id).ToListAsync()).Order());
        }

        await fixture.SynchronizeLikeAppAsync();
        await using (var db = fixture.Remote.OpenContext())
        {
            var row = await db.VocabularyProgresses.SingleAsync();
            Assert.Equal(serverProgress, row.Id);
            Assert.Equal(2, row.TotalAttempts);
            Assert.Equal(2, await db.VocabularyLearningContexts
                .CountAsync(context => context.VocabularyProgressId == serverProgress));
        }
    }

    [Fact]
    public async Task ConcurrentFirstProgress_OneAppSync_ConvergesBothSides()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await SeedConcurrentFirstProgressAsync(fixture);
        var remote = new HttpRemote(fixture.Server);
        using var services = new ServiceCollection().BuildServiceProvider();
        var sync = new SyncService(fixture.Native, remote,
            NullLogger<SyncService>.Instance, services);

        // The download adopts the server Id; the same readiness run then uploads the merge.
        await sync.SynchronizeForReadinessAsync(CancellationToken.None);
        await AssertConvergedAsync(fixture, ids);
        Assert.Equal(2, remote.Passes);

        // Nothing merged: one pass, and the converged rows stay as they are.
        remote.Passes = 0;
        await sync.SynchronizeForReadinessAsync(CancellationToken.None);
        Assert.Equal(1, remote.Passes);
        await AssertConvergedAsync(fixture, ids);
    }

    [Fact]
    public async Task FollowUpPass_IsBounded_EvenWhenEveryDownloadAsksForAnother()
    {
        await using var fixture = await Fixture.CreateAsync();
        var remote = new HttpRemote(fixture.Server);
        using var services = new ServiceCollection().BuildServiceProvider();
        var sync = new SyncService(new AlwaysFollowUp(fixture.Native), remote,
            NullLogger<SyncService>.Instance, services);

        await sync.SynchronizeForReadinessAsync(CancellationToken.None);
        Assert.Equal(2, remote.Passes);
        remote.Passes = 0;
        await sync.TriggerSyncAsync();
        Assert.Equal(2, remote.Passes);
    }

    [Fact]
    public async Task OtherOwnersProgress_IsNeverAdopted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var localProgress = Guid.NewGuid().ToString();
        var t0 = new DateTime(2026, 10, 31, 14, 53, 35);
        await using (var db = fixture.Remote.OpenContext())
        {
            db.UserProfiles.Add(new UserProfile { Id = OtherOwner, Email = "other@example.test" });
            db.VocabularyProgresses.Add(Progress(Guid.NewGuid().ToString(), OtherOwner, t0));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Local.OpenContext())
        {
            db.VocabularyProgresses.Add(Progress(localProgress, Owner, t0));
            await db.SaveChangesAsync();
        }

        await fixture.SynchronizeLikeAppAsync();
        await using (var db = fixture.Local.OpenContext())
        {
            var mine = await db.VocabularyProgresses.SingleAsync(row => row.UserId == Owner);
            Assert.Equal(localProgress, mine.Id);
            Assert.Equal(1, mine.TotalAttempts);
        }
    }

    [Fact]
    public async Task ConcurrentFirstProgress_CancelledInsideApply_NextSyncKeepsMergedHistoryOnBothSides()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await SeedConcurrentFirstProgressAsync(fixture);
        var conflicts = new List<ConflictResolution>();
        // Cancel inside the CoreSync apply transaction, right after it resolved the
        // adopted row's conflict (for example the user navigated away mid-sync).
        var interrupted = fixture.NativeWith((changes, onConflict, ct) =>
            fixture.Local.Provider.ApplyChangesAsync(changes, item =>
            {
                var resolution = onConflict!(item);
                conflicts.Add(resolution);
                throw new OperationCanceledException("Sync cancelled mid-apply.");
            }, ct));

        var failure = await Assert.ThrowsAsync<SynchronizationException>(() =>
            fixture.SynchronizeLikeAppAsync(interrupted));
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal([ConflictResolution.Skip], conflicts);
        await AssertMergedLocalRowAsync(fixture, ids, expectServerContext: false);

        await fixture.SynchronizeLikeAppAsync();
        await AssertConvergedAsync(fixture, ids);
        // A further round trip neither double counts nor loses anything.
        await fixture.SynchronizeLikeAppAsync();
        await AssertConvergedAsync(fixture, ids);
    }

    [Fact]
    public async Task ConcurrentFirstProgress_InterruptedBeforeApply_LeavesCompleteMergedRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await SeedConcurrentFirstProgressAsync(fixture);
        var applied = false;
        var interrupted = fixture.NativeWith((_, _, _) =>
        {
            applied = true;
            throw new OperationCanceledException("Sync cancelled before apply.");
        });

        await Assert.ThrowsAsync<SynchronizationException>(() =>
            fixture.SynchronizeLikeAppAsync(interrupted));
        Assert.True(applied);
        // No half state: the rename never exists without the folded history.
        await AssertMergedLocalRowAsync(fixture, ids, expectServerContext: false);
        await using (var db = fixture.Remote.OpenContext())
        {
            var server = await db.VocabularyProgresses.SingleAsync();
            Assert.Equal(1, server.TotalAttempts);
        }

        // The merged row uploads first; the server keeps the other device's history too.
        await fixture.SynchronizeLikeAppAsync();
        await AssertConvergedAsync(fixture, ids);
    }

    [Fact]
    public async Task NonCollidingProgressDownload_IsAppliedUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var serverProgress = Guid.NewGuid().ToString();
        var t0 = new DateTime(2026, 10, 31, 14, 53, 35).AddTicks(1234567);
        VocabularyProgress expected;
        await using (var db = fixture.Remote.OpenContext())
        {
            expected = Progress(serverProgress, Owner, t0);
            db.VocabularyProgresses.Add(expected);
            db.VocabularyLearningContexts.Add(Attempt(Guid.NewGuid().ToString(), serverProgress, t0));
            await db.SaveChangesAsync();
        }

        await fixture.SynchronizeLikeAppAsync();
        await using var local = fixture.Local.OpenContext();
        var row = await local.VocabularyProgresses.SingleAsync();
        Assert.Equal(serverProgress, row.Id);
        Assert.Equal(expected.TotalAttempts, row.TotalAttempts);
        Assert.Equal(expected.MasteryScore, row.MasteryScore);
        Assert.Equal(expected.NextReviewDate, row.NextReviewDate);
        Assert.Equal(expected.FirstSeenAt, row.FirstSeenAt);
        Assert.Equal(expected.UpdatedAt, row.UpdatedAt);
        Assert.Equal(1, await local.VocabularyLearningContexts.CountAsync());
    }

    [Fact]
    public async Task ForeignOwnerCollision_IsNeverMerged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var t0 = new DateTime(2026, 10, 31, 14, 53, 35);
        await using (var db = fixture.Remote.OpenContext())
        {
            db.UserProfiles.Add(new UserProfile { Id = OtherOwner, Email = "other@example.test" });
            await db.SaveChangesAsync();
        }
        await fixture.SynchronizeLikeAppAsync();

        var foreignLocal = Guid.NewGuid().ToString();
        var foreignServer = Guid.NewGuid().ToString();
        await using (var db = fixture.Local.OpenContext())
        {
            db.VocabularyProgresses.Add(Progress(foreignLocal, OtherOwner, t0));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Remote.OpenContext())
        {
            db.VocabularyProgresses.Add(Progress(foreignServer, OtherOwner, t0.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        await fixture.SynchronizeLikeAppAsync();
        await using (var db = fixture.Local.OpenContext())
        {
            var row = await db.VocabularyProgresses.SingleAsync();
            Assert.Equal(foreignLocal, row.Id);
            Assert.Equal(1, row.TotalAttempts);
            Assert.Equal(t0, row.UpdatedAt);
        }
        await using (var db = fixture.Remote.OpenContext())
        {
            var row = await db.VocabularyProgresses.SingleAsync();
            Assert.Equal(foreignServer, row.Id);
            Assert.Equal(1, row.TotalAttempts);
            Assert.Equal(t0.AddMinutes(1), row.UpdatedAt);
        }
    }

    [Fact]
    public void DownloadedDateTimes_ConvertExactly()
    {
        var ticks = new DateTime(2026, 10, 31, 14, 53, 35).AddTicks(1234567).Ticks;

        var unspecified = (DateTime)NativeProgressIdentityReconciler.ConvertValue(
            "2026-10-31 14:53:35.1234567", typeof(DateTime));
        Assert.Equal(ticks, unspecified.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, unspecified.Kind);

        var utc = (DateTime)NativeProgressIdentityReconciler.ConvertValue(
            "2026-10-31T14:53:35.1234567Z", typeof(DateTime?));
        Assert.Equal(ticks, utc.Ticks);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);

        var typed = new DateTime(ticks, DateTimeKind.Local);
        var kept = (DateTime)NativeProgressIdentityReconciler.ConvertValue(typed, typeof(DateTime));
        Assert.Equal(typed.Ticks, kept.Ticks);
        Assert.Equal(DateTimeKind.Local, kept.Kind);

        var instant = (DateTime)NativeProgressIdentityReconciler.ConvertValue(
            new DateTimeOffset(ticks, TimeSpan.FromHours(9)), typeof(DateTime));
        Assert.Equal(ticks - TimeSpan.FromHours(9).Ticks, instant.Ticks);
        Assert.Equal(DateTimeKind.Utc, instant.Kind);
    }

    private sealed record ConcurrentIds(
        string ServerProgress, string ServerContext, string LocalProgress, string LocalContext,
        DateTime ServerFirstSeen, DateTime LocalLastPracticed);

    /// <summary>
    /// The other device already uploaded its first progress for the word; this device made its
    /// own first progress for the same word offline. Times carry sub-second ticks.
    /// </summary>
    private static async Task<ConcurrentIds> SeedConcurrentFirstProgressAsync(Fixture fixture)
    {
        var ids = new ConcurrentIds(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            new DateTime(2026, 10, 31, 14, 53, 35).AddTicks(1234567),
            new DateTime(2026, 10, 31, 14, 55, 35).AddTicks(7654321));
        await using (var db = fixture.Remote.OpenContext())
        {
            db.VocabularyProgresses.Add(Progress(ids.ServerProgress, Owner, ids.ServerFirstSeen));
            db.VocabularyLearningContexts.Add(
                Attempt(ids.ServerContext, ids.ServerProgress, ids.ServerFirstSeen));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Local.OpenContext())
        {
            db.VocabularyProgresses.Add(Progress(ids.LocalProgress, Owner, ids.LocalLastPracticed));
            db.VocabularyLearningContexts.Add(
                Attempt(ids.LocalContext, ids.LocalProgress, ids.LocalLastPracticed));
            await db.SaveChangesAsync();
        }
        return ids;
    }

    private static void AssertMerged(VocabularyProgress row, ConcurrentIds ids)
    {
        Assert.Equal(ids.ServerProgress, row.Id);
        Assert.Equal(Owner, row.UserId);
        Assert.Equal(2, row.TotalAttempts);
        Assert.Equal(2, row.CorrectAttempts);
        Assert.Equal(2, row.QuizRecognitionDemonstrations);
        Assert.Equal(2, row.RecognitionAttempts);
        Assert.Equal(2, row.MultipleChoiceCorrect);
        Assert.Equal(2f, row.CurrentStreak);
        Assert.Equal(ids.ServerFirstSeen, row.FirstSeenAt);
        Assert.Equal(ids.ServerFirstSeen, row.CreatedAt);
        Assert.Equal(ids.LocalLastPracticed, row.LastPracticedAt);
    }

    private static async Task AssertMergedLocalRowAsync(
        Fixture fixture, ConcurrentIds ids, bool expectServerContext)
    {
        await using var db = fixture.Local.OpenContext();
        AssertMerged(await db.VocabularyProgresses.SingleAsync(), ids);
        var contexts = await db.VocabularyLearningContexts
            .Select(context => new { context.Id, context.VocabularyProgressId }).ToListAsync();
        Assert.All(contexts, context => Assert.Equal(ids.ServerProgress, context.VocabularyProgressId));
        Assert.Contains(contexts, context => context.Id == ids.LocalContext);
        Assert.Equal(expectServerContext, contexts.Any(context => context.Id == ids.ServerContext));
    }

    private static async Task AssertConvergedAsync(Fixture fixture, ConcurrentIds ids)
    {
        await AssertMergedLocalRowAsync(fixture, ids, expectServerContext: true);
        await using var db = fixture.Remote.OpenContext();
        AssertMerged(await db.VocabularyProgresses.SingleAsync(), ids);
        Assert.Equal(
            new[] { ids.LocalContext, ids.ServerContext }.Order(),
            (await db.VocabularyLearningContexts
                .Where(context => context.VocabularyProgressId == ids.ServerProgress)
                .Select(context => context.Id).ToListAsync()).Order());
    }

    private static VocabularyProgress Progress(string id, string owner, DateTime at) => new()
    {
        Id = id, UserId = owner, VocabularyWordId = Word, TotalAttempts = 1,
        CorrectAttempts = 1, RecognitionAttempts = 1, RecognitionCorrect = 1,
        MultipleChoiceCorrect = 1, QuizRecognitionDemonstrations = 1, CurrentStreak = 1,
        MasteryScore = 0.083333336f, FirstSeenAt = at, LastPracticedAt = at,
        CreatedAt = at, UpdatedAt = at, NextReviewDate = at.AddDays(6), ReviewInterval = 6
    };

    private static VocabularyLearningContext Attempt(string id, string progressId, DateTime at) => new()
    {
        Id = id, VocabularyProgressId = progressId, Activity = "VocabularyQuiz",
        InputMode = "MultipleChoice", ContextType = "Isolated", WasCorrect = true,
        DifficultyScore = 1, ResponseTimeMs = 1510, UserInput = "most",
        ExpectedAnswer = "most", CorrectAnswersInContext = 1,
        LearnedAt = at, CreatedAt = at, UpdatedAt = at
    };

    private static bool IsMappingDelete(SyncItem item) =>
        item.TableName == "ResourceVocabularyMapping"
        && item.ChangeType == ChangeType.Delete;

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(Store local, Store remote)
        {
            Local = local;
            Remote = remote;
            Server = new RejectingRemote(remote.Provider);
            Native = new NativeLocalOnlyMappingExportProvider(
                local.Provider, Server, () => Owner,
                NullLogger<NativeLocalOnlyMappingExportProvider>.Instance);
        }

        public Store Local { get; }
        public Store Remote { get; }
        public RejectingRemote Server { get; }
        public ISyncProvider Native { get; }
        public ISyncProvider NativeWith(
            Func<SyncChangeSet, Func<SyncItem, ConflictResolution>?, CancellationToken,
                Task<SyncAnchor>> applyLocal) =>
            new NativeLocalOnlyMappingExportProvider(
                Local.Provider, Server, () => Owner,
                NullLogger<NativeLocalOnlyMappingExportProvider>.Instance, applyLocal);

        public Guid RemoteId =>Remote.Provider.GetStoreIdAsync().GetAwaiter().GetResult();

        public static async Task<Fixture> CreateAsync(bool seedRemoteMapping = false)
        {
            var local = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
            var remote = await Store.CreateAsync(ProviderMode.Remote);
            var fixture = new Fixture(local, remote);
            await using (var db = remote.OpenContext())
            {
                db.UserProfiles.Add(new UserProfile { Id = Owner, Email = "owner@example.test" });
                db.LearningResources.Add(new LearningResource
                {
                    Id = Resource, UserProfileId = Owner, Title = "Owned resource"
                });
                db.VocabularyWords.Add(new VocabularyWord
                {
                    Id = Word, TargetLanguageTerm = "practice", NativeLanguageTerm = "test"
                });
                if (seedRemoteMapping)
                    db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                    {
                        Id = ServerMapping, ResourceId = Resource, VocabularyWordId = Word
                    });
                await db.SaveChangesAsync();
            }
            await new SyncAgent(local.Provider, fixture.Server).SynchronizeAsync();
            return fixture;
        }

        // Mirrors SyncService: skip insert conflicts, force updates and deletes.
        public Task SynchronizeLikeAppAsync(ISyncProvider? native = null) =>
            new SyncAgent(native ?? Native, Server).SynchronizeAsync(
                remoteConflictResolutionFunc: item => item.ChangeType == ChangeType.Insert
                    ? ConflictResolution.Skip : ConflictResolution.ForceWrite,
                localConflictResolutionFunc: item => item.ChangeType == ChangeType.Insert
                    ? ConflictResolution.Skip : ConflictResolution.ForceWrite);

        public async Task<long> LocalAnchorAsync()
        {
            await using var connection = new SqliteConnection(Local.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT LOCAL_VERSION FROM __CORE_SYNC_REMOTE_ANCHOR WHERE ID = $id
                """;
            command.Parameters.AddWithValue("$id", RemoteId.ToString());
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task InsertMappingAsync(string id, string resourceId = Resource)
        {
            await using var db = Local.OpenContext();
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = id, ResourceId = resourceId, VocabularyWordId = Word
            });
            await db.SaveChangesAsync();
        }

        public async Task DeleteMappingAsync(string id)
        {
            await using var db = Local.OpenContext();
            var row = await db.ResourceVocabularyMappings.SingleAsync(mapping => mapping.Id == id);
            db.ResourceVocabularyMappings.Remove(row);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Local.DisposeAsync();
            await Remote.DisposeAsync();
        }
    }

    private sealed class Store(
        SqliteConnection keepAlive, string connectionString,
        DbContextOptions<ApplicationDbContext> options, SqliteSyncProvider provider)
        : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public SqliteSyncProvider Provider { get; } = provider;
        public ApplicationDbContext OpenContext() => new(options);

        public static async Task<Store> CreateAsync(
            ProviderMode mode, bool downloadOnlyProfile = false)
        {
            var connectionString =
                $"Data Source=native-undo-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            var options = DeviceInterpreterQueryGuard.Guard(new DbContextOptionsBuilder<ApplicationDbContext>())
                .UseSqlite(connectionString).Options;
            await using (var db = new ApplicationDbContext(options))
                await db.Database.EnsureCreatedAsync();
            var provider = new SqliteSyncProvider(
                new SqliteSyncConfigurationBuilder(connectionString)
                    .ConfigureSyncTables(downloadOnlyProfile).Build(), mode);
            await provider.ApplyProvisionAsync();
            return new Store(keepAlive, connectionString, options, provider);
        }

        public ValueTask DisposeAsync() => keepAlive.DisposeAsync();
    }

    private sealed class RejectingRemote(ISyncProvider inner) : ISyncProviderBase
    {
        public bool FailNextUpload { get; set; }
        public bool FailNextPreflight { get; set; }
        public string[]? SyncTableNames => inner.SyncTableNames;
        public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
            inner.GetStoreIdAsync(ct);
        public Task SaveVersionForStoreAsync(
            Guid storeId, long version, CancellationToken ct = default) =>
            inner.SaveVersionForStoreAsync(storeId, version, ct);
        public Task<SyncChangeSet> GetChangesAsync(
            Guid storeId, SyncFilterParameter[]? filters = null,
            SyncDirection direction = SyncDirection.UploadAndDownload,
            string[]? tables = null, CancellationToken ct = default)
        {
            if (FailNextPreflight && tables is ["UserProfile"])
            {
                FailNextPreflight = false;
                throw new InvalidOperationException("Remote receipt unavailable.");
            }
            return inner.GetChangesAsync(storeId, filters, direction, tables, ct);
        }
        public Task<SyncAnchor> ApplyChangesAsync(
            SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
            CancellationToken ct = default)
        {
            // Models an API from before owner-checked deletes (decision 2), which refused them.
            if (changes.Items.Any(IsMappingDelete))
                throw new UnauthorizedAccessException("Native server refuses Delete (403).");
            if (FailNextUpload)
            {
                FailNextUpload = false;
                throw new InvalidOperationException("Upload interrupted before server commit.");
            }
            return inner.ApplyChangesAsync(changes, onConflict, ct);
        }
    }

    /// <summary>The app's HTTP remote surface over the in-memory server; counts sync passes.</summary>
    private sealed class HttpRemote(ISyncProviderBase inner) : ISyncProviderHttpClient
    {
        public int Passes { get; set; }
#pragma warning disable CS0067 // Progress is not reported by the in-memory server.
        public event EventHandler<SyncProgressEventArgs>? SyncProgress;
#pragma warning restore CS0067
        public string[]? SyncTableNames => inner.SyncTableNames;
        public Task<Guid> GetStoreIdAsync(CancellationToken ct = default)
        {
            Passes++;
            return inner.GetStoreIdAsync(ct);
        }
        public Task SaveVersionForStoreAsync(
            Guid storeId, long version, CancellationToken ct = default) =>
            inner.SaveVersionForStoreAsync(storeId, version, ct);
        public Task<SyncChangeSet> GetChangesAsync(
            Guid storeId, SyncFilterParameter[]? filters = null,
            SyncDirection direction = SyncDirection.UploadAndDownload,
            string[]? tables = null, CancellationToken ct = default) =>
            inner.GetChangesAsync(storeId, filters, direction, tables, ct);
        public Task<SyncAnchor> ApplyChangesAsync(
            SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
            CancellationToken ct = default) =>
            inner.ApplyChangesAsync(changes, onConflict, ct);
    }

    /// <summary>A local provider whose every download asks for a follow-up upload.</summary>
    private sealed class AlwaysFollowUp(ISyncProvider inner) : ISyncProvider, ISyncFollowUpSource
    {
        public bool TakeFollowUpRequest() => true;
        public string[]? SyncTableNames => inner.SyncTableNames;
        public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
            inner.GetStoreIdAsync(ct);
        public Task SaveVersionForStoreAsync(
            Guid storeId, long version, CancellationToken ct = default) =>
            inner.SaveVersionForStoreAsync(storeId, version, ct);
        public Task<SyncChangeSet> GetChangesAsync(
            Guid storeId, SyncFilterParameter[]? filters = null,
            SyncDirection direction = SyncDirection.UploadAndDownload,
            string[]? tables = null, CancellationToken ct = default) =>
            inner.GetChangesAsync(storeId, filters, direction, tables, ct);
        public Task<SyncAnchor> ApplyChangesAsync(
            SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
            CancellationToken ct = default) =>
            inner.ApplyChangesAsync(changes, onConflict, ct);
        public Task ApplyProvisionAsync(CancellationToken ct = default) =>
            inner.ApplyProvisionAsync(ct);
        public Task RemoveProvisionAsync(CancellationToken ct = default) =>
            inner.RemoveProvisionAsync(ct);
        public Task<SyncVersion> GetSyncVersionAsync(CancellationToken ct = default) =>
            inner.GetSyncVersionAsync(ct);
        public Task<SyncVersion> ApplyRetentionPolicyAsync(
            int minVersion, CancellationToken ct = default) =>
            inner.ApplyRetentionPolicyAsync(minVersion, ct);
        public Task EnableChangeTrackingForTable(string tableName, CancellationToken ct = default) =>
            inner.EnableChangeTrackingForTable(tableName, ct);
        public Task DisableChangeTrackingForTable(string tableName, CancellationToken ct = default) =>
            inner.DisableChangeTrackingForTable(tableName, ct);
    }

    private sealed class FailLocalSave(ISyncProviderBase inner) : ISyncProviderBase
    {
        public string[]? SyncTableNames => inner.SyncTableNames;
        public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
            inner.GetStoreIdAsync(ct);
        public Task SaveVersionForStoreAsync(
            Guid storeId, long version, CancellationToken ct = default) =>
            throw new InvalidOperationException("Interrupted after server commit.");
        public Task<SyncChangeSet> GetChangesAsync(
            Guid storeId, SyncFilterParameter[]? filters = null,
            SyncDirection direction = SyncDirection.UploadAndDownload,
            string[]? tables = null, CancellationToken ct = default) =>
            inner.GetChangesAsync(storeId, filters, direction, tables, ct);
        public Task<SyncAnchor> ApplyChangesAsync(
            SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
            CancellationToken ct = default) =>
            inner.ApplyChangesAsync(changes, onConflict, ct);
    }
}
