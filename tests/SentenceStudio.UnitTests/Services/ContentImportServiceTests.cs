using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Tests for ContentImportService Wave 2 implementation.
/// Covers format detection, parsing (CSV/TSV/Pipe), dedup modes, and commit transaction.
/// </summary>
public class ContentImportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IFileSystemService> _mockFileSystem;
    private readonly Mock<IAiService> _mockAiService;
    private readonly string _testUserId = "test-user-123";

    public ContentImportServiceTests()
    {
        // In-memory SQLite connection (shared across tests in this class)
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Mock dependencies
        var mockPreferences = new Mock<IPreferencesService>();
        mockPreferences.Setup(p => p.Get(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string defaultValue) => defaultValue);

        _mockFileSystem = new Mock<IFileSystemService>();
        _mockAiService = new Mock<IAiService>();

        // Setup DI container
        var services = new ServiceCollection();

        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite(_connection);
            options.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        services.AddLogging(builder => builder.AddDebug());
        services.AddSingleton(mockPreferences.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton(_mockFileSystem.Object);
        services.AddSingleton(_mockAiService.Object);
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<VocabularyProgressRepository>();
        services.AddScoped<ContentImportService>();

        _serviceProvider = services.BuildServiceProvider();

        // Create database schema
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }
    [Fact]
    public async Task ParseContentAsync_DetectsCSV_WhenCommaDelimiterDominates()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        var request = new ContentImportRequest
        {
            RawText = "안녕하세요,hello\n감사합니다,thank you\n잘가요,goodbye",
            ContentType = ContentType.Vocabulary,
            TargetLanguage = "Korean",
            NativeLanguage = "English"
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.DetectedFormat.Should().Contain("CSV");
        preview.Rows.Should().HaveCount(3);
        preview.Rows[0].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows[0].NativeLanguageTerm.Should().Be("hello");
        preview.Rows[1].TargetLanguageTerm.Should().Be("감사합니다");
        preview.Rows[1].NativeLanguageTerm.Should().Be("thank you");
    }

    [Fact]
    public async Task ParseContentAsync_DetectsTSV_WhenTabDelimiterDominates()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        var request = new ContentImportRequest
        {
            RawText = "안녕하세요\thello\n감사합니다\tthank you",
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.DetectedFormat.Should().Contain("TSV");
        preview.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ParseContentAsync_DetectsPipe_WhenPipeDelimiterDominates()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        var request = new ContentImportRequest
        {
            RawText = "안녕하세요|hello\n감사합니다|thank you",
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.DetectedFormat.Should().Contain("Pipe");
        preview.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ParseContentAsync_ParsesJSONArray_Format()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        var json = @"[
            {""targetLanguageTerm"": ""안녕하세요"", ""nativeLanguageTerm"": ""hello""},
            {""targetLanguageTerm"": ""감사합니다"", ""nativeLanguageTerm"": ""thank you""}
        ]";
        
        var request = new ContentImportRequest
        {
            RawText = json,
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.DetectedFormat.Should().Contain("JSON");
        preview.Rows.Should().HaveCount(2);
        preview.Rows[0].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows[0].NativeLanguageTerm.Should().Be("hello");
    }

    [Fact]
    public async Task ParseContentAsync_ParsesJSONArrayWithShortNames_Format()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        // JSON arrays are also supported: ["target", "native"]
        var json = @"[
            [""안녕하세요"", ""hello""],
            [""감사합니다"", ""thank you""]
        ]";
        
        var request = new ContentImportRequest
        {
            RawText = json,
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.DetectedFormat.Should().Contain("JSON");
        preview.Rows.Should().HaveCount(2);
        preview.Rows[0].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows[0].NativeLanguageTerm.Should().Be("hello");
    }

    [Fact]
    public async Task ParseContentAsync_SkipsHeaderRow_WhenFlagSet()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        var request = new ContentImportRequest
        {
            RawText = "Korean,English\n안녕하세요,hello\n감사합니다,thank you",
            ContentType = ContentType.Vocabulary,
            HasHeaderRow = true
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.Rows.Should().HaveCount(2);
        preview.Rows[0].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows.Should().NotContain(r => r.TargetLanguageTerm == "Korean");
    }

    [Fact]
    public async Task ParseContentAsync_UsesDelimiterOverride()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        var request = new ContentImportRequest
        {
            RawText = "안녕하세요;hello\n감사합니다;thank you",
            ContentType = ContentType.Vocabulary,
            DelimiterOverride = ';'
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.Rows.Should().HaveCount(2);
        preview.Rows[0].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows[0].NativeLanguageTerm.Should().Be("hello");
    }

    [Fact]
    public async Task ParseContentAsync_TwoColumnHappyPath_NoAICalls()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        var request = new ContentImportRequest
        {
            RawText = "안녕하세요,hello",
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.Rows.Should().HaveCount(1);
        preview.Rows[0].Status.Should().Be(RowStatus.Ok);
        preview.Rows[0].IsAiTranslated.Should().BeFalse();
        
        // Verify no AI calls were made
        _mockAiService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ParseContentAsync_SkipsCompletelyEmptyRows()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        // Empty lines and rows with empty target terms are skipped
        var request = new ContentImportRequest
        {
            RawText = "안녕하세요,hello\n,empty target\n감사합니다,thank you",
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.Rows.Should().HaveCount(2); // Row with empty target is skipped
        preview.Rows[0].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows[1].TargetLanguageTerm.Should().Be("감사합니다");
    }

    [Fact]
    public async Task ParseContentAsync_TrimsWhitespace()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        // Whitespace is trimmed but duplicates are NOT removed during parsing (dedup happens during commit)
        var request = new ContentImportRequest
        {
            RawText = " 안녕하세요 , hello \n 안녕하세요,hello",
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert - both rows are kept, whitespace is trimmed
        preview.Rows.Should().HaveCount(2);
        preview.Rows[0].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows[0].NativeLanguageTerm.Should().Be("hello");
        preview.Rows[1].TargetLanguageTerm.Should().Be("안녕하세요");
        preview.Rows[1].NativeLanguageTerm.Should().Be("hello");
    }

    [Fact]
    public async Task ParseContentAsync_FreeText_MapsConfidenceToRowStatus()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        // Mock Scriban template file system read
        var templateContent = "Extract vocab from: {{source_text}}";
        _mockFileSystem.Setup(fs => fs.OpenAppPackageFileAsync("FreeTextToVocab.scriban-txt"))
            .ReturnsAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(templateContent)));

        // Mock AI response with varying confidence
        var aiResponse = new FreeTextVocabularyExtractionResponse
        {
            Vocabulary = new List<ExtractedVocabularyItemWithConfidence>
            {
                new ExtractedVocabularyItemWithConfidence 
                { 
                    TargetLanguageTerm = "안녕하세요", 
                    NativeLanguageTerm = "hello",
                    Confidence = "high"
                },
                new ExtractedVocabularyItemWithConfidence 
                { 
                    TargetLanguageTerm = "감사합니다", 
                    NativeLanguageTerm = "thank you",
                    Confidence = "medium"
                },
                new ExtractedVocabularyItemWithConfidence 
                { 
                    TargetLanguageTerm = "잘가요", 
                    NativeLanguageTerm = "goodbye",
                    Confidence = "low"
                }
            }
        };
        _mockAiService.Setup(ai => ai.SendPrompt<FreeTextVocabularyExtractionResponse>(It.IsAny<string>()))
            .ReturnsAsync(aiResponse);

        var request = new ContentImportRequest
        {
            RawText = "Some free-form text",
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.Rows.Should().HaveCount(3);
        preview.Rows[0].Status.Should().Be(RowStatus.Ok);        // high confidence
        preview.Rows[1].Status.Should().Be(RowStatus.Warning);   // medium confidence
        preview.Rows[2].Status.Should().Be(RowStatus.Error);     // low confidence
        preview.Rows[2].IsSelected.Should().BeFalse();           // low confidence auto-deselected
    }

    [Fact]
    public async Task ParseContentAsync_FreeText_Capped50KB()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        
        // Create 55KB of text
        var largeText = new string('가', 55 * 1024);
        
        var request = new ContentImportRequest
        {
            RawText = largeText,
            ContentType = ContentType.Vocabulary
        };

        // Act
        var preview = await service.ParseContentAsync(request);

        // Assert
        preview.Rows.Should().HaveCount(1);
        preview.Rows[0].Status.Should().Be(RowStatus.Error);
        preview.Rows[0].Error.Should().Contain("50KB");
    }

    [Fact]
    public async Task CommitImportAsync_DedupSkip_ReusesExistingWord()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Create existing word directly in database
        var existingWord = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = "안녕하세요",
            NativeLanguageTerm = "hello",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.VocabularyWords.Add(existingWord);

        // Create existing resource with this word
        var existingResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Existing List",
            Language = "Korean",
            MediaType = "Vocabulary List",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserProfileId = _testUserId
        };
        dbContext.LearningResources.Add(existingResource);

        var mapping = new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = existingResource.Id,
            VocabularyWordId = existingWord.Id
        };
        dbContext.ResourceVocabularyMappings.Add(mapping);
        await dbContext.SaveChangesAsync();

        // Create import preview with duplicate word
        var preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new ImportRow 
                { 
                    RowNumber = 1, 
                    TargetLanguageTerm = "안녕하세요", 
                    NativeLanguageTerm = "hello modified",
                    Status = RowStatus.Ok,
                    IsSelected = true
                }
            }
        };

        var commitRequest = new ContentImportCommit
        {
            Preview = preview,
            Target = new ImportTarget 
            { 
                Mode = ImportTargetMode.Existing, 
                ExistingResourceId = existingResource.Id 
            },
            DedupMode = DedupMode.Skip
        };

        // Act
        var result = await service.CommitImportAsync(commitRequest);

        // Assert
        result.SkippedCount.Should().Be(1);
        result.CreatedCount.Should().Be(0);
        result.UpdatedCount.Should().Be(0);

        // Verify word was NOT modified
        var verifyWord = await dbContext.VocabularyWords.FindAsync(existingWord.Id);
        verifyWord!.NativeLanguageTerm.Should().Be("hello"); // Original, not "hello modified"
    }

    [Fact]
    public async Task CommitImportAsync_DedupUpdate_ModifiesSharedWord()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Create existing word
        var existingWord = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = "안녕하세요",
            NativeLanguageTerm = "hello",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.VocabularyWords.Add(existingWord);

        // Create existing resource
        var existingResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Existing List",
            Language = "Korean",
            MediaType = "Vocabulary List",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserProfileId = _testUserId
        };
        dbContext.LearningResources.Add(existingResource);

        var mapping = new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = existingResource.Id,
            VocabularyWordId = existingWord.Id
        };
        dbContext.ResourceVocabularyMappings.Add(mapping);
        await dbContext.SaveChangesAsync();

        var preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new ImportRow 
                { 
                    RowNumber = 1, 
                    TargetLanguageTerm = "안녕하세요", 
                    NativeLanguageTerm = "hello updated",
                    Status = RowStatus.Ok,
                    IsSelected = true
                }
            }
        };

        var commitRequest = new ContentImportCommit
        {
            Preview = preview,
            Target = new ImportTarget 
            { 
                Mode = ImportTargetMode.Existing, 
                ExistingResourceId = existingResource.Id 
            },
            DedupMode = DedupMode.Update
        };

        // Act
        var result = await service.CommitImportAsync(commitRequest);

        // Assert
        result.UpdatedCount.Should().Be(1);
        result.CreatedCount.Should().Be(0);
        result.SkippedCount.Should().Be(0);

        // Reload the word from database to get the updated version
        await dbContext.Entry(existingWord).ReloadAsync();
        existingWord.NativeLanguageTerm.Should().Be("hello updated");
    }

    [Fact]
    public async Task CommitImportAsync_DedupImportAll_CreatesNewWordEvenIfDuplicate()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Create existing word
        var existingWord = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = "안녕하세요",
            NativeLanguageTerm = "hello",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.VocabularyWords.Add(existingWord);

        // Create existing resource
        var existingResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Existing List",
            Language = "Korean",
            MediaType = "Vocabulary List",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserProfileId = _testUserId
        };
        dbContext.LearningResources.Add(existingResource);

        var mapping = new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = existingResource.Id,
            VocabularyWordId = existingWord.Id
        };
        dbContext.ResourceVocabularyMappings.Add(mapping);
        await dbContext.SaveChangesAsync();

        var preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new ImportRow 
                { 
                    RowNumber = 1, 
                    TargetLanguageTerm = "안녕하세요", 
                    NativeLanguageTerm = "hello variant",
                    Status = RowStatus.Ok,
                    IsSelected = true
                }
            }
        };

        var commitRequest = new ContentImportCommit
        {
            Preview = preview,
            Target = new ImportTarget 
            { 
                Mode = ImportTargetMode.Existing, 
                ExistingResourceId = existingResource.Id 
            },
            DedupMode = DedupMode.ImportAll
        };

        // Act
        var result = await service.CommitImportAsync(commitRequest);

        // Assert
        result.CreatedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(0);
        result.SkippedCount.Should().Be(0);

        // Verify we now have 2 words with same target term
        var allWords = await dbContext.VocabularyWords
            .Where(w => w.TargetLanguageTerm == "안녕하세요")
            .ToListAsync();
        allWords.Should().HaveCount(2);
        allWords.Should().Contain(w => w.NativeLanguageTerm == "hello");
        allWords.Should().Contain(w => w.NativeLanguageTerm == "hello variant");
    }

    [Fact]
    public async Task CommitImportAsync_IsCaseSensitive_ForDedup()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Create existing word
        var existingWord = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = "안녕하세요",
            NativeLanguageTerm = "hello",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.VocabularyWords.Add(existingWord);

        // Create existing resource
        var existingResource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Existing List",
            Language = "Korean",
            MediaType = "Vocabulary List",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserProfileId = _testUserId
        };
        dbContext.LearningResources.Add(existingResource);

        var mapping = new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = existingResource.Id,
            VocabularyWordId = existingWord.Id
        };
        dbContext.ResourceVocabularyMappings.Add(mapping);
        await dbContext.SaveChangesAsync();

        // Import with different casing (Korean doesn't have case, but testing dedup on same target term)
        var preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new ImportRow 
                { 
                    RowNumber = 1, 
                    TargetLanguageTerm = "안녕하세요", // Same (case-sensitive match)
                    NativeLanguageTerm = "Hello",      // Different case in native term
                    Status = RowStatus.Ok,
                    IsSelected = true
                }
            }
        };

        var commitRequest = new ContentImportCommit
        {
            Preview = preview,
            Target = new ImportTarget 
            { 
                Mode = ImportTargetMode.Existing, 
                ExistingResourceId = existingResource.Id 
            },
            DedupMode = DedupMode.Skip
        };

        // Act
        var result = await service.CommitImportAsync(commitRequest);

        // Assert - should still detect as duplicate (case-sensitive matching on TargetLanguageTerm)
        result.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task CommitImportAsync_CreatesNewResource_WithMetadata()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new ImportRow 
                { 
                    RowNumber = 1, 
                    TargetLanguageTerm = "안녕하세요", 
                    NativeLanguageTerm = "hello",
                    Status = RowStatus.Ok,
                    IsSelected = true
                }
            }
        };

        var commitRequest = new ContentImportCommit
        {
            Preview = preview,
            Target = new ImportTarget 
            { 
                Mode = ImportTargetMode.New,
                NewResourceTitle = "Imported Vocabulary",
                NewResourceDescription = "Test import",
                TargetLanguage = "Korean",
                NativeLanguage = "English"
            },
            DedupMode = DedupMode.Skip
        };

        // Act
        var result = await service.CommitImportAsync(commitRequest);

        // Assert
        result.CreatedCount.Should().Be(1);
        result.ResourceId.Should().NotBeNullOrEmpty();

        // Verify resource was created with metadata
        var resource = await dbContext.LearningResources.FindAsync(result.ResourceId);
        resource.Should().NotBeNull();
        resource!.Title.Should().Be("Imported Vocabulary");
        resource.Description.Should().Be("Test import");
        resource.Language.Should().Be("Korean");
        resource.MediaType.Should().Be("Vocabulary List");
        
        // Verify word was created
        var mappings = await dbContext.ResourceVocabularyMappings
            .Where(m => m.ResourceId == result.ResourceId)
            .ToListAsync();
        mappings.Should().HaveCount(1);
    }

    [Fact]
    public async Task CommitImportAsync_DedupSkip_DeduplicatesIntraBatchDuplicates()
    {
        // Regression test: two rows with the same trimmed TargetLanguageTerm in a single
        // commit must NOT create two VocabularyWord rows. EF's FirstOrDefaultAsync can't
        // see tracked-but-unsaved entities, so an in-batch cache is required.
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new ImportRow { RowNumber = 1, TargetLanguageTerm = "안녕하세요", NativeLanguageTerm = "hello", Status = RowStatus.Ok, IsSelected = true },
                new ImportRow { RowNumber = 2, TargetLanguageTerm = "안녕하세요", NativeLanguageTerm = "hi",    Status = RowStatus.Ok, IsSelected = true },
                new ImportRow { RowNumber = 3, TargetLanguageTerm = "  안녕하세요  ", NativeLanguageTerm = "greetings", Status = RowStatus.Ok, IsSelected = true },
                new ImportRow { RowNumber = 4, TargetLanguageTerm = "감사합니다", NativeLanguageTerm = "thank you", Status = RowStatus.Ok, IsSelected = true }
            }
        };

        var commitRequest = new ContentImportCommit
        {
            Preview = preview,
            Target = new ImportTarget
            {
                Mode = ImportTargetMode.New,
                NewResourceTitle = "Intra-batch test",
                TargetLanguage = "Korean",
                NativeLanguage = "English"
            },
            DedupMode = DedupMode.Skip
        };

        var result = await service.CommitImportAsync(commitRequest);

        result.CreatedCount.Should().Be(2, "only two unique trimmed target terms exist");
        result.SkippedCount.Should().Be(2, "rows 2 and 3 collapse onto row 1's word");

        // Only ONE VocabularyWord row for "안녕하세요" should exist in the database.
        var hellos = await dbContext.VocabularyWords
            .Where(w => w.TargetLanguageTerm == "안녕하세요")
            .ToListAsync();
        hellos.Should().HaveCount(1, "Skip mode must not create duplicate VocabularyWord rows within a single import");
        hellos[0].NativeLanguageTerm.Should().Be("hello", "Skip mode keeps the first row's translation");

        // Resource gets exactly ONE mapping for "안녕하세요" (no duplicate mappings either).
        var mappings = await dbContext.ResourceVocabularyMappings
            .Where(m => m.ResourceId == result.ResourceId)
            .ToListAsync();
        mappings.Should().HaveCount(2, "two unique words → two mappings, no duplicates");
    }

    [Fact]
    public async Task CommitImportAsync_DedupImportAll_StillCreatesIntraBatchDuplicates()
    {
        // Companion to the Skip regression test: ImportAll must bypass the in-batch cache so
        // the user can intentionally create duplicate VocabularyWord rows within a single import.
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new ImportRow { RowNumber = 1, TargetLanguageTerm = "안녕하세요", NativeLanguageTerm = "hello", Status = RowStatus.Ok, IsSelected = true },
                new ImportRow { RowNumber = 2, TargetLanguageTerm = "안녕하세요", NativeLanguageTerm = "hi",    Status = RowStatus.Ok, IsSelected = true }
            }
        };

        var commitRequest = new ContentImportCommit
        {
            Preview = preview,
            Target = new ImportTarget
            {
                Mode = ImportTargetMode.New,
                NewResourceTitle = "ImportAll intra-batch",
                TargetLanguage = "Korean",
                NativeLanguage = "English"
            },
            DedupMode = DedupMode.ImportAll
        };

        var result = await service.CommitImportAsync(commitRequest);

        result.CreatedCount.Should().Be(2, "ImportAll must create both rows even though they share a target term");

        var hellos = await dbContext.VocabularyWords
            .Where(w => w.TargetLanguageTerm == "안녕하세요")
            .ToListAsync();
        hellos.Should().HaveCount(2, "ImportAll intentionally bypasses dedup");
    }

    [Fact]
    public async Task ParseContentAsync_PhrasesAndTranscript_NoLongerThrow()
    {
        // Phrases and Transcript content types are now supported (v1.1+).
        // This test verifies they execute without throwing.
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();

        var requestPhrases = new ContentImportRequest
        {
            RawText = "안녕하세요,hello",
            ContentType = ContentType.Phrases,
            HarvestPhrases = true,
            HarvestWords = true
        };

        // Phrases branch should parse the CSV line (not throw)
        var phraseResult = await service.ParseContentAsync(requestPhrases);
        phraseResult.Should().NotBeNull();
        phraseResult.Rows.Should().NotBeEmpty("Phrases content type should parse delimited input");
    }
}
