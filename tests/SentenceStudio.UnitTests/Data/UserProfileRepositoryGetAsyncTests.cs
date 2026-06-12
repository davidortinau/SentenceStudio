// REGRESSION TESTS — UserProfileRepository.GetAsync() cross-tenant identity leak
//
// Root cause: when active_profile_id was set but no matching UserProfile row existed,
// GetAsync() silently fell back to db.UserProfiles.FirstOrDefaultAsync() — returning
// a different user's profile. In production this surfaced as dave@ortinau.com seeing
// squad-jayne's profile after dave's UserProfile row was deleted.
//
// See: AGENTS.md "Multi-tenant data scoping rule (NON-NEGOTIABLE)"
// DO NOT DELETE OR WEAKEN THESE TESTS.

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

public sealed class UserProfileRepositoryGetAsyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IPreferencesService> _mockPreferences;
    private readonly CollectingLoggerProvider _logs;
    private readonly UserProfileRepository _sut;

    public UserProfileRepositoryGetAsyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _mockPreferences = new Mock<IPreferencesService>();
        _mockPreferences
            .Setup(p => p.Get("active_profile_id", It.IsAny<string>()))
            .Returns(string.Empty);

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IPreferencesService>(_mockPreferences.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        _logs = new CollectingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.ClearProviders();
            b.AddProvider(_logs);
            b.SetMinimumLevel(LogLevel.Warning);
        });
        _sut = new UserProfileRepository(_serviceProvider, loggerFactory.CreateLogger<UserProfileRepository>());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private void SeedProfile(string id, string name, string targetLang = "Korean", string nativeLang = "English")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile
        {
            Id = id,
            Name = name,
            TargetLanguage = targetLang,
            NativeLanguage = nativeLang,
            DisplayLanguage = "English",
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private void SetActiveProfileId(string id)
    {
        _mockPreferences
            .Setup(p => p.Get("active_profile_id", It.IsAny<string>()))
            .Returns(id);
    }

    [Fact]
    public async Task GetAsync_WhenActiveIdEmpty_ReturnsNull()
    {
        // Arrange — two profiles seeded; active_profile_id is explicitly empty
        SeedProfile("squad-jayne", "Jayne Cobb", "Chinese", "English");
        SeedProfile("captain-dave", "Dave Ortinau", "Korean", "English");
        SetActiveProfileId(string.Empty);

        // Act
        var result = await _sut.GetAsync();

        // Assert — must return null, never fall back to the first row
        result.Should().BeNull("empty active_profile_id must never silently return another user's profile");
        _logs.HasWarningContaining("active_profile_id").Should().BeTrue(
            "the absence of an active profile id must be logged as a warning");
    }

    [Fact]
    public async Task GetAsync_WhenActiveIdDoesNotMatchAnyRow_ReturnsNull()
    {
        // Arrange — squad-jayne is the FIRST row (she would have been returned by the old buggy fallback).
        // Dave's active_profile_id is set but his row was deleted / never synced to this device.
        SeedProfile("squad-jayne-id", "Jayne Cobb", "Chinese", "English");
        SeedProfile("captain-id-B", "Dave Ortinau", "Korean", "English");
        SetActiveProfileId("ghost-id-not-in-db");

        // Act
        var result = await _sut.GetAsync();

        // Assert — returning squad-jayne (first row) would be the cross-tenant leak
        result.Should().BeNull(
            "active_profile_id 'ghost-id-not-in-db' matches no row; returning squad-jayne's profile would be a cross-tenant data leak");
        _logs.HasWarningContaining("ghost-id-not-in-db").Should().BeTrue(
            "the unmatched profile id must appear in the warning so incident responders can trace the exact id");
    }

    [Fact]
    public async Task GetAsync_WhenActiveIdMatches_ReturnsThatProfile()
    {
        // Arrange — squad-jayne is inserted first so she occupies the first DB row.
        // Captain's id is set as active. The fix must return captain, not squad-jayne.
        SeedProfile("squad-jayne-id", "Jayne Cobb", "Chinese", "English");
        SeedProfile("captain-id-C", "Dave Ortinau", "Korean", "English");
        SetActiveProfileId("captain-id-C");

        // Act
        var result = await _sut.GetAsync();

        // Assert — correct profile returned despite not being the first row
        result.Should().NotBeNull();
        result!.Id.Should().Be("captain-id-C",
            "GetAsync must return the profile matching active_profile_id even when a different profile is the first DB row");
        result.Name.Should().Be("Dave Ortinau");
    }
}
