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
/// Tests for ContentImportService.ClassifyContentAsync — covering all 3 branches:
/// null AI response, AI exception, and the 4-arm type-switch (vocabulary/phrases/sentences/transcript + unknown).
/// </summary>
public class ClassifyContentAsyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IAiService> _mockAiService;

    public ClassifyContentAsyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var mockPreferences = new Mock<IPreferencesService>();
        mockPreferences.Setup(p => p.Get(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string defaultValue) => defaultValue);

        var mockFileSystem = new Mock<IFileSystemService>();
        _mockAiService = new Mock<IAiService>();

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
        services.AddSingleton(mockFileSystem.Object);
        services.AddSingleton(_mockAiService.Object);
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<VocabularyProgressRepository>();
        services.AddScoped<ContentImportService>();

        _serviceProvider = services.BuildServiceProvider();

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

    // -----------------------------------------------------------------
    // Branch 1: null AI response → Vocabulary / 0.5f
    // -----------------------------------------------------------------

    [Fact]
    public async Task ClassifyContent_NullAiResponse_DefaultsToVocabulary()
    {
        // Arrange
        _mockAiService
            .Setup(s => s.SendPrompt<ContentClassificationAiResponse>(It.IsAny<string>()))
            .ReturnsAsync((ContentClassificationAiResponse?)null);

        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();

        // Act
        var result = await service.ClassifyContentAsync("apple,사과\nbanana,바나나", null);

        // Assert
        result.ContentType.Should().Be(ContentType.Vocabulary);
        result.Confidence.Should().BeApproximately(0.5f, 0.001f);
    }

    // -----------------------------------------------------------------
    // Branch 2: AI throws → Vocabulary / 0.3f with error signals
    // -----------------------------------------------------------------

    [Fact]
    public async Task ClassifyContent_AiException_DefaultsWithLowConfidence()
    {
        // Arrange
        _mockAiService
            .Setup(s => s.SendPrompt<ContentClassificationAiResponse>(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("AI unavailable"));

        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();

        // Act
        var result = await service.ClassifyContentAsync("apple,사과\nbanana,바나나", null);

        // Assert
        result.ContentType.Should().Be(ContentType.Vocabulary);
        result.Confidence.Should().BeApproximately(0.3f, 0.001f);
        result.Signals.Should().Contain("error");
        result.Signals.Should().Contain(nameof(InvalidOperationException));
    }

    // -----------------------------------------------------------------
    // Branch 3: type-switch arms
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("vocabulary", ContentType.Vocabulary)]
    [InlineData("phrases", ContentType.Phrases)]
    [InlineData("sentences", ContentType.Sentences)]
    [InlineData("transcript", ContentType.Transcript)]
    public async Task ClassifyContent_KnownType_MapsCorrectly(string aiType, ContentType expectedType)
    {
        // Arrange
        _mockAiService
            .Setup(s => s.SendPrompt<ContentClassificationAiResponse>(It.IsAny<string>()))
            .ReturnsAsync(new ContentClassificationAiResponse
            {
                Type = aiType,
                Confidence = 0.9f,
                Reasoning = $"Looks like {aiType}",
                Signals = new List<string> { aiType }
            });

        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();

        // Act
        var result = await service.ClassifyContentAsync("some content sample", null);

        // Assert
        result.ContentType.Should().Be(expectedType);
        result.Confidence.Should().BeApproximately(0.9f, 0.001f);
    }

    [Fact]
    public async Task ClassifyContent_UnknownType_FallsBackToVocabulary()
    {
        // Arrange — AI returns a type not in the switch arms
        _mockAiService
            .Setup(s => s.SendPrompt<ContentClassificationAiResponse>(It.IsAny<string>()))
            .ReturnsAsync(new ContentClassificationAiResponse
            {
                Type = "unknown_future_type",
                Confidence = 0.6f,
                Reasoning = "unrecognised",
                Signals = new List<string>()
            });

        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContentImportService>();

        // Act
        var result = await service.ClassifyContentAsync("some content sample", null);

        // Assert
        result.ContentType.Should().Be(ContentType.Vocabulary);
    }
}
