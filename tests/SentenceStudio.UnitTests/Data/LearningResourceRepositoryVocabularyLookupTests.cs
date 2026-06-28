using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

public sealed class LearningResourceRepositoryVocabularyLookupTests : IDisposable
{
    private const string UserA = "lookup-user-a";
    private const string UserB = "lookup-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly CollectingLoggerProvider _logs;
    private string _activeUserId = UserA;

    public LearningResourceRepositoryVocabularyLookupTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(() => _activeUserId);

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

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        SeedUser(db, UserA);
        SeedUser(db, UserB);
        db.SaveChanges();
    }

    [Fact]
    public async Task SearchVocabularyWordsForResource_HappyPath_ReturnsMatchingLanguageWordsForActiveUser()
    {
        var resourceUnderEdit = SeedResource(UserA, "Korean");
        var sourceResource = SeedResource(UserA, "Korean");
        var otherUserResource = SeedResource(UserB, "Korean");

        var prefixMatch = SeedWord("apple", "사과", "Korean");
        var containsMatch = SeedWord("pineapple", "파인애플", "Korean");
        var otherUserMatch = SeedWord("application", "신청", "Korean");
        var nonMatch = SeedWord("banana", "바나나", "Korean");

        Map(sourceResource.Id, prefixMatch.Id);
        Map(sourceResource.Id, containsMatch.Id);
        Map(sourceResource.Id, nonMatch.Id);
        Map(otherUserResource.Id, otherUserMatch.Id);

        var repo = GetRepository();

        var results = await repo.SearchVocabularyWordsForResourceAsync("app", "Korean", resourceUnderEdit.Id);

        results.Select(w => w.Id).Should().Equal(prefixMatch.Id, containsMatch.Id);
    }

    [Fact]
    public async Task SearchVocabularyWordsForResource_StrictLanguage_ExcludesDifferentNullAndBlankLanguages()
    {
        var resourceUnderEdit = SeedResource(UserA, "Korean");
        var sourceResource = SeedResource(UserA, "Korean");

        var koreanWord = SeedWord("hanja", "한자", "Korean");
        var japaneseWord = SeedWord("hanja", "kanji", "Japanese");
        var nullLanguageWord = SeedWord("hanja", "null language", null);
        var blankLanguageWord = SeedWord("hanja", "blank language", string.Empty);

        Map(sourceResource.Id, koreanWord.Id);
        Map(sourceResource.Id, japaneseWord.Id);
        Map(sourceResource.Id, nullLanguageWord.Id);
        Map(sourceResource.Id, blankLanguageWord.Id);

        var repo = GetRepository();

        var results = await repo.SearchVocabularyWordsForResourceAsync("han", "Korean", resourceUnderEdit.Id);

        results.Select(w => w.Id).Should().Equal(koreanWord.Id);
    }

    [Fact]
    public async Task SearchVocabularyWordsForResource_Exclusion_DoesNotReturnWordsAlreadyMappedToResource()
    {
        var resourceUnderEdit = SeedResource(UserA, "Korean");
        var sourceResource = SeedResource(UserA, "Korean");

        var availableWord = SeedWord("stone", "돌", "Korean");
        var alreadyMappedWord = SeedWord("stone wall", "돌담", "Korean");

        Map(sourceResource.Id, availableWord.Id);
        Map(sourceResource.Id, alreadyMappedWord.Id);
        Map(resourceUnderEdit.Id, alreadyMappedWord.Id);

        var repo = GetRepository();

        var results = await repo.SearchVocabularyWordsForResourceAsync("stone", "Korean", resourceUnderEdit.Id);

        results.Select(w => w.Id).Should().Equal(availableWord.Id);
    }

    [Fact]
    public async Task SearchVocabularyWordsForResource_EmptyUser_ReturnsEmptyResult()
    {
        var resourceUnderEdit = SeedResource(UserA, "Korean");
        var sourceResource = SeedResource(UserA, "Korean");
        var word = SeedWord("apple", "사과", "Korean");
        Map(sourceResource.Id, word.Id);
        _activeUserId = string.Empty;

        var repo = GetRepository();

        var results = await repo.SearchVocabularyWordsForResourceAsync("app", "Korean", resourceUnderEdit.Id);

        results.Should().BeEmpty();
        _logs.HasWarningContaining("SearchVocabularyWordsForResourceAsync", "cross-tenant data leak").Should().BeTrue();
    }

    [Fact]
    public async Task SearchVocabularyWordsForResource_BlankQuery_AppliesLanguageExclusionUserScopeAndLimit()
    {
        var resourceUnderEdit = SeedResource(UserA, "Korean");
        var sourceResource = SeedResource(UserA, "Korean");
        var otherUserResource = SeedResource(UserB, "Korean");

        var alpha = SeedWord("alpha", "a", "Korean");
        var beta = SeedWord("beta", "b", "Korean");
        var gamma = SeedWord("gamma", "g", "Korean");
        var alreadyMapped = SeedWord("aardvark", "already mapped", "Korean");
        var otherLanguage = SeedWord("abel", "different language", "Spanish");
        var otherUser = SeedWord("able", "other user", "Korean");

        Map(sourceResource.Id, gamma.Id);
        Map(sourceResource.Id, beta.Id);
        Map(sourceResource.Id, alpha.Id);
        Map(sourceResource.Id, alreadyMapped.Id);
        Map(sourceResource.Id, otherLanguage.Id);
        Map(otherUserResource.Id, otherUser.Id);
        Map(resourceUnderEdit.Id, alreadyMapped.Id);

        var repo = GetRepository();

        var results = await repo.SearchVocabularyWordsForResourceAsync("   ", "Korean", resourceUnderEdit.Id, limit: 2);

        results.Select(w => w.Id).Should().Equal(alpha.Id, beta.Id);
    }

    private LearningResourceRepository GetRepository()
    {
        return _provider.GetRequiredService<LearningResourceRepository>();
    }

    private static void SeedUser(ApplicationDbContext db, string userId)
    {
        db.UserProfiles.Add(new UserProfile
        {
            Id = userId,
            Name = userId,
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = 20,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private LearningResource SeedResource(string userId, string language)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = $"Resource {Guid.NewGuid()}",
            MediaType = "Vocabulary List",
            Language = language,
            UserProfileId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LearningResources.Add(resource);
        db.SaveChanges();
        return resource;
    }

    private VocabularyWord SeedWord(string targetTerm, string nativeTerm, string? language)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = targetTerm,
            NativeLanguageTerm = nativeTerm,
            Language = language,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.VocabularyWords.Add(word);
        db.SaveChanges();
        return word;
    }

    private void Map(string resourceId, string wordId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = resourceId,
            VocabularyWordId = wordId,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }
}
