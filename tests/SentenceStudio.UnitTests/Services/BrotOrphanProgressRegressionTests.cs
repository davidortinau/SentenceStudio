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
// Fix 1 (the reachability filter on every due read) is what keeps a word out of the plan, and
// it stays. Fix 2 (the delete-time sweep) was retired on 2026-11-06 (review of bf701c42,
// finding 3; B85): under the owner-only rule a word stays in its owner's vocabulary when its
// last resource goes, so its progress and learning history stay with it. The delete tests
// below now prove that history is kept AND that the kept word is not due in plans.
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
            UserProfileId = userId,
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
        // Captain's own word, left behind when its resource was deleted.
        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = wordId,
            UserProfileId = Captain,
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
    // The one ownership rule (Captain 2026-11-06): progress follows its word
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressReads_ExcludeWordsNotRecordedToTheLearner_EvenInTheirResource()
    {
        // Three words in Captain's own resource, each with Captain's due progress: one recorded
        // to Captain, one to another learner, one to no one (legacy). Only Captain's own counts.
        SeedReachableWord(Captain, "own-word", "captain-resource", language: "Korean");
        foreach (var (wordId, owner) in new[] { ("their-word", (string?)OtherUser), ("legacy-word", null) })
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = wordId, UserProfileId = Captain, TargetLanguageTerm = $"term-{wordId}",
                NativeLanguageTerm = $"meaning-{wordId}", Language = "Korean"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(), ResourceId = "captain-resource", VocabularyWordId = wordId
            });
            db.SaveChanges();
            db.VocabularyWords.Where(w => w.Id == wordId)
                .ExecuteUpdate(set => set.SetProperty(w => w.UserProfileId, owner));
        }
        foreach (var wordId in new[] { "own-word", "their-word", "legacy-word" })
            SeedProgress(Captain, wordId);

        (await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow, Captain))
            .Select(p => p.VocabularyWordId).Should().Equal("own-word");
        (await _progressRepo.GetDueVocabCountAsync(DateTime.UtcNow, Captain)).Should().Be(1);
        (await _progressRepo.ListAsync()).Select(p => p.VocabularyWordId).Should().Equal("own-word");
        (await _progressRepo.GetAllForUserAsync(Captain)).Select(p => p.VocabularyWordId).Should().Equal("own-word");
        var (newCount, _, _, review, _) = await _progressRepo.GetVocabSummaryCountsAsync(Captain);
        review.Should().Be(1);
        newCount.Should().Be(0, "hidden words are neither counted as words nor as progress");
        (await _progressRepo.GetUnseenMappedVocabularyAsync(Captain)).Should().BeEmpty();
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
    // DeleteResourceAsync keeps the history of words it unlinks (the sweep is retired)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteResourceAsync_KeepsTheLearnersHistoryOnWordsThatLoseTheirLastMapping_ButNotDue()
    {
        // Setup: resource R1 maps to W1; Captain has long-overdue progress and history on W1.
        SeedReachableWord(Captain, "w1", "r1");
        SeedProgress(Captain, "w1");
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var progressId = db.VocabularyProgresses.Single(p => p.UserId == Captain).Id;
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = "w1-history", VocabularyProgressId = progressId, Activity = "VocabularyQuiz",
                InputMode = "TextEntry", LearnedAt = DateTime.UtcNow.AddDays(-100),
                CreatedAt = DateTime.UtcNow.AddDays(-100), UpdatedAt = DateTime.UtcNow.AddDays(-100)
            });
            db.SaveChanges();
        }
        (await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow, Captain)).Should().HaveCount(1);

        // Act: delete R1, W1's only resource.
        var resource = await GetResource("r1");
        var deleted = await _resourceRepo.DeleteResourceAsync(resource);

        // Assert: W1 stays Captain's word, so its progress and history stay (owner-only rule,
        // 2026-11-06); the reachability filter keeps it out of due review and the plan.
        deleted.Should().BeGreaterThan(0);
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Any(w => w.Id == "w1" && w.UserProfileId == Captain).Should().BeTrue();
            db.VocabularyProgresses.Count(p => p.UserId == Captain && p.VocabularyWordId == "w1").Should().Be(1,
                "the word stays the learner's, so its history stays");
            db.VocabularyLearningContexts.Any(c => c.Id == "w1-history").Should().BeTrue();
        }
        (await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow, Captain)).Should().BeEmpty(
            "a word no resource of the learner's links is not picked for due review (Brot)");
        (await _progressRepo.GetDueVocabCountAsync(DateTime.UtcNow, Captain)).Should().Be(0);
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
        // Captain deletes Captain's resource: neither learner's progress is removed.
        SeedReachableWord(Captain, "w1", "captain-r1");
        AddAdditionalMapping(OtherUser, "w1", "other-r1");
        SeedProgress(Captain, "w1");
        SeedProgress(OtherUser, "w1");

        var captainR1 = await GetResource("captain-r1");
        await _resourceRepo.DeleteResourceAsync(captainR1);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Count(p => p.UserId == Captain).Should().Be(1,
            "Captain's progress stays with the word, which stays Captain's");
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
