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

    public PlanGenerationTestFixture()
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

        // IActiveUserProvider — repos resolve user ID through this abstraction
        services.AddSingleton<IActiveUserProvider>(new PreferencesActiveUserProvider(mockPreferences.Object));

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

        // Logging
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        ServiceProvider = services.BuildServiceProvider();

        // Create schema
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
    }

    /// <summary>Seeds a user profile. Must be called before BuildPlanAsync.</summary>
    public void SeedUserProfile(int sessionMinutes = 20)
    {
        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.UserProfiles.Add(new UserProfile
        {
            Id = TestUserId,
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
        int vocabWordCount = 0)
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
            UserProfileId = TestUserId,
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

    /// <summary>Creates a GeneratedPlanValidator.</summary>
    public GeneratedPlanValidator CreateValidator() => new();

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

    public void Dispose()
    {
        ServiceProvider?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }
}
