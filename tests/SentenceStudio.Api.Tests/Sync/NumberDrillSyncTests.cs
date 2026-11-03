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

    /// <summary>
    /// Phase 2a: Minimal Pairs, audio history and conversation memory travel only to their owner,
    /// and a pair or answer only with words that owner can see.
    /// </summary>
    [Fact]
    public async Task NativeDownload_KeepsOnlyTheOwnersPhase2aRowsWithVisibleWords()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);
        db.LearningResources.Add(new LearningResource { Id = "owned-resource", UserProfileId = Owner });
        db.VocabularyWords.AddRange(
            new VocabularyWord { Id = "w1", TargetLanguageTerm = "w1" },
            new VocabularyWord { Id = "w2", TargetLanguageTerm = "w2" },
            new VocabularyWord { Id = "hidden", TargetLanguageTerm = "hidden" });
        db.ResourceVocabularyMappings.AddRange(
            new ResourceVocabularyMapping { Id = "m1", ResourceId = "owned-resource", VocabularyWordId = "w1" },
            new ResourceVocabularyMapping { Id = "m2", ResourceId = "owned-resource", VocabularyWordId = "w2" });
        await db.SaveChangesAsync();

        var changes = new SyncChangeSet(new SyncAnchor(Guid.NewGuid(), 3), SyncAnchor.Null,
        [
            Item("MinimalPair", "owned-pair", ("UserId", Owner),
                ("VocabularyWordAId", "w1"), ("VocabularyWordBId", "w2")),
            Item("MinimalPair", "hidden-word-pair", ("UserId", Owner),
                ("VocabularyWordAId", "w1"), ("VocabularyWordBId", "hidden")),
            Item("MinimalPair", "foreign-pair", ("UserId", Foreign),
                ("VocabularyWordAId", "w1"), ("VocabularyWordBId", "w2")),
            Item("MinimalPairSession", "owned-session", ("UserId", Owner)),
            Item("MinimalPairSession", "foreign-session", ("UserId", Foreign)),
            Item("MinimalPairAttempt", "owned-answer", ("UserId", Owner),
                ("PromptWordId", "w1"), ("SelectedWordId", "w2")),
            Item("MinimalPairAttempt", "hidden-answer", ("UserId", Owner),
                ("PromptWordId", "hidden"), ("SelectedWordId", "w2")),
            Item("StreamHistory", "owned-audio", ("UserProfileId", Owner)),
            Item("StreamHistory", "unowned-audio"),
            Item("ConversationMemoryState", "cm:owned", ("UserProfileId", Owner)),
            Item("ConversationMemoryState", "cm:foreign", ("UserProfileId", Foreign)),
        ]);

        var filtered = await NativeOwnerSyncProvider.FilterForOwnerAsync(changes, Owner, db);

        Assert.Equal(["owned-pair", "owned-session", "owned-answer", "owned-audio", "cm:owned"],
            filtered.Items.Select(item => item.Values["Id"].Value?.ToString() ?? string.Empty));
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

        var applied = LatestCopyUploadMerge.WithoutLosers(items, plan)
            .Select(item => item.Values["Id"].Value?.ToString() ?? string.Empty).ToArray();
        Assert.Equal(["upload-newer", "new-bucket", "foreign-row", "attempt"], applied);
        Assert.Equal(ConflictResolution.ForceWrite,
            LatestCopyUploadMerge.Resolve(plan, items[0]));
        Assert.Null(LatestCopyUploadMerge.Resolve(plan, items[3]));
        Assert.Null(LatestCopyUploadMerge.Resolve(plan, items[5]));
    }

    /// <summary>
    /// Phase 2b: a device that saved a quiz run, went offline, and uploads after the web saved a
    /// newer copy must not overwrite it (seen on the local stack 2026-11-03). The newer save wins.
    /// </summary>
    [Fact]
    public async Task QuizRunConflict_MostRecentlySavedCopyWins()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);
        var webSave = new DateTime(2026, 11, 3, 18, 45, 0, DateTimeKind.Utc);
        ActivitySession Run(string id, string owner, DateTime updated) => new()
        {
            Id = id, UserId = owner, ActivityType = "VocabularyReview", LaunchContextKey = id,
            StateJson = "{}", Status = ActivitySessionStatus.InProgress,
            StartedAt = updated, UpdatedAt = updated
        };
        db.ActivitySessions.AddRange(
            Run("web-newer", Owner, webSave),
            Run("device-newer", Owner, webSave.AddHours(-3)),
            Run("foreign-run", Foreign, webSave));
        await db.SaveChangesAsync();

        var items = new[]
        {
            // A device's SQLite text timestamp, saved before the web's save.
            Item("ActivitySession", "web-newer", ("UserId", Owner), ("UpdatedAt", "2026-11-03 16:04:22.606993")),
            Item("ActivitySession", "device-newer", ("UserId", Owner), ("UpdatedAt", "2026-11-03 17:00:00")),
            Item("ActivitySession", "new-run", ("UserId", Owner), ("UpdatedAt", "2026-11-03 17:00:00")),
            Item("ActivitySession", "foreign-run", ("UserId", Owner), ("UpdatedAt", "2026-11-04 00:00:00")),
        };

        var runs = await ActivitySessionUploadMerge.PrepareAsync(db, items, Owner, default);
        var plan = runs.Copies;

        Assert.Equal(["device-newer"], plan.UploadWins);
        Assert.Equal(["web-newer"], plan.StoredWins);
        Assert.DoesNotContain("foreign-run", plan.UploadWins.Concat(plan.StoredWins));
        Assert.Equal(["device-newer", "new-run", "foreign-run"],
            LatestCopyUploadMerge.WithoutLosers(items, plan)
                .Select(item => item.Values["Id"].Value?.ToString() ?? string.Empty));
        Assert.Equal(ConflictResolution.ForceWrite, LatestCopyUploadMerge.Resolve(plan, items[1]));
        Assert.Null(LatestCopyUploadMerge.Resolve(plan, items[2]));
        // The kept copy is sent back in one statement.
        await LatestCopyUploadMerge.TouchStoredWinnersAsync(db, plan, Owner, default);
    }

    /// <summary>
    /// Phase 2b review: two clients can each hold a different run of one launch (each started it
    /// before they synced, or the migration gave each client's latest run the run ID). The newer
    /// copy takes the run row; the other run is kept as its own record, never dropped.
    /// </summary>
    [Fact]
    public async Task QuizRunConflict_ADifferentRunThatLoses_IsKeptAsItsOwnRecord()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);
        var webStart = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);
        var storedOlder = $"as:{Owner}:VocabularyReview:older";
        var storedNewer = $"as:{Owner}:VocabularyReview:newer";
        var sameRun = $"as:{Owner}:VocabularyReview:same";
        ActivitySession Run(string id, DateTime started, DateTime updated) => new()
        {
            Id = id, UserId = Owner, ActivityType = "VocabularyReview", LaunchContextKey = id,
            StateJson = $$"""{"stored":"{{id}}"}""", Status = ActivitySessionStatus.InProgress,
            StartedAt = started, UpdatedAt = updated
        };
        db.ActivitySessions.AddRange(
            Run(storedOlder, webStart, webStart),
            Run(storedNewer, webStart, webStart.AddHours(2)),
            Run(sameRun, webStart.AddMilliseconds(250), webStart.AddHours(2)));
        await db.SaveChangesAsync();

        SyncItem Upload(string id, string started, string updated, string status) => Item(
            "ActivitySession", id, ("UserId", Owner), ("ActivityType", "VocabularyReview"),
            ("LaunchContextKey", id), ("StateJson", $$"""{"device":"{{id}}"}"""), ("Status", status),
            ("StartedAt", started), ("UpdatedAt", updated));
        var items = new[]
        {
            // A device's own newer run replaces the web's run of the same launch.
            Upload(storedOlder, "2026-11-03 19:00:00.1234567", "2026-11-03 19:30:00", "InProgress"),
            // A device's run finished a day earlier loses to the web's newer one.
            Upload(storedNewer, "2026-11-02 08:00:00", "2026-11-02 08:20:00", "Completed"),
            // The same run (same start second) saved earlier: only an older position.
            Upload(sameRun, "2026-11-03 18:00:00.9", "2026-11-03 19:00:00", "InProgress"),
        };

        var runs = await ActivitySessionUploadMerge.PrepareAsync(db, items, Owner, default);
        await ActivitySessionUploadMerge.KeepReplacedRunsAsync(db, runs, default);

        Assert.Equal([storedOlder], runs.Copies.UploadWins);
        Assert.Equal([storedNewer, sameRun], runs.Copies.StoredWins.Order());
        var records = await db.ActivitySessions.AsNoTracking()
            .Where(row => row.Id.Contains("@")).OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(
            [SentenceStudio.Services.SyncIds.ActivitySessionRecord(storedNewer, new DateTime(2026, 11, 2, 8, 0, 0)),
             SentenceStudio.Services.SyncIds.ActivitySessionRecord(storedOlder, webStart)],
            records.Select(row => row.Id));
        // The web's replaced run, still in progress, is kept as abandoned with its state.
        var webRun = records.Single(row => row.Id.StartsWith(storedOlder + "@"));
        Assert.Equal(ActivitySessionStatus.Abandoned, webRun.Status);
        Assert.Equal($$"""{"stored":"{{storedOlder}}"}""", webRun.StateJson);
        // The device's losing finished run is kept as it was.
        var deviceRun = records.Single(row => row.Id.StartsWith(storedNewer + "@"));
        Assert.Equal(ActivitySessionStatus.Completed, deviceRun.Status);
        Assert.Equal($$"""{"device":"{{storedNewer}}"}""", deviceRun.StateJson);
        Assert.Equal(Owner, deviceRun.UserId);

        // A repeated upload keeps no second copy.
        Assert.Empty((await ActivitySessionUploadMerge.PrepareAsync(db, items, Owner, default)).Records);
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
        Assert.Equal(expected, LatestCopyUploadMerge.UploadIsNewer(Parse(upload), Parse(stored)));
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

    /// <summary>
    /// Phase 2b: quiz resume rows travel only to their owner, and a native answer receipt
    /// (device-only, by type or by ID) never travels.
    /// </summary>
    [Fact]
    public async Task NativeDownload_KeepsOnlyTheOwnersQuizRuns_NeverReceipts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);

        var changes = new SyncChangeSet(new SyncAnchor(Guid.NewGuid(), 3), SyncAnchor.Null,
        [
            Item("ActivitySession", $"as:{Owner}:VocabularyReview:plan=p1", ("UserId", Owner),
                ("ActivityType", ActivitySessionTypes.VocabularyReview)),
            Item("ActivitySession", $"as:{Foreign}:VocabularyReview:plan=p1", ("UserId", Foreign),
                ("ActivityType", ActivitySessionTypes.VocabularyReview)),
            Item("ActivitySession", "receipt:typed", ("UserId", Owner),
                ("ActivityType", ActivitySessionTypes.NativeAttemptReceipt)),
            Item("ActivitySession", "untyped-receipt", ("UserId", Owner),
                ("ActivityType", ActivitySessionTypes.NativeSentenceReceipt)),
            Item("ActivitySession", "receipt:any-type", ("UserId", Owner),
                ("ActivityType", ActivitySessionTypes.VocabularyReview)),
        ]);

        var filtered = await NativeOwnerSyncProvider.FilterForOwnerAsync(changes, Owner, db);

        Assert.Equal([$"as:{Owner}:VocabularyReview:plan=p1"],
            filtered.Items.Select(item => item.Values["Id"].Value?.ToString() ?? string.Empty));
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
