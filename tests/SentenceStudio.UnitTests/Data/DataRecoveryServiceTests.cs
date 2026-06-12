// REGRESSION TESTS — DataRecoveryService cross-tenant data corruption prevention
//
// Root cause (2026-06-11): DataRecoveryService.RecoverOrphanedDataAsync silently retagged
// Captain's 8 months of data (2,584 rows) to squad-jayne's account when squad-jayne signed
// in to his Mac Catalyst for E2E testing. CoreSync then propagated the corruption to Postgres.
//
// Full RCA: .squad/decisions/inbox/captain-rca-datarecoveryservice-cross-tenant-corruption.md
//
// Three safeguards are verified here:
//   1. Email mismatch abort — the exact production scenario (different email = different human).
//   2. Temporal sanity abort — orphan data older than the new account's creation date.
//   3. First-run gate — _data_recovery_complete flag makes the service one-shot.
//   4. Legitimate recovery still works — same email + recent orphan data → retag succeeds.
//
// DO NOT DELETE OR WEAKEN THESE TESTS.

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

public sealed class DataRecoveryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IPreferencesService> _mockPreferences;
    private readonly DataRecoveryLogCollector _logs;
    private readonly DataRecoveryService _sut;
    private readonly Dictionary<string, object?> _prefsStore = new(StringComparer.Ordinal);

    public DataRecoveryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _mockPreferences = new Mock<IPreferencesService>();

        // Default: _data_recovery_complete is not set (empty string = not done yet)
        _mockPreferences
            .Setup(p => p.Get(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(string.Empty);

        // Capture Set<string> calls so tests can inspect what was written
        _mockPreferences
            .Setup(p => p.Set(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((key, value) => _prefsStore[key] = value);

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        _logs = new DataRecoveryLogCollector();
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.ClearProviders();
            b.AddProvider(_logs);
            b.SetMinimumLevel(LogLevel.Debug);
        });

        _sut = new DataRecoveryService(
            _serviceProvider,
            loggerFactory.CreateLogger<DataRecoveryService>(),
            _mockPreferences.Object);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    // ── TEST 1: The exact production scenario ─────────────────────────────────────────────────
    // Captain's data (dave@ortinau.com) lives on his Mac Catalyst.
    // squad-jayne signs in for E2E testing → DataRecoveryService must ABORT.
    // Nothing must be retagged; nothing must be deleted; a Warning must say "ABORTED".
    [Fact]
    public async Task RecoverOrphanedDataAsync_WhenOrphanEmailDiffersFromNewUser_AbortsWithWarningAndNoMutation()
    {
        // Arrange
        const string captainId    = "ba20bcc5-dave-0000-0000-000000000000";
        const string jayneId      = "5b999582-jayne-000-0000-000000000000";
        const string captainEmail = "dave@ortinau.com";
        const string jayneEmail   = "squad-jayne@sentencestudio.test";

        SeedUserProfile(captainId, "Dave Ortinau",   captainEmail, DateTime.UtcNow.AddMonths(-8));
        SeedLearningResource(captainId, "Korean Vocab Pack",  DateTime.UtcNow.AddMonths(-8));
        SeedLearningResource(captainId, "Podcast Episode 47", DateTime.UtcNow.AddMonths(-3));

        // Act — squad-jayne signs in on Captain's device
        await _sut.RecoverOrphanedDataAsync(jayneId, jayneEmail);

        // Assert — NO data mutation
        var resources = GetAllLearningResources();
        resources.Should().HaveCount(2, "both of Captain's resources must survive");
        resources.Should().AllSatisfy(r =>
            r.UserProfileId.Should().Be(captainId,
                "captain's LearningResources must NOT be retagged to squad-jayne"));

        var profiles = GetAllUserProfiles();
        profiles.Should().ContainSingle(p => p.Id == captainId,
            "captain's UserProfile must NOT be deleted");

        // Assert — ABORTED warning was logged
        _logs.HasWarningContaining("ABORTED").Should().BeTrue(
            "an ABORTED warning must be emitted when orphan email != new user's email");
        // Email is masked in the log — verify the domain portion is traceable
        _logs.HasWarningContaining("@ortinau.com").Should().BeTrue(
            "the orphan email's domain must survive masking so incident responders can trace the account");
    }

    // ── TEST 2: Temporal sanity abort ────────────────────────────────────────────────────────
    // New account was just created today; orphan data is from 6 months ago.
    // Temporally impossible — must ABORT.
    [Fact]
    public async Task RecoverOrphanedDataAsync_WhenOrphanDataPredatesNewAccount_AbortsWithWarning()
    {
        // Arrange
        const string captainId = "ba20bcc5-dave-0000-0000-000000000000";
        const string jayneId   = "5b999582-jayne-000-0000-000000000000";

        var sixMonthsAgo = DateTime.UtcNow.AddMonths(-6);
        var today        = DateTime.UtcNow;

        // Jayne's profile exists locally (freshly synced after registration) — created TODAY
        SeedUserProfile(jayneId, "Jayne Test", "squad-jayne@sentencestudio.test", today);
        // Captain's orphan resource is from 6 months ago (predates Jayne's account by far)
        SeedLearningResource(captainId, "Old resource from Oct 2025", sixMonthsAgo);

        // Act — no email passed so the email safeguard doesn't fire; only temporal check runs
        await _sut.RecoverOrphanedDataAsync(jayneId, newUserEmail: null);

        // Assert — no retagging
        var resources = GetAllLearningResources();
        resources.Should().AllSatisfy(r =>
            r.UserProfileId.Should().Be(captainId,
                "orphan resource must not be retagged when temporal sanity check fails"));

        _logs.HasWarningContaining("ABORTED").Should().BeTrue(
            "a temporal mismatch must emit an ABORTED warning");
    }

    // ── TEST 3: First-run gate honoured ───────────────────────────────────────────────────────
    // _data_recovery_complete_{jayneId} = "true" → service exits before touching ANY data.
    [Fact]
    public async Task RecoverOrphanedDataAsync_WhenFirstRunFlagIsTrue_ExitsImmediatelyWithoutMutation()
    {
        const string captainId = "ba20bcc5-dave-0000-0000-000000000000";
        const string jayneId   = "5b999582-jayne-000-0000-000000000000";

        // Arrange — per-user flag already set for jayne's id
        _mockPreferences
            .Setup(p => p.Get($"_data_recovery_complete_{jayneId}", It.IsAny<string>()))
            .Returns("true");

        SeedUserProfile(captainId, "Dave Ortinau", "dave@ortinau.com", DateTime.UtcNow.AddMonths(-8));
        SeedLearningResource(captainId, "Precious resource", DateTime.UtcNow.AddMonths(-8));

        // Act
        await _sut.RecoverOrphanedDataAsync(jayneId, "squad-jayne@sentencestudio.test");

        // Assert — nothing mutated (gate fired before reaching any DB writes)
        var resources = GetAllLearningResources();
        resources.Should().AllSatisfy(r =>
            r.UserProfileId.Should().Be(captainId, "first-run gate must prevent any data mutation"));

        // Assert — no new Set calls (flag was already true; service should not write anything)
        _mockPreferences.Verify(p => p.Set(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "when the first-run gate fires, no preference writes should occur");
    }

    // ── TEST 4: Legitimate recovery still works ───────────────────────────────────────────────
    // Same human re-registered after a server wipe:
    //   - Same email on both old profile and new profile.
    //   - Orphan data is recent (well within the 1-day temporal window).
    // All safeguards must pass and orphan rows must be retagged to the new id.
    [Fact]
    public async Task RecoverOrphanedDataAsync_WhenSameEmailAndRecentData_RetagsAndSetsFirstRunFlag()
    {
        // Arrange — server wipe scenario: dave had id=oldId, re-registered with id=newId
        const string oldId = "old-dave-id-aaa";
        const string newId = "new-dave-id-bbb";
        const string email = "dave@ortinau.com";

        var thirtyMinutesAgo = DateTime.UtcNow.AddMinutes(-30);
        var fiveMinutesAgo   = DateTime.UtcNow.AddMinutes(-5);

        // New profile row exists locally (just synced from server after re-registration)
        SeedUserProfile(newId, "Dave Ortinau", email, fiveMinutesAgo);
        // Old profile (orphan) — same email, slightly older
        SeedUserProfile(oldId, "Dave Ortinau", email, thirtyMinutesAgo);
        // Orphan LearningResource carries the old id
        SeedLearningResource(oldId, "Resource to recover", thirtyMinutesAgo);

        // Act
        await _sut.RecoverOrphanedDataAsync(newId, email);

        // Assert — resource is now under the new id
        var resources = GetAllLearningResources();
        resources.Should().AllSatisfy(r =>
            r.UserProfileId.Should().Be(newId,
                "orphan LearningResource must be retagged to the new user id after legitimate recovery"));

        // Assert — per-user first-run flag was set (not the old global key)
        var expectedKey = $"_data_recovery_complete_{newId}";
        _prefsStore.Should().ContainKey(expectedKey,
            "per-user first-run flag must be set after successful recovery");
        _prefsStore[expectedKey].Should().Be("true",
            "flag value must be 'true' so future logins skip the scan");
    }

    // ── TEST 5: No-email + no-local-profile = ABORT (R1 regression) ──────────────────────────
    // Models the exact StoreTokens ordering in production:
    //   1. new user's profile is NOT in local SQLite (sync hasn't run yet)
    //   2. JWT has no email claim (newUserEmail = null)
    //   3. Orphan data with timestamps exists from the previous user
    // Both safeguards "fail open" in the original code → data gets corrupted.
    // After the R1 fix, the temporal gate fires because the new profile is absent AND
    // orphan timestamps are present.
    [Fact]
    public async Task RecoverOrphanedDataAsync_WhenNoLocalNewUserProfileAndNoEmail_AbortsWithWarning()
    {
        // Arrange
        const string captainId = "ba20bcc5-dave-0000-0000-000000000000";
        const string jayneId   = "5b999582-jayne-000-0000-000000000000";

        // Captain's profile + resource exist on device (orphans)
        SeedUserProfile(captainId, "Dave Ortinau", "dave@ortinau.com", DateTime.UtcNow.AddMonths(-8));
        SeedLearningResource(captainId, "8 months of vocab work", DateTime.UtcNow.AddMonths(-8));

        // NOTE: jayne's UserProfile is NOT seeded — simulates StoreTokens ordering
        //       where recovery runs before sync (line 425 vs line 464).

        // Act — no email claim in JWT (simulates empty/missing JWT email)
        await _sut.RecoverOrphanedDataAsync(jayneId, newUserEmail: null);

        // Assert — no mutation despite both primary safeguard inputs being absent
        var resources = GetAllLearningResources();
        resources.Should().AllSatisfy(r =>
            r.UserProfileId.Should().Be(captainId,
                "recovery must abort when new user has no local profile and email is unavailable"));

        var profiles = GetAllUserProfiles();
        profiles.Should().ContainSingle(p => p.Id == captainId,
            "captain's UserProfile must not be deleted");

        // Assert — temporal abort fired
        _logs.HasWarningContaining("ABORTED").Should().BeTrue(
            "ABORTED warning must fire when new user profile is absent and orphan timestamps exist");
        _logs.HasWarningContaining("Cannot verify temporal sanity").Should().BeTrue(
            "warning must specifically identify the absent-new-profile case for incident tracing");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private void SeedUserProfile(string id, string name, string? email, DateTime createdAt)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile
        {
            Id = id,
            Name = name,
            Email = email,
            TargetLanguage = "Korean",
            NativeLanguage = "English",
            DisplayLanguage = "English",
            CreatedAt = createdAt,
        });
        db.SaveChanges();
    }

    private void SeedLearningResource(string userProfileId, string title, DateTime createdAt)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LearningResources.Add(new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            UserProfileId = userProfileId,
            CreatedAt = createdAt,
        });
        db.SaveChanges();
    }

    private List<LearningResource> GetAllLearningResources()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.LearningResources.ToList();
    }

    private List<UserProfile> GetAllUserProfiles()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.UserProfiles.ToList();
    }
}

// ── Test-local logger ────────────────────────────────────────────────────────────────────────
// Captures log entries so tests can assert on warning content.
// Kept internal to this file to avoid naming conflicts.

internal sealed class DataRecoveryLogCollector : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public ILogger CreateLogger(string categoryName) => new Logger(_entries);

    public bool HasWarningContaining(params string[] terms) =>
        _entries.Any(e =>
            e.Level >= LogLevel.Warning
            && terms.All(t => e.Message.Contains(t, StringComparison.OrdinalIgnoreCase)));

    public void Dispose() { }

    private sealed class Logger : ILogger
    {
        private readonly List<(LogLevel, string)> _entries;

        public Logger(List<(LogLevel, string)> entries) => _entries = entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
