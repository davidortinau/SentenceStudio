using System.Data.Common;
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
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.Data;

public sealed class CrossProfileRepositoryBoundaryTests : IDisposable
{
    private const string UserA = "boundary-user-a";
    private const string UserB = "boundary-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly SelectCommandCounter _queryCounter;
    private readonly CollectingLoggerProvider _logs;
    private string _activeUserId = UserA;

    public CrossProfileRepositoryBoundaryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _queryCounter = new SelectCommandCounter();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(_connection)
                .AddInterceptors(_queryCounter)
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
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.AddRange(CreateUser(UserA), CreateUser(UserB));
        db.SaveChanges();
        _queryCounter.Reset();
    }

    [Fact]
    public async Task ExactReads_EmptyUserFailClosedBeforeAnyQuery()
    {
        var resource = SeedResource(UserA);
        var skill = SeedSkill(UserA);
        _activeUserId = string.Empty;
        _queryCounter.Reset();

        var resourceResult = await ResourceRepository.GetResourceAsync(resource.Id);
        var skillResult = await SkillRepository.GetSkillProfileAsync(skill.Id);
        var skillAliasResult = await SkillRepository.GetAsync(skill.Id);

        resourceResult.Should().BeNull();
        skillResult.Should().BeNull();
        skillAliasResult.Should().BeNull();
        _queryCounter.SelectCount.Should().Be(0, "empty-user exact reads must not reach the database");
        _logs.HasWarningContaining("GetResourceAsync", "cross-tenant").Should().BeTrue();
        _logs.HasWarningContaining("GetSkillProfileAsync", "cross-tenant").Should().BeTrue();
        _logs.HasWarningContaining("GetAsync", "cross-tenant").Should().BeTrue();
    }

    [Fact]
    public async Task ExactReads_OnlyReturnEntitiesOwnedByExplicitUser()
    {
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var skillA = SeedSkill(UserA);
        var skillB = SeedSkill(UserB);

        (await ResourceRepository.GetResourceAsync(resourceA.Id, UserA)).Should().NotBeNull();
        (await ResourceRepository.GetResourceAsync(resourceB.Id, UserA)).Should().BeNull();
        (await ResourceRepository.GetResourceAsync("missing-resource", UserA)).Should().BeNull();

        (await SkillRepository.GetSkillProfileAsync(skillA.Id, UserA)).Should().NotBeNull();
        (await SkillRepository.GetSkillProfileAsync(skillB.Id, UserA)).Should().BeNull();
        (await SkillRepository.GetAsync(skillA.Id, UserA)).Should().NotBeNull();
        (await SkillRepository.GetAsync(skillB.Id, UserA)).Should().BeNull();
        (await SkillRepository.GetAsync("missing-skill", UserA)).Should().BeNull();
    }

    [Fact]
    public async Task ResourceWrites_EmptyAndForeignUserRefuseWithoutMutation()
    {
        var owned = SeedResource(UserA, title: "Owned");
        var foreign = SeedResource(UserB, title: "Foreign");

        foreign.Title = "Attempted foreign update";
        (await ResourceRepository.SaveResourceAsync(foreign, UserA)).Should().BeEmpty();
        (await ResourceRepository.DeleteResourceAsync(foreign, UserA)).Should().Be(0);

        _activeUserId = string.Empty;
        _queryCounter.Reset();
        var unsaved = new LearningResource { Title = "No owner" };
        (await ResourceRepository.SaveResourceAsync(unsaved)).Should().BeEmpty();
        (await ResourceRepository.DeleteResourceAsync(owned)).Should().Be(0);
        _queryCounter.SelectCount.Should().Be(0, "empty-user writes and deletes must refuse before querying");

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.LearningResources.AsNoTracking().SingleAsync(r => r.Id == foreign.Id))
            .Title.Should().Be("Foreign");
        (await db.LearningResources.AsNoTracking().AnyAsync(r => r.Id == owned.Id)).Should().BeTrue();
        (await db.LearningResources.AsNoTracking().AnyAsync(r => r.Id == unsaved.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ResourceWrites_OwnedUpdateCreateAndDeleteStayWithinOwner()
    {
        var owned = SeedResource(UserA, title: "Before");
        owned.Title = "After";
        owned.UserProfileId = UserB;

        (await ResourceRepository.SaveResourceAsync(owned, UserA)).Should().Be(owned.Id);
        var created = new LearningResource { Title = "Created", UserProfileId = UserB };
        (await ResourceRepository.SaveResourceAsync(created, UserA)).Should().Be(created.Id);
        (await ResourceRepository.DeleteResourceAsync(owned, UserA)).Should().BeGreaterThan(0);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedCreated = await db.LearningResources.AsNoTracking().SingleAsync(r => r.Id == created.Id);
        persistedCreated.UserProfileId.Should().Be(UserA);
        (await db.LearningResources.AsNoTracking().AnyAsync(r => r.Id == owned.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task SkillWrites_EmptyForeignAndOwnedCasesFailClosed()
    {
        var owned = SeedSkill(UserA, title: "Before");
        var foreign = SeedSkill(UserB, title: "Foreign");

        foreign.Title = "Attempted foreign update";
        (await SkillRepository.SaveAsync(foreign, UserA)).Should().BeEmpty();
        (await SkillRepository.DeleteAsync(foreign, UserA)).Should().Be(0);

        owned.Title = "After";
        owned.UserProfileId = UserB;
        (await SkillRepository.SaveAsync(owned, UserA)).Should().Be(owned.Id);

        var created = new SkillProfile { Title = "Created", UserProfileId = UserB };
        (await SkillRepository.SaveAsync(created, UserA)).Should().Be(created.Id);

        _activeUserId = string.Empty;
        _queryCounter.Reset();
        (await SkillRepository.SaveAsync(new SkillProfile { Title = "No owner" })).Should().BeEmpty();
        (await SkillRepository.DeleteAsync(owned)).Should().Be(0);
        _queryCounter.SelectCount.Should().Be(0, "empty-user skill writes must refuse before querying");

        _activeUserId = UserA;
        (await SkillRepository.DeleteAsync(owned)).Should().BeGreaterThan(0);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedForeign = await db.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == foreign.Id);
        persistedForeign.Title.Should().Be("Foreign");
        (await db.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == created.Id))
            .UserProfileId.Should().Be(UserA);
        (await db.SkillProfiles.AsNoTracking().AnyAsync(s => s.Id == owned.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ResourceVocabularyWrites_RejectForeignAndMixedIdentifiersWithoutPartialMutation()
    {
        var target = SeedResource(UserA);
        var ownedSource = SeedResource(UserA);
        var foreignSource = SeedResource(UserB);
        var ownedWord = SeedWord();
        var foreignWord = SeedWord();
        Map(ownedSource.Id, ownedWord.Id);
        Map(foreignSource.Id, foreignWord.Id);
        var mixedResource = new LearningResource
        {
            Title = "Mixed resource",
            Vocabulary = new List<VocabularyWord> { ownedWord, foreignWord },
        };

        (await ResourceRepository.AddVocabularyToResourceAsync(target.Id, foreignWord.Id, UserA))
            .Should().BeFalse();
        (await ResourceRepository.SaveResourceAsync(mixedResource, UserA)).Should().BeEmpty();
        (await ResourceRepository.BulkAssociateWordsWithResourceAsync(
            target.Id,
            new List<string> { ownedWord.Id, foreignWord.Id },
            UserA)).Should().BeFalse();
        (await ResourceRepository.RemoveVocabularyFromResourceAsync(foreignSource.Id, foreignWord.Id, UserA))
            .Should().BeFalse();
        (await ResourceRepository.BulkRemoveWordsFromResourceAsync(
            foreignSource.Id,
            new List<string> { foreignWord.Id },
            UserA)).Should().BeFalse();
        (await ResourceRepository.GetVocabularyWordsByResourceAsync(foreignSource.Id, UserA))
            .Should().BeEmpty();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ResourceVocabularyMappings.AsNoTracking()
            .CountAsync(m => m.ResourceId == target.Id)).Should().Be(0);
        (await db.LearningResources.AsNoTracking()
            .CountAsync(r => r.Id == mixedResource.Id)).Should().Be(0);
        (await db.ResourceVocabularyMappings.AsNoTracking()
            .CountAsync(m => m.ResourceId == foreignSource.Id && m.VocabularyWordId == foreignWord.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task TargetTermLookup_ReusesOnlyReachableOrUnownedWords()
    {
        const string commonTerm = "공통";
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var wordA = SeedWord(commonTerm);
        Map(resourceA.Id, wordA.Id);

        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserA))
            ?.Id.Should().Be(wordA.Id);
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            .Should().BeNull("a foreign-only mapping is not reusable");

        var unowned = SeedWord(commonTerm);
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            ?.Id.Should().Be(unowned.Id);

        (await ResourceRepository.AddVocabularyToResourceAsync(resourceB.Id, unowned.Id, UserB))
            .Should().BeTrue();
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            ?.Id.Should().Be(unowned.Id);
    }

    [Fact]
    public async Task CommonTermCreation_ForSecondProfileCreatesDistinctReachableWord()
    {
        const string commonTerm = "사람";
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var foreignWord = SeedWord(commonTerm);
        Map(resourceA.Id, foreignWord.Id);

        var lookup = await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB);
        lookup.Should().BeNull();

        var newWord = new VocabularyWord
        {
            TargetLanguageTerm = commonTerm,
            NativeLanguageTerm = "person",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        (await ResourceRepository.SaveWordAsync(newWord)).Should().BeGreaterThan(0);
        (await ResourceRepository.AddVocabularyToResourceAsync(resourceB.Id, newWord.Id, UserB))
            .Should().BeTrue();

        newWord.Id.Should().NotBe(foreignWord.Id);
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            ?.Id.Should().Be(newWord.Id);
    }

    [Fact]
    public async Task VocabularyGraph_ContainsOnlyExplicitUsersResources()
    {
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var sharedWord = SeedWord();
        Map(resourceA.Id, sharedWord.Id);
        Map(resourceB.Id, sharedWord.Id);

        var words = await ResourceRepository.GetAllVocabularyWordsWithResourcesAsync(UserA);

        words.Should().ContainSingle(word => word.Id == sharedWord.Id);
        var returned = words.Single(word => word.Id == sharedWord.Id);
        returned.LearningResources.Should().ContainSingle()
            .Which.Id.Should().Be(resourceA.Id);
    }

    private LearningResourceRepository ResourceRepository =>
        _provider.GetRequiredService<LearningResourceRepository>();

    private SkillProfileRepository SkillRepository =>
        _provider.GetRequiredService<SkillProfileRepository>();

    private static UserProfile CreateUser(string id) => new()
    {
        Id = id,
        Name = id,
        NativeLanguage = "English",
        TargetLanguage = "Korean",
        PreferredSessionMinutes = 20,
        CreatedAt = DateTime.UtcNow,
    };

    private LearningResource SeedResource(string userId, string? title = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = new LearningResource
        {
            Title = title ?? $"Resource {Guid.NewGuid()}",
            MediaType = "Vocabulary List",
            Language = "Korean",
            UserProfileId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.LearningResources.Add(resource);
        db.SaveChanges();
        return resource;
    }

    private SkillProfile SeedSkill(string userId, string? title = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var skill = new SkillProfile
        {
            Title = title ?? $"Skill {Guid.NewGuid()}",
            UserProfileId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.SkillProfiles.Add(skill);
        db.SaveChanges();
        return skill;
    }

    private VocabularyWord SeedWord(string? targetTerm = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = new VocabularyWord
        {
            TargetLanguageTerm = targetTerm ?? $"target-{Guid.NewGuid()}",
            NativeLanguageTerm = $"native-{Guid.NewGuid()}",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.VocabularyWords.Add(word);
        db.SaveChanges();
        return word;
    }

    private void Map(string resourceId, string vocabularyWordId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            ResourceId = resourceId,
            VocabularyWordId = vocabularyWordId,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        public int SelectCount { get; private set; }

        public void Reset() => SelectCount = 0;

        private void Inspect(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                SelectCount++;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Inspect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }
    }
}
