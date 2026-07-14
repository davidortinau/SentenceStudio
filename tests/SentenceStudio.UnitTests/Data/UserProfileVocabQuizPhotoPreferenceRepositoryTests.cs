// REGRESSION TESTS — VocabQuizShowTextWithPhoto round-trips through SQLite.
//
// Follows the same in-memory-SQLite pattern as UserProfileRepositoryGetAsyncTests.
// Proves:
//   1. The column exists in the EF model and persists through SQLite.
//   2. Default value is false when not explicitly set.
//   3. Two profiles store independent values (multi-tenant scoping).
//   4. Empty userId does NOT read/write unscoped data.
//
// Does NOT test photo visibility — photo behavior is unchanged.
// Does NOT modify production code — Wash owns the model/migration.

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
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.Data;

public sealed class UserProfileVocabQuizPhotoPreferenceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IPreferencesService> _mockPreferences;
    private readonly CollectingLoggerProvider _logs;
    private readonly UserProfileRepository _sut;

    public UserProfileVocabQuizPhotoPreferenceRepositoryTests()
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

    private void SeedProfile(string id, string name, bool vocabQuizShowTextWithPhoto = false)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile
        {
            Id = id,
            Name = name,
            TargetLanguage = "Korean",
            NativeLanguage = "English",
            DisplayLanguage = "English",
            CreatedAt = DateTime.UtcNow,
            VocabQuizShowTextWithPhoto = vocabQuizShowTextWithPhoto,
        });
        db.SaveChanges();
    }

    private void SetActiveProfileId(string id)
    {
        _mockPreferences
            .Setup(p => p.Get("active_profile_id", It.IsAny<string>()))
            .Returns(id);
    }

    private UserProfile? ReadProfileDirectly(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.UserProfiles.AsNoTracking().FirstOrDefault(p => p.Id == id);
    }

    [Fact]
    public void Column_DefaultsToFalse_WhenNotExplicitlySet()
    {
        SeedProfile("default-test", "Default Tester");

        var profile = ReadProfileDirectly("default-test");

        profile.Should().NotBeNull();
        profile!.VocabQuizShowTextWithPhoto.Should().BeFalse(
            "a profile seeded without setting VocabQuizShowTextWithPhoto must default to false in SQLite");
    }

    [Fact]
    public void Column_RoundTrips_TrueValue()
    {
        SeedProfile("opt-in-user", "Opt-In User", vocabQuizShowTextWithPhoto: true);

        var profile = ReadProfileDirectly("opt-in-user");

        profile.Should().NotBeNull();
        profile!.VocabQuizShowTextWithPhoto.Should().BeTrue(
            "a profile explicitly set to true must round-trip through SQLite as true");
    }

    [Fact]
    public void Column_RoundTrips_FalseValue()
    {
        SeedProfile("opt-out-user", "Opt-Out User", vocabQuizShowTextWithPhoto: false);

        var profile = ReadProfileDirectly("opt-out-user");

        profile.Should().NotBeNull();
        profile!.VocabQuizShowTextWithPhoto.Should().BeFalse(
            "a profile explicitly set to false must round-trip through SQLite as false");
    }

    [Fact]
    public void TwoProfiles_StoreIndependentValues_InSqlite()
    {
        SeedProfile("user-alice", "Alice", vocabQuizShowTextWithPhoto: true);
        SeedProfile("user-bob", "Bob", vocabQuizShowTextWithPhoto: false);

        var alice = ReadProfileDirectly("user-alice");
        var bob = ReadProfileDirectly("user-bob");

        alice.Should().NotBeNull();
        bob.Should().NotBeNull();
        alice!.VocabQuizShowTextWithPhoto.Should().BeTrue("Alice opted in");
        bob!.VocabQuizShowTextWithPhoto.Should().BeFalse("Bob kept the default");
    }

    [Fact]
    public async Task GetAsync_EmptyUserId_DoesNotReturnUnscopedProfile()
    {
        // Enforce multi-tenant rule: empty userId must not leak cross-tenant data.
        SeedProfile("leaked-user", "Leaked User", vocabQuizShowTextWithPhoto: true);
        SetActiveProfileId(string.Empty);

        var result = await _sut.GetAsync();

        result.Should().BeNull(
            "empty active_profile_id must return null, not leak a profile with VocabQuizShowTextWithPhoto=true");
    }

    [Fact]
    public async Task GetAsync_MatchingUserId_ReturnsPersisted_VocabQuizShowTextWithPhoto()
    {
        SeedProfile("matched-user", "Matched User", vocabQuizShowTextWithPhoto: true);
        SetActiveProfileId("matched-user");

        var result = await _sut.GetAsync();

        result.Should().NotBeNull();
        result!.VocabQuizShowTextWithPhoto.Should().BeTrue(
            "GetAsync with matching active_profile_id must return the persisted preference value");
    }

    [Fact]
    public void Column_CanBeUpdated_InPlace()
    {
        SeedProfile("toggle-user", "Toggle User", vocabQuizShowTextWithPhoto: false);

        // Update in place
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = db.UserProfiles.First(p => p.Id == "toggle-user");
            profile.VocabQuizShowTextWithPhoto = true;
            db.SaveChanges();
        }

        var updated = ReadProfileDirectly("toggle-user");
        updated.Should().NotBeNull();
        updated!.VocabQuizShowTextWithPhoto.Should().BeTrue(
            "updating VocabQuizShowTextWithPhoto from false to true must persist through SQLite");
    }

    // SaveVocabQuizShowTextWithPhotoAsync regression tests

    [Fact]
    public async Task SaveVocabQuiz_MatchingUser_UpdatesOnlyShowTextWithPhoto_PreservesOtherFields()
    {
        SeedProfile("save-match", "Matchy McMatchFace", vocabQuizShowTextWithPhoto: false);
        var before = ReadProfileDirectly("save-match")!;

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync("save-match", true);

        result.Should().BeTrue("a matching profile should succeed");
        var after = ReadProfileDirectly("save-match")!;
        after.VocabQuizShowTextWithPhoto.Should().BeTrue("the targeted column must flip to true");

        // Unrelated fields must be untouched
        after.Name.Should().Be(before.Name, "Name must not change");
        after.NativeLanguage.Should().Be(before.NativeLanguage, "NativeLanguage must not change");
        after.TargetLanguage.Should().Be(before.TargetLanguage, "TargetLanguage must not change");
        after.PreferredSessionMinutes.Should().Be(before.PreferredSessionMinutes,
            "PreferredSessionMinutes must not change");
        after.Email.Should().Be(before.Email, "Email must not change");
        after.TargetCEFRLevel.Should().Be(before.TargetCEFRLevel, "TargetCEFRLevel must not change");
        after.IanaTimeZoneId.Should().Be(before.IanaTimeZoneId, "IanaTimeZoneId must not change");
    }

    [Fact]
    public async Task SaveVocabQuiz_AnotherUsersFields_RemainUnchanged()
    {
        SeedProfile("user-alpha", "Alpha", vocabQuizShowTextWithPhoto: false);
        SeedProfile("user-beta", "Beta", vocabQuizShowTextWithPhoto: true);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync("user-alpha", true);

        result.Should().BeTrue();
        var alpha = ReadProfileDirectly("user-alpha")!;
        var beta = ReadProfileDirectly("user-beta")!;

        alpha.VocabQuizShowTextWithPhoto.Should().BeTrue("alpha was the target");
        beta.VocabQuizShowTextWithPhoto.Should().BeTrue("beta's existing true value must be untouched");
        beta.Name.Should().Be("Beta", "beta's Name must be untouched");
    }

    [Fact]
    public async Task SaveVocabQuiz_EmptyUserId_ReturnsFalse_ChangesNothing()
    {
        SeedProfile("innocent-bystander", "Bystander", vocabQuizShowTextWithPhoto: false);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync(string.Empty, true);

        result.Should().BeFalse("empty userId must be rejected per multi-tenant scoping rule");
        var profile = ReadProfileDirectly("innocent-bystander")!;
        profile.VocabQuizShowTextWithPhoto.Should().BeFalse("no profile should be modified");
    }

    [Fact]
    public async Task SaveVocabQuiz_NullUserId_ReturnsFalse_ChangesNothing()
    {
        SeedProfile("null-guard-bystander", "Null Guard", vocabQuizShowTextWithPhoto: false);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync(null!, true);

        result.Should().BeFalse("null userId must be rejected per multi-tenant scoping rule");
        var profile = ReadProfileDirectly("null-guard-bystander")!;
        profile.VocabQuizShowTextWithPhoto.Should().BeFalse("no profile should be modified");
    }

    [Fact]
    public async Task SaveVocabQuiz_WhitespaceUserId_ReturnsFalse_ChangesNothing()
    {
        SeedProfile("ws-guard-bystander", "Whitespace Guard", vocabQuizShowTextWithPhoto: false);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync("   ", true);

        result.Should().BeFalse("whitespace-only userId must be rejected per multi-tenant scoping rule");
        var profile = ReadProfileDirectly("ws-guard-bystander")!;
        profile.VocabQuizShowTextWithPhoto.Should().BeFalse("no profile should be modified");
    }

    [Fact]
    public async Task SaveVocabQuiz_UnknownProfileId_ReturnsFalse()
    {
        SeedProfile("known-user", "Known", vocabQuizShowTextWithPhoto: false);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync("ghost-profile-xyz", true);

        result.Should().BeFalse("an unknown profile id must not create or modify anything");
        var known = ReadProfileDirectly("known-user")!;
        known.VocabQuizShowTextWithPhoto.Should().BeFalse("the existing profile must be untouched");
    }

    [Fact]
    public async Task SaveVocabQuiz_TrueToFalse_Persists()
    {
        SeedProfile("flip-to-false", "Flipper", vocabQuizShowTextWithPhoto: true);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync("flip-to-false", false);

        result.Should().BeTrue();
        var profile = ReadProfileDirectly("flip-to-false")!;
        profile.VocabQuizShowTextWithPhoto.Should().BeFalse("true -> false must persist");
    }

    [Fact]
    public async Task SaveVocabQuiz_FalseToTrue_Persists()
    {
        SeedProfile("flip-to-true", "Flipper", vocabQuizShowTextWithPhoto: false);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync("flip-to-true", true);

        result.Should().BeTrue();
        var profile = ReadProfileDirectly("flip-to-true")!;
        profile.VocabQuizShowTextWithPhoto.Should().BeTrue("false -> true must persist");
    }

    [Fact]
    public async Task SaveVocabQuiz_IdempotentSameValue_StillReturnsTrue()
    {
        SeedProfile("idempotent-user", "Idem Potent", vocabQuizShowTextWithPhoto: true);

        var result = await _sut.SaveVocabQuizShowTextWithPhotoAsync("idempotent-user", true);

        result.Should().BeTrue("setting the same value that already exists should still succeed");
        var profile = ReadProfileDirectly("idempotent-user")!;
        profile.VocabQuizShowTextWithPhoto.Should().BeTrue();
    }
}
