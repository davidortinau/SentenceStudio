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
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Tests for Phrases smart resource implementation in SmartResourceService.
/// Covers population correctness, user scoping, idempotency, planner exclusion,
/// and integration with existing smart resources.
/// </summary>
public class SmartResourcePhrasesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly ApplicationDbContext _db;
    private readonly SmartResourceService _smartResourceService;
    private readonly LearningResourceRepository _resourceRepo;
    private readonly VocabularyProgressRepository _progressRepo;
    
    private const string TestUserA = "user-a-test";
    private const string TestUserB = "user-b-test";
    
    public SmartResourcePhrasesTests()
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
        
        // DeterministicPlanBuilder for planner exclusion tests
        services.AddScoped<DeterministicPlanBuilder>();

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
    /// Scenario 1: User with 2 Phrase, 1 Sentence, 3 Word vocab → refresh Phrases resource → 
    /// mapping contains exactly the 3 non-Word rows
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_WithMixedVocabulary_ReturnsOnlyPhrasesAndSentences()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var phraseWord1 = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        var phraseWord2 = SeedVocabularyWord("phrase-2", "감사합니다", LexicalUnitType.Phrase);
        var sentenceWord = SeedVocabularyWord("sentence-1", "오늘 날씨가 좋아요", LexicalUnitType.Sentence);
        var regularWord1 = SeedVocabularyWord("word-1", "사과", LexicalUnitType.Word);
        var regularWord2 = SeedVocabularyWord("word-2", "물", LexicalUnitType.Word);
        var regularWord3 = SeedVocabularyWord("word-3", "책", LexicalUnitType.Word);
        
        // Create progress for all words for TestUserA
        SeedVocabularyProgress(phraseWord1.Id, TestUserA);
        SeedVocabularyProgress(phraseWord2.Id, TestUserA);
        SeedVocabularyProgress(sentenceWord.Id, TestUserA);
        SeedVocabularyProgress(regularWord1.Id, TestUserA);
        SeedVocabularyProgress(regularWord2.Id, TestUserA);
        SeedVocabularyProgress(regularWord3.Id, TestUserA);

        // Create Phrases smart resource
        var phrasesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            Description = "Practice all your phrase and sentence vocabulary",
            MediaType = "Smart Vocabulary List",
            Language = "Korean",
            Tags = "system-generated,dynamic,phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id);
        mappedWords.Should().HaveCount(3, "only Phrase and Sentence types should be included");
        
        var mappedWordIds = mappedWords.Select(w => w.Id).ToList();
        mappedWordIds.Should().Contain(phraseWord1.Id);
        mappedWordIds.Should().Contain(phraseWord2.Id);
        mappedWordIds.Should().Contain(sentenceWord.Id);
        mappedWordIds.Should().NotContain(regularWord1.Id);
        mappedWordIds.Should().NotContain(regularWord2.Id);
        mappedWordIds.Should().NotContain(regularWord3.Id);
    }

    /// <summary>
    /// Scenario 2: User with zero Phrase/Sentence vocab → refresh → empty mapping (no exception, resource still exists)
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_WithNoPhrasesOrSentences_ReturnsEmptyMapping()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        // Create only regular Word vocabulary
        var word1 = SeedVocabularyWord("word-1", "사과", LexicalUnitType.Word);
        var word2 = SeedVocabularyWord("word-2", "물", LexicalUnitType.Word);
        
        SeedVocabularyProgress(word1.Id, TestUserA);
        SeedVocabularyProgress(word2.Id, TestUserA);

        var phrasesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            Description = "Practice all your phrase and sentence vocabulary",
            MediaType = "Smart Vocabulary List",
            Language = "Korean",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id);
        mappedWords.Should().BeEmpty("no phrase/sentence vocabulary exists for this user");
        
        // Verify resource still exists
        var resource = await _resourceRepo.GetResourceAsync(phrasesResource.Id);
        resource.Should().NotBeNull("resource should still exist even with empty mapping");
    }

    /// <summary>
    /// Scenario 3: User with only Sentence vocab (no Phrase) → refresh → mapping contains only the Sentence rows
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_WithOnlySentences_ReturnsOnlySentences()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var sentence1 = SeedVocabularyWord("sentence-1", "오늘 날씨가 좋아요", LexicalUnitType.Sentence);
        var sentence2 = SeedVocabularyWord("sentence-2", "저는 학생입니다", LexicalUnitType.Sentence);
        var word1 = SeedVocabularyWord("word-1", "사과", LexicalUnitType.Word);
        
        SeedVocabularyProgress(sentence1.Id, TestUserA);
        SeedVocabularyProgress(sentence2.Id, TestUserA);
        SeedVocabularyProgress(word1.Id, TestUserA);

        var phrasesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id);
        mappedWords.Should().HaveCount(2, "only Sentence types should be included");
        
        var mappedWordIds = mappedWords.Select(w => w.Id).ToList();
        mappedWordIds.Should().Contain(sentence1.Id);
        mappedWordIds.Should().Contain(sentence2.Id);
        mappedWordIds.Should().NotContain(word1.Id);
    }

    /// <summary>
    /// Scenario 4: User with Phrase vocab where LexicalUnitType == Unknown → refresh → 
    /// those rows NOT included (only explicitly Phrase/Sentence)
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_WithUnknownLexicalType_ExcludesUnknown()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var phrase = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        var unknown = SeedVocabularyWord("unknown-1", "뭔가", LexicalUnitType.Unknown);
        var sentence = SeedVocabularyWord("sentence-1", "좋은 아침입니다", LexicalUnitType.Sentence);
        
        SeedVocabularyProgress(phrase.Id, TestUserA);
        SeedVocabularyProgress(unknown.Id, TestUserA);
        SeedVocabularyProgress(sentence.Id, TestUserA);

        var phrasesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResource);

        // Act
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id);
        mappedWords.Should().HaveCount(2, "only Phrase and Sentence (not Unknown) should be included");
        
        var mappedWordIds = mappedWords.Select(w => w.Id).ToList();
        mappedWordIds.Should().Contain(phrase.Id);
        mappedWordIds.Should().Contain(sentence.Id);
        mappedWordIds.Should().NotContain(unknown.Id);
    }

    /// <summary>
    /// Scenario 5: User A has VocabularyProgress rows for Phrase words X and Y. 
    /// User B has progress for Phrase word Z. Refresh User A's Phrases resource → 
    /// mapping contains only X, Y (not Z).
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_WithMultipleUsers_OnlyIncludesUserAWords()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        SeedUserProfile(TestUserB);
        
        var phraseX = SeedVocabularyWord("phrase-x", "안녕하세요", LexicalUnitType.Phrase);
        var phraseY = SeedVocabularyWord("phrase-y", "감사합니다", LexicalUnitType.Phrase);
        var phraseZ = SeedVocabularyWord("phrase-z", "죄송합니다", LexicalUnitType.Phrase);
        
        // User A has progress for X and Y
        SeedVocabularyProgress(phraseX.Id, TestUserA);
        SeedVocabularyProgress(phraseY.Id, TestUserA);
        
        // User B has progress for Z
        SeedVocabularyProgress(phraseZ.Id, TestUserB);

        var phrasesResourceA = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResourceA);

        // Act - refresh for User A
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResourceA.Id, TestUserA);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResourceA.Id);
        mappedWords.Should().HaveCount(2, "only User A's phrases should be included");
        
        var mappedWordIds = mappedWords.Select(w => w.Id).ToList();
        mappedWordIds.Should().Contain(phraseX.Id);
        mappedWordIds.Should().Contain(phraseY.Id);
        mappedWordIds.Should().NotContain(phraseZ.Id, "User B's phrase should not be included");
    }

    /// <summary>
    /// Scenario 6: Word X is a Phrase and has progress for User A only. 
    /// User B runs refresh on their own Phrases resource → mapping is empty 
    /// (even though X exists globally as a Phrase).
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_UserBWithNoProgress_ReturnsEmpty()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        SeedUserProfile(TestUserB);
        
        var phraseX = SeedVocabularyWord("phrase-x", "안녕하세요", LexicalUnitType.Phrase);
        
        // Only User A has progress for X
        SeedVocabularyProgress(phraseX.Id, TestUserA);

        var phrasesResourceB = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserB,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResourceB);

        // Act - refresh for User B
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResourceB.Id, TestUserB);

        // Assert
        var mappedWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResourceB.Id);
        mappedWords.Should().BeEmpty("User B has no progress for any phrases, even though X exists globally");
    }

    /// <summary>
    /// Scenario 7: Run refresh twice in sequence. Final mapping count equals single-run count. 
    /// No duplicate ResourceVocabularyMapping rows.
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_CalledTwice_IsIdempotent()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var phrase1 = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        var phrase2 = SeedVocabularyWord("phrase-2", "감사합니다", LexicalUnitType.Phrase);
        
        SeedVocabularyProgress(phrase1.Id, TestUserA);
        SeedVocabularyProgress(phrase2.Id, TestUserA);

        var phrasesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResource);

        // Act - refresh twice
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);
        var firstRunCount = (await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id)).Count;
        
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);
        var secondRunCount = (await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id)).Count;

        // Assert
        secondRunCount.Should().Be(firstRunCount, "second refresh should produce same count as first");
        secondRunCount.Should().Be(2, "mapping should contain exactly 2 phrases");
        
        // Verify no duplicate mappings at database level
        var allMappings = _db.ResourceVocabularyMappings
            .Where(m => m.ResourceId == phrasesResource.Id)
            .ToList();
        allMappings.Should().HaveCount(2, "no duplicate mappings should exist");
    }

    /// <summary>
    /// Scenario 8: Add a new Phrase word to the user's vocab, run refresh → mapping grows by 1. 
    /// Remove one, run refresh → mapping shrinks.
    /// </summary>
    [Fact]
    public async Task RefreshPhrases_AfterAddingAndRemovingWords_UpdatesMapping()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        var phrase1 = SeedVocabularyWord("phrase-1", "안녕하세요", LexicalUnitType.Phrase);
        var phrase2 = SeedVocabularyWord("phrase-2", "감사합니다", LexicalUnitType.Phrase);
        
        SeedVocabularyProgress(phrase1.Id, TestUserA);
        SeedVocabularyProgress(phrase2.Id, TestUserA);

        var phrasesResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrasesResource);

        // Initial refresh
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);
        var initialCount = (await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id)).Count;
        initialCount.Should().Be(2);

        // Add a new phrase
        var phrase3 = SeedVocabularyWord("phrase-3", "실례합니다", LexicalUnitType.Phrase);
        SeedVocabularyProgress(phrase3.Id, TestUserA);
        
        // Act - refresh after adding
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);
        var afterAddCount = (await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id)).Count;
        
        // Assert - grew by 1
        afterAddCount.Should().Be(3, "mapping should grow by 1 after adding new phrase");
        
        // Remove one progress record
        var progressToRemove = await _db.VocabularyProgresses
            .AsNoTracking()
            .FirstOrDefaultAsync(vp => vp.VocabularyWordId == phrase1.Id && vp.UserId == TestUserA);
        
        // Remove using the Id to avoid tracking conflicts
        var trackedEntry = _db.VocabularyProgresses.Find(progressToRemove!.Id);
        if (trackedEntry != null)
        {
            _db.VocabularyProgresses.Remove(trackedEntry);
        }
        else
        {
            _db.VocabularyProgresses.Attach(progressToRemove!);
            _db.VocabularyProgresses.Remove(progressToRemove!);
        }
        await _db.SaveChangesAsync();
        
        // Act - refresh after removing
        await _smartResourceService.RefreshSmartResourceAsync(phrasesResource.Id, TestUserA);
        var afterRemoveCount = (await _resourceRepo.GetVocabularyWordsByResourceAsync(phrasesResource.Id)).Count;
        
        // Assert - shrunk by 1
        afterRemoveCount.Should().Be(2, "mapping should shrink by 1 after removing progress");
    }

    /// <summary>
    /// Scenario 9: Invoke DeterministicPlanBuilder when the user has only smart resources 
    /// (including Phrases) and no regular LearningResource. Assert no smart resource is returned.
    /// </summary>
    [Fact]
    public async Task PlanBuilder_WithOnlySmartResources_ReturnsNoResource()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        // Create only smart resources, no regular resources
        var dailyReview = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Daily Review",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_DailyReview,
            MediaType = "Smart Vocabulary List",
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(dailyReview);
        
        var phrases = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            MediaType = "Smart Vocabulary List",
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrases);

        var builder = _serviceProvider.GetRequiredService<DeterministicPlanBuilder>();

        // Act
        var plan = await builder.BuildPlanAsync();

        // Assert - plan should be null or have no primary resource
        // (fallback to vocab-only plan is acceptable)
        plan?.PrimaryResource.Should().BeNull("planner should not select smart resources");
    }

    /// <summary>
    /// Scenario 10: Invoke the planner with one regular LearningResource AND the Phrases smart resource. 
    /// Assert the regular resource is selected, Phrases is ignored.
    /// </summary>
    [Fact]
    public async Task PlanBuilder_WithRegularAndSmartResources_SelectsRegularResource()
    {
        // Arrange
        SeedUserProfile(TestUserA);
        
        // Create a regular resource
        var regularResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Korean Podcast Episode 1",
            MediaType = "Podcast",
            Transcript = "Some podcast transcript content here",
            IsSmartResource = false,
            UserProfileId = TestUserA,
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(regularResource);
        
        // Create Phrases smart resource
        var phrases = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Phrases",
            IsSmartResource = true,
            SmartResourceType = SmartResourceService.SmartResourceType_Phrases,
            MediaType = "Smart Vocabulary List",
            UserProfileId = TestUserA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _resourceRepo.SaveResourceAsync(phrases);

        var builder = _serviceProvider.GetRequiredService<DeterministicPlanBuilder>();

        // Act
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(regularResource.Id, 
            "planner should select regular resource, not smart resource");
    }

    /// <summary>
    /// Scenario 11: Initialize smart resources from scratch for a new user. 
    /// Assert exactly 4 smart resources exist: DailyReview, NewWords, Struggling, Phrases.
    /// </summary>
    [Fact]
    public async Task InitializeSmartResources_CreatesAllFourTypes()
    {
        // Arrange
        SeedUserProfile(TestUserA);

        // Act
        await _smartResourceService.InitializeSmartResourcesAsync("Korean", TestUserA);

        // Assert
        var smartResources = await _resourceRepo.GetSmartResourcesAsync();
        smartResources.Should().HaveCount(4, "exactly 4 smart resources should exist");
        
        var types = smartResources.Select(r => r.SmartResourceType).ToList();
        types.Should().Contain(SmartResourceService.SmartResourceType_DailyReview);
        types.Should().Contain(SmartResourceService.SmartResourceType_NewWords);
        types.Should().Contain(SmartResourceService.SmartResourceType_Struggling);
        types.Should().Contain(SmartResourceService.SmartResourceType_Phrases);
    }

    /// <summary>
    /// Scenario 12: Re-initialize (idempotent) — still 4 resources, no duplicates.
    /// </summary>
    [Fact]
    public async Task InitializeSmartResources_CalledTwice_IsIdempotent()
    {
        // Arrange
        SeedUserProfile(TestUserA);

        // Act - initialize twice
        await _smartResourceService.InitializeSmartResourcesAsync("Korean", TestUserA);
        var firstRunCount = (await _resourceRepo.GetSmartResourcesAsync()).Count;
        
        await _smartResourceService.InitializeSmartResourcesAsync("Korean", TestUserA);
        var secondRunCount = (await _resourceRepo.GetSmartResourcesAsync()).Count;

        // Assert
        secondRunCount.Should().Be(firstRunCount, "second initialization should not create duplicates");
        secondRunCount.Should().Be(4, "exactly 4 smart resources should exist");
        
        // Verify no duplicate types
        var smartResources = await _resourceRepo.GetSmartResourcesAsync();
        var groupedByType = smartResources.GroupBy(r => r.SmartResourceType);
        foreach (var group in groupedByType)
        {
            group.Should().ContainSingle($"only one {group.Key} resource should exist");
        }
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
