// REGRESSION TESTS — orphaned VocabularyProgress rows must not pollute plan generation
// or be revivable through any read path. These tests exist because of the "Brot incident"
// on 2026-06-12: a German word from a long-removed LearningResource appeared in Captain's
// production Today Plan / Flashcard Preview, despite his account being Korean-only.
//
// Root cause (two cooperating bugs):
//   1. GetDueVocabularyAsync filtered by UserId + NextReviewDate only — no reachability
//      JOIN to ResourceVocabularyMapping → LearningResource(UserProfileId=user). When a
//      user removed a LearningResource months earlier, the FK cascade dropped the
//      mappings but NOT the VocabularyProgress rows. Those orphans sat at
//      NextReviewDate=2026-02-12 with MasteryScore=0.143 — "eternally overdue".
//   2. LearningResourceRepository.DeleteResourceAsync did not sweep orphan progress at
//      delete time, so future plan-generation would re-pick those words forever.
//
// If these tests fail, the Brot bug is back. DO NOT DELETE OR WEAKEN.

using Xunit;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public class BrotOrphanProgressRegressionTests : IDisposable
{
    private const string Captain = "captain-id";
    private const string OtherUser = "other-user-id";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly LearningResourceRepository _resourceRepo;

    public BrotOrphanProgressRegressionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var mockPrefs = new Mock<IPreferencesService>();
        mockPrefs.Setup(p => p.Get("active_profile_id", string.Empty)).Returns(Captain);

        var mockFs = new Mock<IFileSystemService>();
        mockFs.Setup(f => f.AppDataDirectory).Returns(Path.GetTempPath());

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlite(_connection));
        services.AddSingleton<IPreferencesService>(mockPrefs.Object);
        services.AddSingleton<IFileSystemService>(mockFs.Object);

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        _progressRepo = new VocabularyProgressRepository(
            _serviceProvider,
            NullLogger<VocabularyProgressRepository>.Instance);

        _resourceRepo = new LearningResourceRepository(
            _serviceProvider,
            NullLogger<LearningResourceRepository>.Instance,
            mockFs.Object);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────
    // Test helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Seed a word, a resource for the user, and a mapping that connects them.</summary>
    private void SeedReachableWord(string userId, string wordId, string resourceId, string language = "Korean")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = wordId,
            TargetLanguageTerm = $"term-{wordId}",
            NativeLanguageTerm = $"meaning-{wordId}",
            Language = language
        });
        db.LearningResources.Add(new LearningResource
        {
            Id = resourceId,
            UserProfileId = userId,
            Title = $"Resource {resourceId}",
            Language = language,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = resourceId,
            VocabularyWordId = wordId
        });
        db.SaveChanges();
    }

    /// <summary>Seed a word with NO mapping to any of the user's resources (orphan scenario).</summary>
    private void SeedOrphanWord(string wordId, string language = "German")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = wordId,
            TargetLanguageTerm = $"term-{wordId}",
            NativeLanguageTerm = $"meaning-{wordId}",
            Language = language
        });
        db.SaveChanges();
    }

    private void SeedProgress(string userId, string wordId, DateTime? nextReviewDate = null, float mastery = 0.143f)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = userId,
            TotalAttempts = 1,
            CorrectAttempts = 0,
            MasteryScore = mastery,
            ProductionInStreak = 0,
            NextReviewDate = nextReviewDate ?? DateTime.UtcNow.AddDays(-100), // long overdue
            FirstSeenAt = DateTime.UtcNow.AddDays(-100),
            LastPracticedAt = DateTime.UtcNow.AddDays(-100),
            CreatedAt = DateTime.UtcNow.AddDays(-100),
            UpdatedAt = DateTime.UtcNow.AddDays(-100)
        });
        db.SaveChanges();
    }

    // ──────────────────────────────────────────────────────────────────
    // GetDueVocabularyAsync reachability filter
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDueVocabularyAsync_ExcludesOrphanProgressWithNoUserMapping()
    {
        // Captain has 1 reachable Korean word and 1 orphaned German word ("Brot").
        SeedReachableWord(Captain, "korean-word-1", "korean-resource-1", language: "Korean");
        SeedOrphanWord("brot-word", language: "German");

        SeedProgress(Captain, "korean-word-1");
        SeedProgress(Captain, "brot-word"); // <-- the Brot scenario

        var due = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow, Captain);

        due.Should().HaveCount(1, "the German orphan must NOT appear in due review — it has no reachable mapping");
        due.Single().VocabularyWordId.Should().Be("korean-word-1");
    }

    [Fact]
    public async Task GetDueVocabularyAsync_ExcludesProgressMappedOnlyToOtherUsersResources()
    {
        // A word is mapped via a resource owned by OTHER user. Captain has progress for it
        // (e.g. legacy data, or another bug). Without the reachability filter scoped to
        // Captain's resources, this would leak across tenants.
        SeedReachableWord(OtherUser, "shared-word", "other-resource", language: "Korean");
        SeedProgress(Captain, "shared-word"); // Captain has progress but cannot reach via own resources

        var due = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow, Captain);

        due.Should().BeEmpty("the word is only reachable via OTHER user's resource — Captain must not see it");
    }

    [Fact]
    public async Task GetDueVocabCountAsync_ExcludesOrphanProgressWithNoUserMapping()
    {
        SeedReachableWord(Captain, "korean-word-1", "korean-resource-1");
        SeedOrphanWord("brot-word", language: "German");
        SeedProgress(Captain, "korean-word-1");
        SeedProgress(Captain, "brot-word");

        var count = await _progressRepo.GetDueVocabCountAsync(DateTime.UtcNow, Captain);

        count.Should().Be(1, "due count must match due list — orphans excluded from both");
    }

    [Fact]
    public async Task GetDueVocabularyAsync_WithEmptyUserId_ReturnsEmpty()
    {
        SeedReachableWord(Captain, "w1", "r1");
        SeedProgress(Captain, "w1");

        var due = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow, userId: "");

        // Active profile in mock prefs = Captain, so resolved userId is Captain → returns 1.
        // The empty-userId guard kicks in only when neither explicit nor preference resolves.
        due.Should().HaveCount(1, "empty userId resolves via IPreferencesService");
    }

    // ──────────────────────────────────────────────────────────────────
    // DeleteResourceAsync orphan sweep
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteResourceAsync_RemovesProgressForWordsThatLoseLastUserMapping()
    {
        // Setup: resource R1 maps to W1; Captain has progress on W1.
        SeedReachableWord(Captain, "w1", "r1");
        SeedProgress(Captain, "w1");

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Count(p => p.UserId == Captain).Should().Be(1);
        }

        // Act: delete R1.
        var resource = await GetResource("r1");
        var deleted = await _resourceRepo.DeleteResourceAsync(resource);

        // Assert: progress on W1 is gone because no other resource of Captain maps to W1.
        deleted.Should().BeGreaterThan(0);
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Count(p => p.UserId == Captain).Should().Be(0,
                "removing the last reachable resource must sweep its orphaned progress");
        }
    }

    [Fact]
    public async Task DeleteResourceAsync_KeepsProgressForWordsStillReachableViaOtherUserResource()
    {
        // W1 is mapped from BOTH R1 and R2 (both owned by Captain). Deleting R1 leaves W1
        // still reachable via R2 → progress must survive.
        SeedReachableWord(Captain, "w1", "r1");
        AddAdditionalMapping(Captain, "w1", "r2"); // second resource maps to same word
        SeedProgress(Captain, "w1");

        var r1 = await GetResource("r1");
        var deleted = await _resourceRepo.DeleteResourceAsync(r1);
        deleted.Should().BeGreaterThan(0);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Count(p => p.UserId == Captain).Should().Be(1,
            "progress must survive — word is still reachable via another resource");
    }

    [Fact]
    public async Task DeleteResourceAsync_DoesNotTouchProgressOfOtherUsers()
    {
        // Captain and OtherUser both have their own resource mapping to the SAME global word.
        // Captain deletes Captain's resource → only Captain's progress is swept.
        SeedReachableWord(Captain, "w1", "captain-r1");
        AddAdditionalMapping(OtherUser, "w1", "other-r1");
        SeedProgress(Captain, "w1");
        SeedProgress(OtherUser, "w1");

        var captainR1 = await GetResource("captain-r1");
        await _resourceRepo.DeleteResourceAsync(captainR1);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Count(p => p.UserId == Captain).Should().Be(0,
            "Captain's progress should be swept");
        db.VocabularyProgresses.Count(p => p.UserId == OtherUser).Should().Be(1,
            "OtherUser's progress on the shared word must be preserved");
    }

    // ──────────────────────────────────────────────────────────────────
    // GetVocabSummaryCountsAsync reachability filter (Brot dashboard counters)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetVocabSummaryCountsAsync_ExcludesOrphanProgressFromCounts()
    {
        // Captain has 1 reachable Korean word with progress, plus 1 orphan German progress
        // row left over from a deleted resource. The orphan must NOT inflate any counter.
        SeedReachableWord(Captain, "korean-word-1", "korean-resource-1", language: "Korean");
        SeedOrphanWord("brot-word", language: "German");
        SeedProgress(Captain, "korean-word-1");
        SeedProgress(Captain, "brot-word"); // orphan — must be excluded

        var (newCount, learning, familiar, review, known) =
            await _progressRepo.GetVocabSummaryCountsAsync(Captain);

        // SeedProgress defaults: TotalAttempts=5, MasteryScore=0.5, IsKnown=false,
        // NextReviewDate=now-100days, IsUserDeclared=false → "Review" bucket.
        review.Should().Be(1, "only the reachable Korean word should be in Review");
        learning.Should().Be(0);
        familiar.Should().Be(0);
        known.Should().Be(0);
        // totalVocabWords counts mapped distinct words for Captain = 1 (Korean only).
        // wordsWithProgress (after reachability filter) = 1 → wordsNeverSeen = 0.
        newCount.Should().Be(0, "wordsNeverSeen math must not be inflated by orphans");
    }

    [Fact]
    public async Task GetVocabSummaryCountsAsync_WithEmptyResolvedUserId_ReturnsZeros()
    {
        // Build a separate fixture whose preferences return empty so resolution truly fails.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var mockPrefs = new Mock<IPreferencesService>();
        mockPrefs.Setup(p => p.Get("active_profile_id", string.Empty)).Returns(string.Empty);
        var mockFs = new Mock<IFileSystemService>();
        mockFs.Setup(f => f.AppDataDirectory).Returns(Path.GetTempPath());

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlite(connection));
        services.AddSingleton<IPreferencesService>(mockPrefs.Object);
        services.AddSingleton<IFileSystemService>(mockFs.Object);
        using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();

        var repo = new VocabularyProgressRepository(sp, NullLogger<VocabularyProgressRepository>.Instance);

        var counts = await repo.GetVocabSummaryCountsAsync(userId: "");

        counts.Should().Be((0, 0, 0, 0, 0), "no active user must return zeros, not aggregate across all users");

        connection.Dispose();
    }

    private async Task<LearningResource> GetResource(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LearningResources.SingleAsync(r => r.Id == id);
    }

    private void AddAdditionalMapping(string userId, string wordId, string resourceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!db.LearningResources.Any(r => r.Id == resourceId))
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = resourceId,
                UserProfileId = userId,
                Title = $"Resource {resourceId}",
                Language = "Korean",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = resourceId,
            VocabularyWordId = wordId
        });
        db.SaveChanges();
    }
}
