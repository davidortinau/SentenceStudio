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
            new VocabularyWord { Id = "owned-word", TargetLanguageTerm = "owned" },
            new VocabularyWord { Id = "foreign-word", TargetLanguageTerm = "foreign" });
        db.ResourceVocabularyMappings.AddRange(
            new ResourceVocabularyMapping {
                Id = "owned-map", ResourceId = "owned-resource", VocabularyWordId = "owned-word" },
            new ResourceVocabularyMapping {
                Id = "foreign-map", ResourceId = "foreign-resource", VocabularyWordId = "foreign-word" });
        db.VocabularyProgresses.AddRange(
            new VocabularyProgress {
                Id = "owned-progress", VocabularyWordId = "owned-word", UserId = Owner },
            new VocabularyProgress {
                Id = "foreign-progress", VocabularyWordId = "foreign-word", UserId = Foreign },
            new VocabularyProgress {
                Id = "unmapped-progress", VocabularyWordId = "foreign-word", UserId = Owner });
        await db.SaveChangesAsync();

        var changes = new SyncChangeSet(
            new SyncAnchor(Guid.NewGuid(), 12), SyncAnchor.Null,
            [
                Item("UserProfile", Owner), Item("UserProfile", Foreign),
                Item("LearningResource", "owned-resource", ("UserProfileId", Owner)),
                Item("LearningResource", "foreign-resource", ("UserProfileId", Foreign)),
                Item("VocabularyWord", "owned-word"), Item("VocabularyWord", "foreign-word"),
                Item("ResourceVocabularyMapping", "owned-map", ("ResourceId", "owned-resource")),
                Item("ResourceVocabularyMapping", "foreign-map", ("ResourceId", "foreign-resource")),
                Item("VocabularyProgress", "owned-progress", ("UserId", Owner)),
                Item("VocabularyProgress", "foreign-progress", ("UserId", Foreign)),
                Item("VocabularyProgress", "unmapped-progress", ("UserId", Owner)),
                Item("VocabularyLearningContext", "owned-context", ("VocabularyProgressId", "owned-progress")),
                Item("VocabularyLearningContext", "foreign-context", ("VocabularyProgressId", "foreign-progress")),
                Item("VocabularyLearningContext", "unmapped-context", ("VocabularyProgressId", "unmapped-progress")),
                Item("DailyPlan", "owned-plan", ("UserProfileId", Owner)),
                Item("DailyPlan", "foreign-plan", ("UserProfileId", Foreign)),
                Item("Challenge", "unattributed-challenge")
            ]);

        var filtered = await NativeOwnerSyncProvider.FilterForOwnerAsync(changes, Owner, db);

        Assert.Same(changes.SourceAnchor, filtered.SourceAnchor);
        Assert.Equal(
            [Owner, "owned-resource", "owned-word", "owned-map", "owned-progress",
                "owned-context", "owned-plan"],
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
            new VocabularyWord { Id = "legacy-word", TargetLanguageTerm = "옛" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId)
            VALUES ('foreign-link', 'foreign-resource', 'recorded-word'),
                   ('legacy-link', 'owned-resource', 'legacy-word')
            """);

        var owners = await NativeOwnerSyncProvider.GetStoredOwnersAsync(
            db, "VocabularyWord", ["recorded-word", "legacy-word"], default);

        Assert.Equal(Owner, owners["recorded-word"]);
        Assert.Equal(Owner, owners["legacy-word"]);
    }

    [Fact]
    public void Upload_DropsClientSentWordOwnersSoOnlyTheServerDecides()
    {
        var claimed = Item("VocabularyWord", "claimed-word",
            ("UserProfileId", Foreign), ("TargetLanguageTerm", "kept"));
        var blank = new SyncItem("VocabularyWord", ChangeType.Update,
            new Dictionary<string, object?> { ["Id"] = "blank-word", ["userprofileid"] = null });
        var resource = Item("LearningResource", "owned-resource", ("UserProfileId", Owner));

        NativeOwnerSyncProvider.IgnoreClientWordOwners([claimed, blank, resource]);

        Assert.False(claimed.Values.ContainsKey("UserProfileId"));
        Assert.Equal("kept", claimed.Values["TargetLanguageTerm"].Value);
        Assert.DoesNotContain(blank.Values.Keys,
            key => key.Equals("UserProfileId", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Owner, resource.Values["UserProfileId"].Value);
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
