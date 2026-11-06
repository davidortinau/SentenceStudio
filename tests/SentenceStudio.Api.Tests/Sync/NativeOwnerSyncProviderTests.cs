using System.Security.Claims;
using CoreSync;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Sync;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

public sealed class NativeOwnerSyncProviderTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Foreign = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    // Words go to a device by their recorded owner (Captain 2026-11-06), and their dependents
    // follow them: the owner's word outside every resource and its progress arrive; another
    // learner's word, or one recorded to no one, never does, even when the owner's resource
    // links it, and neither does that link or the owner's progress on it.
    [Fact]
    public async Task NativeDownload_KeepsOnlyOwnedAndReachableRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.UserProfiles.AddRange(
            new UserProfile { Id = Owner, Email = "owner@example.test" },
            new UserProfile { Id = Foreign, Email = "foreign@example.test" });
        db.LearningResources.AddRange(
            new LearningResource { Id = "owned-resource", UserProfileId = Owner },
            new LearningResource { Id = "foreign-resource", UserProfileId = Foreign });
        db.VocabularyWords.AddRange(
            new VocabularyWord { Id = "owned-word", TargetLanguageTerm = "owned", UserProfileId = Owner },
            new VocabularyWord { Id = "owned-unlinked-word", TargetLanguageTerm = "mine", UserProfileId = Owner },
            new VocabularyWord { Id = "foreign-word", TargetLanguageTerm = "foreign", UserProfileId = Foreign },
            new VocabularyWord { Id = "ownerless-word", TargetLanguageTerm = "legacy", UserProfileId = Owner });
        db.ResourceVocabularyMappings.AddRange(
            new ResourceVocabularyMapping {
                Id = "owned-map", ResourceId = "owned-resource", VocabularyWordId = "owned-word" },
            new ResourceVocabularyMapping {
                Id = "foreign-map", ResourceId = "foreign-resource", VocabularyWordId = "foreign-word" },
            new ResourceVocabularyMapping {
                Id = "foreign-word-in-owned-map", ResourceId = "owned-resource", VocabularyWordId = "foreign-word" },
            new ResourceVocabularyMapping {
                Id = "ownerless-in-owned-map", ResourceId = "owned-resource", VocabularyWordId = "ownerless-word" });
        db.VocabularyProgresses.AddRange(
            new VocabularyProgress {
                Id = "owned-progress", VocabularyWordId = "owned-word", UserId = Owner },
            new VocabularyProgress {
                Id = "unlinked-progress", VocabularyWordId = "owned-unlinked-word", UserId = Owner },
            new VocabularyProgress {
                Id = "foreign-progress", VocabularyWordId = "foreign-word", UserId = Foreign },
            new VocabularyProgress {
                Id = "unmapped-progress", VocabularyWordId = "foreign-word", UserId = Owner });
        await db.SaveChangesAsync();
        await db.VocabularyWords.Where(word => word.Id == "ownerless-word")
            .ExecuteUpdateAsync(set => set.SetProperty(word => word.UserProfileId, (string?)null));

        var changes = new SyncChangeSet(
            new SyncAnchor(Guid.NewGuid(), 12), SyncAnchor.Null,
            [
                Item("UserProfile", Owner), Item("UserProfile", Foreign),
                Item("LearningResource", "owned-resource", ("UserProfileId", Owner)),
                Item("LearningResource", "foreign-resource", ("UserProfileId", Foreign)),
                Item("VocabularyWord", "owned-word", ("UserProfileId", Owner)),
                Item("VocabularyWord", "owned-unlinked-word", ("UserProfileId", Owner)),
                Item("VocabularyWord", "foreign-word", ("UserProfileId", Foreign)),
                Item("VocabularyWord", "ownerless-word"),
                Item("ResourceVocabularyMapping", "owned-map",
                    ("ResourceId", "owned-resource"), ("VocabularyWordId", "owned-word")),
                Item("ResourceVocabularyMapping", "foreign-map",
                    ("ResourceId", "foreign-resource"), ("VocabularyWordId", "foreign-word")),
                Item("ResourceVocabularyMapping", "foreign-word-in-owned-map",
                    ("ResourceId", "owned-resource"), ("VocabularyWordId", "foreign-word")),
                Item("ResourceVocabularyMapping", "ownerless-in-owned-map",
                    ("ResourceId", "owned-resource"), ("VocabularyWordId", "ownerless-word")),
                Item("VocabularyProgress", "owned-progress", ("UserId", Owner)),
                Item("VocabularyProgress", "unlinked-progress", ("UserId", Owner)),
                Item("VocabularyProgress", "foreign-progress", ("UserId", Foreign)),
                Item("VocabularyProgress", "unmapped-progress", ("UserId", Owner)),
                Item("VocabularyLearningContext", "owned-context", ("VocabularyProgressId", "owned-progress")),
                Item("VocabularyLearningContext", "unlinked-context", ("VocabularyProgressId", "unlinked-progress")),
                Item("VocabularyLearningContext", "foreign-context", ("VocabularyProgressId", "foreign-progress")),
                Item("VocabularyLearningContext", "unmapped-context", ("VocabularyProgressId", "unmapped-progress")),
                Item("DailyPlan", "owned-plan", ("UserProfileId", Owner)),
                Item("DailyPlan", "foreign-plan", ("UserProfileId", Foreign)),
                Item("Challenge", "unattributed-challenge")
            ]);

        var filtered = await NativeOwnerSyncProvider.FilterForOwnerAsync(changes, Owner, db);

        Assert.Same(changes.SourceAnchor, filtered.SourceAnchor);
        Assert.Equal(
            [Owner, "owned-resource", "owned-word", "owned-unlinked-word", "owned-map",
                "owned-progress", "unlinked-progress", "owned-context", "unlinked-context", "owned-plan"],
            filtered.Items.Select(item => item.Values["Id"].Value?.ToString()));
    }

    [Fact]
    public async Task StoredWordOwner_WinsOverCurrentLinks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await SeedTwoOwnersAsync(connection);
        // A's recorded word that B later linked to, and a legacy ownerless word.
        db.VocabularyWords.AddRange(
            new VocabularyWord { Id = "recorded-word", TargetLanguageTerm = "기록", UserProfileId = Owner },
            new VocabularyWord { Id = "legacy-word", TargetLanguageTerm = "옛", UserProfileId = Owner });
        await db.SaveChangesAsync();
        await db.VocabularyWords.Where(word => word.Id == "legacy-word")
            .ExecuteUpdateAsync(set => set.SetProperty(word => word.UserProfileId, (string?)null));
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId)
            VALUES ('foreign-link', 'foreign-resource', 'recorded-word'),
                   ('legacy-link', 'owned-resource', 'legacy-word')
            """);

        var owners = await SentenceStudio.Services.SyncRowOwners.GetStoredOwnersAsync(
            db, "VocabularyWord", ["recorded-word", "legacy-word"], default);

        Assert.Equal(Owner, owners["recorded-word"]);
        // One runtime rule (Captain 2026-11-06): a word recorded to no one is no one's, even one
        // only this owner links; the one-time backfill records it, nothing infers it at runtime.
        Assert.Null(owners["legacy-word"]);
    }

    [Fact]
    public void Upload_ReplacesClientSentWordOwnersWithTheAuthenticatedCreator()
    {
        var claimed = Item("VocabularyWord", "claimed-word",
            ("UserProfileId", Foreign), ("TargetLanguageTerm", "kept"));
        var blank = new SyncItem("VocabularyWord", ChangeType.Update,
            new Dictionary<string, object?> { ["Id"] = "blank-word", ["userprofileid"] = null });
        var resource = Item("LearningResource", "owned-resource", ("UserProfileId", Owner));
        var items = new[] { claimed, blank, resource };

        // The device copies are read first: one names another learner, one names no one.
        Assert.Equal(new[] { "claimed-word" }, NativeOwnerSyncProvider.WordsNamingAnotherOwner(items, Owner));
        Assert.Equal(new[] { "blank-word" }, NativeOwnerSyncProvider.WordsWithoutOwner(items));

        NativeOwnerSyncProvider.RecordUploadedWordOwners(items, Owner);

        // Every written word carries the server's choice (the uploader); no client spelling of the
        // column survives, and other tables are untouched.
        Assert.Equal(Owner, claimed.Values["UserProfileId"].Value);
        Assert.Equal("kept", claimed.Values["TargetLanguageTerm"].Value);
        Assert.Equal(Owner, blank.Values["UserProfileId"].Value);
        Assert.DoesNotContain(blank.Values.Keys, key => key == "userprofileid");
        Assert.Equal(Owner, resource.Values["UserProfileId"].Value);

        // A headerless legacy upload never sets an owner (the stored one stays).
        var legacy = Item("VocabularyWord", "legacy-upload", ("UserProfileId", Foreign));
        NativeOwnerSyncProvider.RecordUploadedWordOwners([legacy], owner: null);
        Assert.False(legacy.Values.ContainsKey("UserProfileId"));
    }

    private static async Task<ApplicationDbContext> SeedTwoOwnersAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.UserProfiles.AddRange(
            new UserProfile { Id = Owner, Email = "owner@example.test" },
            new UserProfile { Id = Foreign, Email = "foreign@example.test" });
        db.LearningResources.AddRange(
            new LearningResource { Id = "owned-resource", UserProfileId = Owner },
            new LearningResource { Id = "foreign-resource", UserProfileId = Foreign });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public void NativeOwnerHeader_RequiresTheSameAuthenticatedBearerOwner()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthClaimTypes.UserProfileId, Owner)], "Bearer"))
        };
        context.Request.Headers.Authorization = "Bearer test-token";

        Assert.Null(NativeOwnerSyncProvider.GetNativeOwner(context));
        context.Request.Headers[SharedSyncRegistration.NativeOwnerHeader] = Owner;
        Assert.Equal(Owner, NativeOwnerSyncProvider.GetNativeOwner(context));

        context.Request.Headers[SharedSyncRegistration.NativeOwnerHeader] = Foreign;
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.Throws<BadHttpRequestException>(
                () => NativeOwnerSyncProvider.GetNativeOwner(context)).StatusCode);
        context.Request.Headers[SharedSyncRegistration.NativeOwnerHeader] = Owner;
        context.Request.Headers.Remove("Authorization");
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.Throws<BadHttpRequestException>(
                () => NativeOwnerSyncProvider.GetNativeOwner(context)).StatusCode);
    }

    private static SyncItem Item(
        string table, string id, params (string Column, string Value)[] columns)
    {
        var values = new Dictionary<string, object?> { ["Id"] = id };
        foreach (var (column, value) in columns)
            values[column] = value;
        return new SyncItem(table, ChangeType.Insert, values);
    }
}
