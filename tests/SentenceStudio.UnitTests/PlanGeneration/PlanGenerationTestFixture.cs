using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.Logging;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Integration test fixture that sets up an in-memory SQLite database
/// with ApplicationDbContext and all required DI registrations for
/// DeterministicPlanBuilder integration tests.
/// </summary>
public class PlanGenerationTestFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    public ServiceProvider ServiceProvider { get; }

    public const string TestUserId = "test-user-1";
    public const string TestUserName = "Test Captain";

    /// <summary>Captured log records (message + structured state) for this fixture.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public PlanGenerationTestFixture()
        : this(registerSmartResourceService: false)
    {
    }

    /// <param name="registerSmartResourceService">
    /// When true, <c>SmartResourceService</c> is registered so
    /// <c>UserProfileRepository.EnsureSmartResourcesAsync</c> actually writes
    /// smart-resource rows. Only the pure-preview no-write tests need this;
    /// leaving it off keeps every other plan test on the historical no-seed
    /// behavior. Private because xUnit class fixtures may declare exactly one
    /// public constructor — use <see cref="CreateWithSmartResourceSeeding"/>.
    /// </param>
    private PlanGenerationTestFixture(bool registerSmartResourceService)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        // ApplicationDbContext with shared in-memory SQLite connection
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        // IPreferencesService — returns test user ID
        var mockPreferences = new Mock<IPreferencesService>();
        mockPreferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(TestUserId);
        services.AddSingleton(mockPreferences.Object);

        // ISyncService — no-op for tests
        services.AddSingleton<ISyncService>(new NoOpSyncService());

        // IFileSystemService — mock
        var mockFileSystem = new Mock<IFileSystemService>();
        mockFileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());
        services.AddSingleton(mockFileSystem.Object);

        // Repositories
        services.AddScoped<UserProfileRepository>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();
        services.AddScoped<VocabularyProgressRepository>();

        // The builder under test
        services.AddScoped<DeterministicPlanBuilder>();
        services.AddScoped<GeneratedPlanValidator>();

        if (registerSmartResourceService)
        {
            services.AddScoped<SentenceStudio.Services.SmartResourceService>();
        }

        // Live-clock date context — preserves the prior "today == real now" semantics
        // that existing tests depend on. Tests that need a frozen clock can
        // replace this registration with PlanDateContext(TimeZoneInfo.Utc, fixedNow).
        services.AddScoped<SentenceStudio.Services.Plans.IPlanDateContext>(_ =>
            new SentenceStudio.Services.Plans.PlanDateContext(TimeZoneInfo.Utc));

        // Logging — every record is captured so privacy tests can assert an
        // identifier never reaches a message or a structured field.
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(Logs);
        });

        ServiceProvider = services.BuildServiceProvider();

        // Create schema
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
    }

    /// <summary>
    /// Creates a fixture whose <c>UserProfileRepository.EnsureSmartResourcesAsync</c>
    /// really writes, so pure-preview no-write behavior is observable.
    /// </summary>
    public static PlanGenerationTestFixture CreateWithSmartResourceSeeding() => new(registerSmartResourceService: true);

    /// <summary>Seeds a user profile. Must be called before BuildPlanAsync.</summary>
    public void SeedUserProfile(int sessionMinutes = 20, string? userId = null)
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.UserProfiles.Add(new UserProfile
        {
            Id = userId ?? TestUserId,
            Name = TestUserName,
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = sessionMinutes,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    /// <summary>Seeds a learning resource.</summary>
    public LearningResource SeedResource(
        string? id = null,
        string title = "Test Resource",
        string mediaType = "Podcast",
        string? transcript = "Some transcript text",
        string? mediaUrl = null,
        string language = "Korean",
        int vocabWordCount = 0,
        string? userProfileId = null)
    {
        id ??= Guid.NewGuid().ToString();

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var resource = new LearningResource
        {
            Id = id,
            Title = title,
            MediaType = mediaType,
            Transcript = transcript,
            MediaUrl = mediaUrl,
            Language = language,
            UserProfileId = userProfileId ?? TestUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.LearningResources.Add(resource);

        // Create vocabulary words and mappings
        for (int i = 0; i < vocabWordCount; i++)
        {
            var word = new VocabularyWord
            {
                Id = Guid.NewGuid().ToString(),
                TargetLanguageTerm = $"word_{id}_{i}",
                NativeLanguageTerm = $"word_en_{id}_{i}",
                Language = language,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.VocabularyWords.Add(word);

            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = id,
                VocabularyWordId = word.Id
            });
        }

        db.SaveChanges();
        return resource;
    }

    /// <summary>Seeds a skill profile.</summary>
    public SkillProfile SeedSkill(string? id = null, string title = "Test Skill")
    {
        id ??= Guid.NewGuid().ToString();

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var skill = new SkillProfile
        {
            Id = id,
            Title = title,
            Description = $"{title} description",
            Language = "Korean",
            UserProfileId = TestUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.SkillProfiles.Add(skill);
        db.SaveChanges();
        return skill;
    }

    /// <summary>Seeds vocabulary progress for a word, optionally linking it to a resource.</summary>
    public VocabularyProgress SeedVocabularyProgress(
        string? vocabularyWordId = null,
        float masteryScore = 0.3f,
        int productionInStreak = 0,
        int currentStreak = 0,
        int totalAttempts = 5,
        int correctAttempts = 2,
        DateTime? nextReviewDate = null,
        string? resourceId = null,
        string? tags = null)
    {
        vocabularyWordId ??= Guid.NewGuid().ToString();

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Ensure the VocabularyWord exists
        var existingWord = db.VocabularyWords.Find(vocabularyWordId);
        if (existingWord == null)
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = vocabularyWordId,
                TargetLanguageTerm = $"term_{vocabularyWordId[..8]}",
                NativeLanguageTerm = $"en_{vocabularyWordId[..8]}",
                Tags = tags,
                Language = "Korean",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else if (tags != null)
        {
            existingWord.Tags = tags;
        }

        var progress = new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = vocabularyWordId,
            UserId = TestUserId,
            MasteryScore = masteryScore,
            ProductionInStreak = productionInStreak,
            CurrentStreak = currentStreak,
            TotalAttempts = totalAttempts,
            CorrectAttempts = correctAttempts,
            NextReviewDate = nextReviewDate ?? DateTime.UtcNow.AddDays(-1),
            ReviewInterval = 1,
            EaseFactor = 2.5f,
            FirstSeenAt = DateTime.UtcNow.AddDays(-7),
            LastPracticedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.VocabularyProgresses.Add(progress);

        // Create learning context linking to a resource
        if (!string.IsNullOrEmpty(resourceId))
        {
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = Guid.NewGuid().ToString(),
                VocabularyProgressId = progress.Id,
                LearningResourceId = resourceId,
                Activity = "VocabularyQuiz",
                InputMode = "MultipleChoice",
                WasCorrect = true,
                LearnedAt = DateTime.UtcNow.AddDays(-3),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        db.SaveChanges();
        return progress;
    }

    /// <summary>Seeds a daily plan completion record.</summary>
    public DailyPlanCompletion SeedCompletion(
        DateTime date,
        string activityType,
        string? resourceId = null,
        string? skillId = null,
        bool isCompleted = true)
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var completion = new DailyPlanCompletion
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = TestUserId,
            Date = date.Date,
            PlanItemId = Guid.NewGuid().ToString(),
            ActivityType = activityType,
            ResourceId = resourceId,
            SkillId = skillId,
            IsCompleted = isCompleted,
            CompletedAt = isCompleted ? date : null,
            MinutesSpent = 10,
            EstimatedMinutes = 10,
            Priority = 1,
            TitleKey = $"plan_item_{activityType.ToLower()}_title",
            DescriptionKey = $"plan_item_{activityType.ToLower()}_desc",
            Rationale = "Test rationale",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.DailyPlanCompletions.Add(completion);
        db.SaveChanges();
        return completion;
    }

    /// <summary>Clears all test data for a fresh test run.</summary>
    public void ClearAllData()
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.VocabularyLearningContexts.RemoveRange(db.VocabularyLearningContexts);
        db.VocabularyProgresses.RemoveRange(db.VocabularyProgresses);
        db.MinimalPairAttempts.RemoveRange(db.MinimalPairAttempts);
        db.MinimalPairSessions.RemoveRange(db.MinimalPairSessions);
        db.MinimalPairs.RemoveRange(db.MinimalPairs);
        db.PhraseConstituents.RemoveRange(db.PhraseConstituents);
        db.ExampleSentences.RemoveRange(db.ExampleSentences);
        db.ResourceVocabularyMappings.RemoveRange(db.ResourceVocabularyMappings);
        db.DailyPlanCompletions.RemoveRange(db.DailyPlanCompletions);
        db.VocabularyWords.RemoveRange(db.VocabularyWords);
        db.LearningResources.RemoveRange(db.LearningResources);
        db.SkillProfiles.RemoveRange(db.SkillProfiles);
        db.UserProfiles.RemoveRange(db.UserProfiles);
        db.SaveChanges();
    }

    /// <summary>Creates a DeterministicPlanBuilder from a fresh scope.</summary>
    public DeterministicPlanBuilder CreateBuilder()
    {
        var scope = ServiceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DeterministicPlanBuilder>();
    }

    /// <summary>Creates a GeneratedPlanValidator with the fixture's IPlanDateContext.</summary>
    public GeneratedPlanValidator CreateValidator()
    {
        var scope = ServiceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<GeneratedPlanValidator>();
    }

    /// <summary>Gets all learning resources as a lookup dictionary.</summary>
    public Dictionary<string, LearningResource> GetResourceLookup()
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.LearningResources.AsNoTracking().ToDictionary(r => r.Id);
    }

    /// <summary>Gets recent completions for the last N days.</summary>
    public List<DailyPlanCompletion> GetRecentCompletions(int days = 14)
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var since = DateTime.UtcNow.Date.AddDays(-days);
        return db.DailyPlanCompletions.Where(c => c.Date >= since).ToList();
    }

    /// <summary>
    /// Gets the vocabulary word IDs that belong to a resource (via ResourceVocabularyMapping).
    /// </summary>
    public List<string> GetResourceVocabularyWordIds(string resourceId)
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.ResourceVocabularyMappings
            .Where(rvm => rvm.ResourceId == resourceId)
            .Select(rvm => rvm.VocabularyWordId)
            .ToList();
    }

    /// <summary>Counts smart-resource rows, the only database write on the plan-generation path.</summary>
    public int CountSmartResources()
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.LearningResources.Count(r => r.IsSmartResource);
    }

    /// <summary>Counts every row the plan-generation path could plausibly write.</summary>
    public (int Resources, int Words, int Progress, int Completions) CountAllRows()
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (
            db.LearningResources.Count(),
            db.VocabularyWords.Count(),
            db.VocabularyProgresses.Count(),
            db.DailyPlanCompletions.Count());
    }

    public void Dispose()
    {
        ServiceProvider?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }
}
