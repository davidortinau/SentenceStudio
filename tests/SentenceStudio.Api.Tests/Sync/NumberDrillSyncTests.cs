using CoreSync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Sync;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// Phase 1 of syncing every learner table (Captain 2026-11-02, docs/sync-all-tables-plan.md):
/// Number Drill attempts and mastery and Word Association scores sync only to their owner, and
/// a mastery bucket practiced on two devices keeps the copy reviewed most recently (D5).
/// </summary>
public sealed class NumberDrillSyncTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Foreign = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task NativeDownload_KeepsOnlyTheOwnersNumberDrillAndWordAssociationRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);

        var changes = new SyncChangeSet(new SyncAnchor(Guid.NewGuid(), 3), SyncAnchor.Null,
        [
            Item("NumberAttempt", "owned-attempt", ("UserProfileId", Owner)),
            Item("NumberAttempt", "foreign-attempt", ("UserProfileId", Foreign)),
            Item("NumberMasteryProgress", "owned-mastery", ("UserProfileId", Owner)),
            Item("NumberMasteryProgress", "foreign-mastery", ("UserProfileId", Foreign)),
            Item("WordAssociationScore", "owned-score", ("UserProfileId", Owner)),
            Item("WordAssociationScore", "foreign-score", ("UserProfileId", Foreign)),
            Item("NumberAttempt", "unowned-attempt"),
        ]);

        var filtered = await NativeOwnerSyncProvider.FilterForOwnerAsync(changes, Owner, db);

        Assert.Equal(["owned-attempt", "owned-mastery", "owned-score"],
            filtered.Items.Select(item => item.Values["Id"].Value?.ToString()));
    }

    [Fact]
    public async Task StoredOwnerLookup_ReportsTheForeignOwnerOfAnExistingRow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);
        db.NumberMasteryProgresses.Add(Mastery("shared-id", Foreign, null));
        db.NumberAttempts.Add(new NumberAttempt { Id = "attempt", UserProfileId = Foreign });
        db.WordAssociationScores.Add(new WordAssociationScore { Id = "score", UserProfileId = Foreign });
        await db.SaveChangesAsync();

        // An upload that reuses another learner's row ID is refused by the owner check.
        foreach (var (table, id) in new[] {
            ("NumberMasteryProgress", "shared-id"), ("NumberAttempt", "attempt"),
            ("WordAssociationScore", "score") })
        {
            var owners = await NativeOwnerSyncProvider.GetStoredOwnersAsync(db, table, [id], default);
            Assert.Equal(Foreign, owners[id]);
        }
    }

    [Fact]
    public async Task MasteryConflict_MostRecentlyReviewedCopyWins()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);
        var day = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        db.NumberMasteryProgresses.AddRange(
            Mastery("upload-newer", Owner, day),
            Mastery("stored-newer", Owner, day.AddHours(2)),
            Mastery("same-time", Owner, day),
            Mastery("foreign-row", Foreign, day));
        await db.SaveChangesAsync();

        var items = new[]
        {
            Item("NumberMasteryProgress", "upload-newer", ("UserProfileId", Owner),
                ("LastReviewed", day.AddHours(1).ToString("O"))),
            Item("NumberMasteryProgress", "stored-newer", ("UserProfileId", Owner),
                ("LastReviewed", day.AddHours(1).ToString("O"))),
            Item("NumberMasteryProgress", "same-time", ("UserProfileId", Owner),
                ("LastReviewed", day.ToString("O"))),
            Item("NumberMasteryProgress", "new-bucket", ("UserProfileId", Owner),
                ("LastReviewed", day.ToString("O"))),
            Item("NumberMasteryProgress", "foreign-row", ("UserProfileId", Owner),
                ("LastReviewed", day.AddDays(1).ToString("O"))),
            Item("NumberAttempt", "attempt", ("UserProfileId", Owner)),
        };

        var plan = await NumberMasteryUploadMerge.PrepareAsync(db, items, Owner, default);

        Assert.Equal(["upload-newer"], plan.UploadWins);
        Assert.Equal(["same-time", "stored-newer"], plan.StoredWins.Order());
        // Another learner's row is never compared or written by this owner's upload.
        Assert.DoesNotContain("foreign-row", plan.UploadWins);
        Assert.DoesNotContain("foreign-row", plan.StoredWins);

        var applied = NumberMasteryUploadMerge.WithoutLosers(items, plan)
            .Select(item => item.Values["Id"].Value?.ToString() ?? string.Empty).ToArray();
        Assert.Equal(["upload-newer", "new-bucket", "foreign-row", "attempt"], applied);
        Assert.Equal(ConflictResolution.ForceWrite,
            NumberMasteryUploadMerge.Resolve(plan, items[0]));
        Assert.Null(NumberMasteryUploadMerge.Resolve(plan, items[3]));
        Assert.Null(NumberMasteryUploadMerge.Resolve(plan, items[5]));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("2026-11-02T10:00:00Z", null, true)]
    [InlineData(null, "2026-11-02T10:00:00Z", false)]
    [InlineData("2026-11-02T10:00:00Z", "2026-11-02T10:00:00Z", false)]
    [InlineData("2026-11-02T11:00:00Z", "2026-11-02T10:00:00Z", true)]
    public void UploadIsNewer_OnlyWhenItWasReviewedLater(string? upload, string? stored, bool expected)
    {
        static DateTime? Parse(string? text) => text is null ? null
            : DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(expected, NumberMasteryUploadMerge.UploadIsNewer(Parse(upload), Parse(stored)));
    }

    private static async Task<ApplicationDbContext> CreateAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.UserProfiles.AddRange(
            new UserProfile { Id = Owner, Email = "owner@example.test" },
            new UserProfile { Id = Foreign, Email = "foreign@example.test" });
        await db.SaveChangesAsync();
        return db;
    }

    private static NumberMasteryProgress Mastery(string id, string owner, DateTime? reviewed) => new()
    {
        Id = id,
        UserProfileId = owner,
        LanguageCode = "ko",
        ContextCode = $"context-{id}",
        System = NumberSystem.Sino,
        Bucket = "1-10",
        LastReviewed = reviewed,
    };

    private static SyncItem Item(
        string table, string id, params (string Column, string Value)[] columns)
    {
        var values = new Dictionary<string, object?> { ["Id"] = id };
        foreach (var (column, value) in columns)
            values[column] = value;
        return new SyncItem(table, ChangeType.Insert, values);
    }
}
