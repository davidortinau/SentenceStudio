using Xunit;
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

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Tests for Sentences smart resource implementation in SmartResourceService.
/// Covers population correctness, user scoping, idempotency, and integration
/// with existing smart resources.
/// </summary>
public class SmartResourceSentencesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly ApplicationDbContext _db;
    private readonly SmartResourceService _smartResourceService;
    private readonly LearningResourceRepository _resourceRepo;
    private readonly VocabularyProgressRepository _progressRepo;
    
    private const string TestUserA = "user-a-test";
    private const string TestUserB = "user-b-test";
    
    public SmartResourceSentencesTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        // ApplicationDbContext with shared in-memory SQLite connection
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        // Mock IPreferencesService
        var mockPreferences = new Mock<IPreferencesService>();
        mockPreferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(TestUserA);
        services.AddSingleton(mockPreferences.Object);

        // Mock ISyncService
        services.AddSingleton<ISyncService>(new NoOpSyncService());

        // Mock IFileSystemService
        var mockFileSystem = new Mock<IFileSystemService>();
        mockFileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());
        services.AddSingleton(mockFileSystem.Object);

        // Repositories
        services.AddScoped<UserProfileRepository>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();
        services.AddScoped<VocabularyProgressRepository>();

        // SmartResourceService
        services.AddScoped<SmartResourceService>();

        // Logging
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        _serviceProvider = services.BuildServiceProvider();

        // Create schema
        var scope = _serviceProvider.CreateScope();
        _db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();
        
        _smartResourceService = scope.ServiceProvider.GetRequiredService<SmartResourceService>();
        _resourceRepo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();
        _progressRepo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();
    }

    public void Dispose()
    {
        _db?.Dispose();
        _serviceProvider?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }

    /// <summary>
    /// Scenario 1: User with 2 Phrase, 3 Sentence, 2 Word vocab → refresh Sentences resource → 
    /// mapping contains exactly the 3 Sentence rows
    /// </summary>
    [Fact]
    public async Task RefreshSentences_WithMixedVocabulary_ReturnsOnlySentences()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var phraseWord1 = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        var phraseWord2 = SeedVocabularyWord("phrase-2", "감사합니다", LexicalUnitType.Phrase);
        var sentenceWord1 = SeedVocabularyWord("sentence-1", "오늘 날씨가 좋아요", LexicalUnitType.Sentence);
        var sentenceWord2 = SeedVocabularyWord("sentence-2", "저는 학생입니다", LexicalUnitType.Sentence);
        var sentenceWord3 = SeedVocabularyWord("sentence-3", "좋은 아침입니다", LexicalUnitType.Sentence);
        var regularWord1 = SeedVocabularyWord("word-1", "사과", LexicalUnitType.Word);
        var regularWord2 = SeedVocabularyWord("word-2", "물", LexicalUnitType.Word);
        
        // Create progress for all words for TestUserA
        SeedVocabularyProgress(phraseWord1.Id, TestUserA);
        SeedVocabularyProgress(phraseWord2.Id, TestUserA);
        SeedVocabularyProgress(sentenceWord1.Id, TestUserA);
        SeedVocabularyProgress(sentenceWord2.Id, TestUserA);
        SeedVocabularyProgress(sentenceWord3.Id, TestUserA);
        SeedVocabularyProgress(regularWord1.Id, TestUserA);
        SeedVocabularyProgress(regularWord2.Id, TestUserA);

        // Create Sentences smart resource
        var sentencesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Sentences",
            Description = "Practice all your sentence vocabulary",
            MediaType = "Smart Vocabulary List",
            Language = "Korean",
            Tags = "system-generated,dynamic,sentences",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Sentences,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(sentencesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(sentencesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(sentencesResource.Id);
        mappedWords.Should().HaveCount(3, "only Sentence types should be included");
        
        var mappedWordIds = mappedWords.Select(w => w.Id).ToList();
        mappedWordIds.Should().Contain(sentenceWord1.Id);
        mappedWordIds.Should().Contain(sentenceWord2.Id);
        mappedWordIds.Should().Contain(sentenceWord3.Id);
        mappedWordIds.Should().NotContain(phraseWord1.Id);
        mappedWordIds.Should().NotContain(phraseWord2.Id);
        mappedWordIds.Should().NotContain(regularWord1.Id);
        mappedWordIds.Should().NotContain(regularWord2.Id);
    }

    /// <summary>
    /// Scenario 2: User with zero Sentence vocab → refresh → empty mapping (no exception, resource still exists)
    /// </summary>
    [Fact]
    public async Task RefreshSentences_WithNoSentences_ReturnsEmptyMapping()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        // Create only regular Word vocabulary and Phrases (no Sentences)
        var word1 = SeedVocabularyWord("word-1", "사과", LexicalUnitType.Word);
        var word2 = SeedVocabularyWord("word-2", "물", LexicalUnitType.Word);
        var phrase1 = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        
        SeedVocabularyProgress(word1.Id, TestUserA);
        SeedVocabularyProgress(word2.Id, TestUserA);
        SeedVocabularyProgress(phrase1.Id, TestUserA);

        var sentencesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Sentences",
            Description = "Practice all your sentence vocabulary",
            MediaType = "Smart Vocabulary List",
            Language = "Korean",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Sentences,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(sentencesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(sentencesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(sentencesResource.Id);
        mappedWords.Should().BeEmpty("no sentence vocabulary exists for this user");
        
        // Verify resource still exists
        var resource = await _resourceRepo.GetResourceAsync(sentencesResource.Id);
        resource.Should().NotBeNull("resource should still exist even with empty mapping");
    }

    /// <summary>
    /// Scenario 3: User with only Phrase vocab (no Sentence) → refresh → mapping is empty (Phrases go to Phrases resource)
    /// </summary>
    [Fact]
    public async Task RefreshSentences_WithOnlyPhrases_ReturnsEmpty()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var phrase1 = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        var phrase2 = SeedVocabularyWord("phrase-2", "감사합니다", LexicalUnitType.Phrase);
        var word1 = SeedVocabularyWord("word-1", "사과", LexicalUnitType.Word);
        
        SeedVocabularyProgress(phrase1.Id, TestUserA);
        SeedVocabularyProgress(phrase2.Id, TestUserA);
        SeedVocabularyProgress(word1.Id, TestUserA);

        var sentencesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Sentences",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Sentences,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(sentencesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(sentencesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(sentencesResource.Id);
        mappedWords.Should().BeEmpty("only Sentence types should be included, not Phrases");
    }

    /// <summary>
    /// Scenario 4: User with Sentence vocab where LexicalUnitType == Unknown → refresh → 
    /// those rows NOT included (only explicitly Sentence)
    /// </summary>
    [Fact]
    public async Task RefreshSentences_WithUnknownLexicalType_ExcludesUnknown()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var sentence = SeedVocabularyWord("sentence-1", "좋은 아침입니다", LexicalUnitType.Sentence);
        var unknown = SeedVocabularyWord("unknown-1", "뭔가", LexicalUnitType.Unknown);
        var phrase = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        
        SeedVocabularyProgress(sentence.Id, TestUserA);
        SeedVocabularyProgress(unknown.Id, TestUserA);
        SeedVocabularyProgress(phrase.Id, TestUserA);

        var sentencesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Sentences",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Sentences,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(sentencesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(sentencesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(sentencesResource.Id);
        mappedWords.Should().HaveCount(1, "only explicitly Sentence type should be included");
        mappedWords.First().Id.Should().Be(sentence.Id);
    }

    /// <summary>
    /// Scenario 5: Two users with separate sentence vocab → refresh scoped to UserA → 
    /// UserB's sentences NOT included
    /// </summary>
    [Fact]
    public async Task RefreshSentences_WithMultipleUsers_ScopedCorrectly()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        SeedUserProfile(TestUserB);
        
        var sentenceUserA1 = SeedVocabularyWord("sentence-a-1", "오늘 날씨가 좋아요", LexicalUnitType.Sentence);
        var sentenceUserA2 = SeedVocabularyWord("sentence-a-2", "저는 학생입니다", LexicalUnitType.Sentence);
        var sentenceUserB1 = SeedVocabularyWord("sentence-b-1", "좋은 아침입니다", LexicalUnitType.Sentence);
        var sentenceUserB2 = SeedVocabularyWord("sentence-b-2", "안녕히 가세요", LexicalUnitType.Sentence);
        
        SeedVocabularyProgress(sentenceUserA1.Id, TestUserA);
        SeedVocabularyProgress(sentenceUserA2.Id, TestUserA);
        SeedVocabularyProgress(sentenceUserB1.Id, TestUserB);
        SeedVocabularyProgress(sentenceUserB2.Id, TestUserB);

        var sentencesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Sentences",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Sentences,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(sentencesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(sentencesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(sentencesResource.Id);
        mappedWords.Should().HaveCount(2, "only UserA's sentences should be included");
        
        var mappedWordIds = mappedWords.Select(w => w.Id).ToList();
        mappedWordIds.Should().Contain(sentenceUserA1.Id);
        mappedWordIds.Should().Contain(sentenceUserA2.Id);
        mappedWordIds.Should().NotContain(sentenceUserB1.Id);
        mappedWordIds.Should().NotContain(sentenceUserB2.Id);
    }

    /// <summary>
    /// Scenario 6: Refresh twice (idempotent) → same mapping both times, no duplicates
    /// </summary>
    [Fact]
    public async Task RefreshSentences_CalledTwice_IsIdempotent()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var sentence1 = SeedVocabularyWord("sentence-1", "오늘 날씨가 좋아요", LexicalUnitType.Sentence);
        var sentence2 = SeedVocabularyWord("sentence-2", "저는 학생입니다", LexicalUnitType.Sentence);
        
        SeedVocabularyProgress(sentence1.Id, TestUserA);
        SeedVocabularyProgress(sentence2.Id, TestUserA);

        var sentencesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Sentences",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Sentences,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(sentencesResource);

        // Act - refresh twice
        await _smartResourceService.RefreshSmartResourceAsync(sentencesResource.Id, TestUserA);
        var firstMappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(sentencesResource.Id);
        
        await _smartResourceService.RefreshSmartResourceAsync(sentencesResource.Id, TestUserA);
        var secondMappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(sentencesResource.Id);

        // Assert
        secondMappedWords.Should().HaveCount(firstMappedWords.Count, "second refresh should produce same mapping");
        secondMappedWords.Should().HaveCount(2, "exactly 2 sentences should be mapped");
        
        var secondMappedWordIds = secondMappedWords.Select(w => w.Id).ToList();
        secondMappedWordIds.Should().Contain(sentence1.Id);
        secondMappedWordIds.Should().Contain(sentence2.Id);
    }

    // Helper methods

    private void SeedUserProfile(string userId)
    {
        var existing = _db.UserProfiles.Find(userId);
        if (existing != null) return;
        
        _db.UserProfiles.Add(new UserProfile
        {
            Id = userId,
            Name = $"Test User {userId}",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = 20,
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private VocabularyWord SeedVocabularyWord(
        string id, 
        string targetTerm, 
        LexicalUnitType lexicalType)
    {
        var word = new VocabularyWord
        {
            Id = id,
            TargetLanguageTerm = targetTerm,
            NativeLanguageTerm = $"en_{targetTerm}",
            Language = "Korean",
            LexicalUnitType = lexicalType,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.VocabularyWords.Add(word);
        _db.SaveChanges();
        return word;
    }

    private VocabularyProgress SeedVocabularyProgress(
        string vocabularyWordId, 
        string userId,
        float masteryScore = 0.3f,
        int totalAttempts = 5,
        int correctAttempts = 2)
    {
        var progress = new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = vocabularyWordId,
            UserId = userId,
            MasteryScore = masteryScore,
            TotalAttempts = totalAttempts,
            CorrectAttempts = correctAttempts,
            NextReviewDate = DateTime.UtcNow.AddDays(-1),
            ReviewInterval = 1,
            EaseFactor = 2.5f,
            FirstSeenAt = DateTime.UtcNow.AddDays(-7),
            LastPracticedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.VocabularyProgresses.Add(progress);
        _db.SaveChanges();
        return progress;
    }
}
