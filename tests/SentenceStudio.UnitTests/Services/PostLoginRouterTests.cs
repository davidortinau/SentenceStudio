// CRITICAL REGRESSION TESTS — post-login routing
// These tests exist because LoginPage.razor:127 used to short-circuit to /onboarding
// whenever the local "is_onboarded" preference was unset, which black-holed every
// returning user after a fresh install (issue #187). If these tests fail, users with
// server-side accounts will be re-onboarded as if they were brand new.
// DO NOT DELETE OR WEAKEN THESE TESTS.

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public class PostLoginRouterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IPreferencesService> _mockPreferences;
    private readonly Mock<ISyncService> _mockSyncService;
    private readonly UserProfileRepository _profileRepo;
    private readonly PostLoginRouter _sut;

    public PostLoginRouterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _mockPreferences = new Mock<IPreferencesService>();
        _mockSyncService = new Mock<ISyncService>();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlite(_connection));
        services.AddSingleton<IPreferencesService>(_mockPreferences.Object);
        services.AddSingleton<ISyncService>(_mockSyncService.Object);

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        _profileRepo = new UserProfileRepository(
            _serviceProvider,
            NullLogger<UserProfileRepository>.Instance);

        _sut = new PostLoginRouter(
            _mockSyncService.Object,
            _profileRepo,
            NullLogger<PostLoginRouter>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private void SeedProfile(string id, string? targetLang, string? nativeLang, string name = "Test User")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile
        {
            Id = id,
            Name = name,
            TargetLanguage = targetLang ?? "",
            NativeLanguage = nativeLang ?? "",
            DisplayLanguage = "English"
        });
        db.SaveChanges();

        _mockPreferences
            .Setup(p => p.Get("active_profile_id", string.Empty))
            .Returns(id);
    }

    [Fact]
    public async Task DecideRouteAsync_WhenInitialSyncInProgress_DefersDecision()
    {
        // Arrange — sync is in flight (just-after-login window)
        _mockSyncService.Setup(s => s.IsInitialSyncInProgress).Returns(true);

        // Act
        var route = await _sut.DecideRouteAsync();

        // Assert — caller (MainLayout) must wait, NOT route to /onboarding
        route.DeferUntilSyncCompletes.Should().BeTrue();
        route.Path.Should().BeNull();
        route.ShouldMarkOnboarded.Should().BeFalse();
    }

    [Fact]
    public async Task DecideRouteAsync_PopulatedProfile_RoutesToDashboardAndMarksOnboarded()
    {
        // Arrange — sync done, fully populated profile (returning user on fresh install)
        _mockSyncService.Setup(s => s.IsInitialSyncInProgress).Returns(false);
        SeedProfile("user-1", "Korean", "English");

        // Act
        var route = await _sut.DecideRouteAsync();

        // Assert
        route.Path.Should().Be("/");
        route.DeferUntilSyncCompletes.Should().BeFalse();
        route.ShouldMarkOnboarded.Should().BeTrue();
    }

    [Fact]
    public async Task DecideRouteAsync_NoLocalProfile_RoutesToOnboarding()
    {
        // Arrange — sync done, no local profile (brand-new account)
        _mockSyncService.Setup(s => s.IsInitialSyncInProgress).Returns(false);
        // No SeedProfile call — DB has zero UserProfile rows.

        // Act
        var route = await _sut.DecideRouteAsync();

        // Assert
        route.Path.Should().Be("/onboarding");
        route.DeferUntilSyncCompletes.Should().BeFalse();
        route.ShouldMarkOnboarded.Should().BeFalse();
    }

    [Fact]
    public async Task DecideRouteAsync_ProfileMissingTargetLanguage_RoutesToOnboarding()
    {
        // Arrange — sync done, profile exists but TargetLanguage is empty
        _mockSyncService.Setup(s => s.IsInitialSyncInProgress).Returns(false);
        SeedProfile("user-2", targetLang: "", nativeLang: "English");

        // Act
        var route = await _sut.DecideRouteAsync();

        // Assert
        route.Path.Should().Be("/onboarding",
            "an incomplete profile (no target language) means onboarding never finished");
        route.ShouldMarkOnboarded.Should().BeFalse();
    }

    [Fact]
    public async Task DecideRouteAsync_ProfileMissingNativeLanguage_RoutesToOnboarding()
    {
        // Arrange — sync done, profile exists but NativeLanguage is empty
        _mockSyncService.Setup(s => s.IsInitialSyncInProgress).Returns(false);
        SeedProfile("user-3", targetLang: "Korean", nativeLang: "");

        // Act
        var route = await _sut.DecideRouteAsync();

        // Assert
        route.Path.Should().Be("/onboarding",
            "an incomplete profile (no native language) means onboarding never finished");
        route.ShouldMarkOnboarded.Should().BeFalse();
    }
}
