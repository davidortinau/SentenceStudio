using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.Services.Progress;

public sealed class CrossProfileAdHocProgressTests : IDisposable
{
    private const string UserA = "adhoc-boundary-user-a";
    private const string UserB = "adhoc-boundary-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly CollectingLoggerProvider _logs;
    private string _activeUserId = UserA;

    private readonly LearningResource _resourceA;
    private readonly LearningResource _resourceA2;
    private readonly LearningResource _resourceB;
    private readonly SkillProfile _skillA;
    private readonly SkillProfile _skillB;
    private readonly VocabularyWord _wordA;
    private readonly VocabularyWord _wordA2;
    private readonly VocabularyWord _wordB;

    public CrossProfileAdHocProgressTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>()))
            .Returns(() => _activeUserId);
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());

        _logs = new CollectingLoggerProvider();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_logs);
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        services.AddSingleton(preferences.Object);
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton(Mock.Of<ILlmPlanGenerationService>());
        services.AddScoped<UserProfileRepository>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();
        services.AddScoped<UserActivityRepository>();
        services.AddScoped<VocabularyProgressRepository>();
        services.AddScoped<VocabularyLearningContextRepository>();
        services.AddScoped<VocabularyProgressService>();
        services.AddSingleton<ProgressCacheService>();
        services.AddScoped<ProgressService>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.AddRange(CreateUser(UserA), CreateUser(UserB));

        _resourceA = CreateResource(UserA);
        _resourceA2 = CreateResource(UserA);
        _resourceB = CreateResource(UserB);
        _skillA = CreateSkill(UserA);
        _skillB = CreateSkill(UserB);
        _wordA = CreateWord();
        _wordA2 = CreateWord();
        _wordB = CreateWord();

        db.LearningResources.AddRange(_resourceA, _resourceA2, _resourceB);
        db.SkillProfiles.AddRange(_skillA, _skillB);
        db.VocabularyWords.AddRange(_wordA, _wordA2, _wordB);
        db.ResourceVocabularyMappings.AddRange(
            CreateMapping(_resourceA.Id, _wordA.Id),
            CreateMapping(_resourceA2.Id, _wordA2.Id),
            CreateMapping(_resourceB.Id, _wordB.Id));
        db.SaveChanges();
    }

    [Fact]
    public async Task OwnedAdHocTuplePersists()
    {
        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.VocabularyReview,
            _resourceA.Id,
            _skillA.Id,
            new[] { _wordA.Id });

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(c => c.PlanItemId == planItemId);
        row.UserProfileId.Should().Be(UserA);
        row.ResourceId.Should().Be(_resourceA.Id);
        row.SkillId.Should().Be(_skillA.Id);
    }

    [Fact]
    public async Task SameUserFlowWithoutOptionalReferencesStillPersists()
    {
        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.Translation,
            resourceId: null,
            skillId: null);

        (await CountCompletionRowsAsync(planItemId)).Should().Be(1);
    }

    [Fact]
    public async Task ForeignResourceRefusesWithZeroRows()
    {
        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.VocabularyReview,
            _resourceB.Id,
            _skillA.Id,
            new[] { _wordA.Id });

        planItemId.Should().BeEmpty();
        (await CountCompletionRowsAsync(planItemId)).Should().Be(0);
        _logs.HasWarningContaining("mixed-owner", "activity context").Should().BeTrue();
    }

    [Fact]
    public async Task ForeignSkillRefusesWithZeroRows()
    {
        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.VocabularyReview,
            _resourceA.Id,
            _skillB.Id,
            new[] { _wordA.Id });

        planItemId.Should().BeEmpty();
        (await CountCompletionRowsAsync(planItemId)).Should().Be(0);
    }

    [Fact]
    public async Task ForeignVocabularyRefusesWithZeroRows()
    {
        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.VocabularyReview,
            _resourceA.Id,
            _skillA.Id,
            new[] { _wordB.Id });

        planItemId.Should().BeEmpty();
        (await CountCompletionRowsAsync(planItemId)).Should().Be(0);
    }

    [Fact]
    public async Task MixedVocabularySetRefusesWithoutPartialSuccess()
    {
        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.VocabularyReview,
            _resourceA.Id,
            _skillA.Id,
            new[] { _wordA.Id, _wordB.Id });

        planItemId.Should().BeEmpty();
        (await CountCompletionRowsAsync(planItemId)).Should().Be(0);
    }

    [Fact]
    public async Task WordOwnedThroughDifferentResourceCannotBridgeSpecifiedResource()
    {
        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.VocabularyReview,
            _resourceA.Id,
            _skillA.Id,
            new[] { _wordA2.Id });

        planItemId.Should().BeEmpty();
        (await CountCompletionRowsAsync(planItemId)).Should().Be(0);
    }

    [Fact]
    public async Task EmptyUserRefusesWithZeroRows()
    {
        _activeUserId = string.Empty;

        var planItemId = await Service.StartAdHocSessionAsync(
            PlanActivityType.VocabularyReview,
            _resourceA.Id,
            _skillA.Id,
            new[] { _wordA.Id });

        planItemId.Should().BeEmpty();
        (await CountCompletionRowsAsync(planItemId)).Should().Be(0);
        _logs.HasWarningContaining("No user profile", "cannot start").Should().BeTrue();
    }

    private ProgressService Service => _provider.GetRequiredService<ProgressService>();

    private async Task<int> CountCompletionRowsAsync(string planItemId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DailyPlanCompletions.AsNoTracking()
            .CountAsync(c => c.PlanItemId == planItemId);
    }

    private static UserProfile CreateUser(string id) => new()
    {
        Id = id,
        Name = id,
        NativeLanguage = "English",
        TargetLanguage = "Korean",
        PreferredSessionMinutes = 20,
        CreatedAt = DateTime.UtcNow,
    };

    private static LearningResource CreateResource(string userId) => new()
    {
        Title = $"Resource {Guid.NewGuid()}",
        MediaType = "Vocabulary List",
        Language = "Korean",
        UserProfileId = userId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static SkillProfile CreateSkill(string userId) => new()
    {
        Title = $"Skill {Guid.NewGuid()}",
        UserProfileId = userId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static VocabularyWord CreateWord() => new()
    {
        TargetLanguageTerm = $"target-{Guid.NewGuid()}",
        NativeLanguageTerm = $"native-{Guid.NewGuid()}",
        Language = "Korean",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ResourceVocabularyMapping CreateMapping(string resourceId, string wordId) => new()
    {
        ResourceId = resourceId,
        VocabularyWordId = wordId,
    };

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }
}
