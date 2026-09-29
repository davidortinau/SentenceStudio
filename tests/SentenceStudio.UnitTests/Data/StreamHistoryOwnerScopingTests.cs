using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

public sealed class StreamHistoryOwnerScopingTests
{
    private const string UserA = "history-owner-a";
    private const string UserB = "history-owner-b";

    [Fact]
    public async Task EveryRead_FiltersByOwner_AndHidesLegacyRows()
    {
        await using var store = await HistoryStore.CreateAsync([UserA, UserB]);
        var ownedA = await store.SeedAsync(UserA);
        var ownedB = await store.SeedAsync(UserB);
        var legacy = await store.SeedAsync(null);
        store.Scope.ActiveId = UserA;

        (await store.Repository.GetAllStreamHistoryAsync())
            .Select(h => h.Id).Should().Equal(ownedA.Id);
        (await store.Repository.GetStreamHistoryAsync(ownedA.Id))!.Id.Should().Be(ownedA.Id);
        (await store.Repository.GetStreamHistoryAsync(ownedB.Id)).Should().BeNull();
        (await store.Repository.GetStreamHistoryAsync(legacy.Id)).Should().BeNull();
        (await store.Repository.SearchStreamHistoryAsync("shared"))
            .Select(h => h.Id).Should().Equal(ownedA.Id);
        (await store.Repository.GetStreamHistoryByVoiceAsync("same-voice"))
            .Select(h => h.Id).Should().Equal(ownedA.Id);
        (await store.Repository.GetStreamHistoryByPhraseAndVoiceAsync("shared phrase", "same-voice"))!
            .Id.Should().Be(ownedA.Id);

        store.Scope.ActiveId = UserB;
        (await store.Repository.GetAllStreamHistoryAsync())
            .Select(h => h.Id).Should().Equal(ownedB.Id);
        (await store.Repository.GetStreamHistoryAsync(ownedA.Id)).Should().BeNull();
        (await store.Repository.SearchStreamHistoryAsync("shared"))
            .Select(h => h.Id).Should().Equal(ownedB.Id);
        (await store.Repository.GetStreamHistoryByVoiceAsync("same-voice"))
            .Select(h => h.Id).Should().Equal(ownedB.Id);
        (await store.Repository.GetStreamHistoryByPhraseAndVoiceAsync("shared phrase", "same-voice"))!
            .Id.Should().Be(ownedB.Id);
    }

    [Fact]
    public async Task Save_StampsActiveOwner_AndRefusesAbsentUnknownOrConflictingIdentity()
    {
        await using var store = await HistoryStore.CreateAsync([UserA, UserB]);
        store.Scope.ActiveId = UserA;

        var saved = new StreamHistory { Phrase = "my audio" };
        (await store.Repository.SaveStreamHistoryAsync(saved)).Should().Be(1);
        saved.UserProfileId.Should().Be(UserA);
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Phrase = "foreign owner supplied",
            UserProfileId = UserB
        })).Should().Be(0);

        store.Scope.ActiveId = null;
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Phrase = "no active user"
        })).Should().Be(0);
        store.Scope.ActiveId = "profile-missing-from-db";
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Phrase = "unknown user"
        })).Should().Be(0);
        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryAsync(saved.Id)).Should().BeNull();
        (await store.Repository.DeleteStreamHistoryAsync(saved)).Should().Be(0);

        var stored = await store.RowsAsync();
        stored.Should().ContainSingle().Which.UserProfileId.Should().Be(UserA);
        store.Logger.Warnings.Should().Contain(m => m.Contains("without an active user"));
        store.Logger.Warnings.Should().Contain(m => m.Contains("no local profile"));
    }

    [Fact]
    public async Task Update_RejectsForeignLegacySpoofedAndNonexistentRows()
    {
        await using var store = await HistoryStore.CreateAsync([UserA, UserB]);
        var ownedA = await store.SeedAsync(UserA);
        var ownedB = await store.SeedAsync(UserB);
        var legacy = await store.SeedAsync(null);
        store.Scope.ActiveId = UserA;

        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Id = ownedB.Id, Phrase = "hijacked"
        })).Should().Be(0);
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Id = legacy.Id, Phrase = "claimed"
        })).Should().Be(0);
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Id = ownedA.Id, UserProfileId = UserB, Phrase = "owner spoof"
        })).Should().Be(0);
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Id = int.MaxValue, Phrase = "nonexistent id"
        })).Should().Be(0);

        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Id = ownedA.Id, Phrase = "my revised audio", VoiceId = "my-voice"
        })).Should().Be(1);

        var rows = await store.RowsAsync();
        rows.Should().HaveCount(3);
        rows.Single(h => h.Id == ownedA.Id).Should().Match<StreamHistory>(
            h => h.Phrase == "my revised audio" && h.VoiceId == "my-voice"
                && h.UserProfileId == UserA && h.CreatedAt == ownedA.CreatedAt);
        rows.Single(h => h.Id == ownedB.Id).Phrase.Should().Be("shared phrase");
        rows.Single(h => h.Id == legacy.Id).UserProfileId.Should().BeNull();
    }

    [Fact]
    public async Task Delete_OnlyRemovesTrackedRowOwnedByActiveUser()
    {
        await using var store = await HistoryStore.CreateAsync([UserA, UserB]);
        var ownedA = await store.SeedAsync(UserA);
        var ownedB = await store.SeedAsync(UserB);
        var legacy = await store.SeedAsync(null);
        store.Scope.ActiveId = UserA;

        (await store.Repository.DeleteStreamHistoryAsync(ownedB)).Should().Be(0);
        (await store.Repository.DeleteStreamHistoryAsync(legacy)).Should().Be(0);
        (await store.Repository.DeleteStreamHistoryAsync(new StreamHistory
        {
            Id = ownedA.Id, UserProfileId = UserB
        })).Should().Be(0);
        (await store.RowsAsync()).Should().HaveCount(3);

        (await store.Repository.DeleteStreamHistoryAsync(ownedA)).Should().Be(1);
        (await store.RowsAsync()).Select(h => h.Id)
            .Should().BeEquivalentTo([ownedB.Id, legacy.Id]);
        store.Scope.ActiveId = UserB;
        (await store.Repository.DeleteStreamHistoryAsync(ownedB)).Should().Be(1);
        (await store.RowsAsync()).Should().ContainSingle().Which.Id.Should().Be(legacy.Id);
    }

    [Fact]
    public async Task NoActiveUser_AllReadsAndMutationsFailClosed()
    {
        await using var store = await HistoryStore.CreateAsync([UserA, UserB]);
        var owned = await store.SeedAsync(UserA);
        var legacy = await store.SeedAsync(null);
        store.Scope.ActiveId = null;

        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryAsync(owned.Id)).Should().BeNull();
        (await store.Repository.GetStreamHistoryAsync(legacy.Id)).Should().BeNull();
        (await store.Repository.SearchStreamHistoryAsync("shared")).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryByVoiceAsync("same-voice")).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryByPhraseAndVoiceAsync("shared phrase", "same-voice"))
            .Should().BeNull();
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory())).Should().Be(0);
        (await store.Repository.DeleteStreamHistoryAsync(owned)).Should().Be(0);
        (await store.Repository.DeleteStreamHistoryAsync(legacy)).Should().Be(0);

        var rows = await store.RowsAsync();
        rows.Select(h => h.Id).Should().BeEquivalentTo([owned.Id, legacy.Id]);
        rows.Single(h => h.Id == legacy.Id).UserProfileId.Should().BeNull();
        store.Logger.Warnings.Count.Should().BeGreaterOrEqualTo(7);
    }

    [Fact]
    public async Task ConflictingClaimAndPreference_FailsClosedRatherThanChoosingAnOwner()
    {
        await using var store = await HistoryStore.CreateAsync([UserA, UserB]);
        var owned = await store.SeedAsync(UserA);
        store.Scope.ActiveId = UserA;
        store.Preferences.ActiveId = UserB;

        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.Repository.DeleteStreamHistoryAsync(owned)).Should().Be(0);
        (await store.RowsAsync()).Should().ContainSingle().Which.Id.Should().Be(owned.Id);
        store.Logger.Warnings.Should().Contain(m => m.Contains("conflicting identities"));
    }

    [Fact]
    public async Task ServerReadWithDeviceStylePreference_DoesNotBackfillSingleProfileLegacyRows()
    {
        await using var store = await HistoryStore.CreateAsync([UserA], deviceScope: true);
        var legacy = await store.SeedAsync(null);
        store.Preferences.ActiveId = UserA;

        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryAsync(legacy.Id)).Should().BeNull();
        (await store.RowsAsync()).Should().ContainSingle().Which.UserProfileId.Should().BeNull(
            "the net10 server build must never infer the owner, even with one profile");

        var newEntry = new StreamHistory { Phrase = "scoped by login-selected profile" };
        (await store.Repository.SaveStreamHistoryAsync(newEntry)).Should().Be(1);
        newEntry.UserProfileId.Should().Be(UserA);
        (await store.Repository.GetAllStreamHistoryAsync())
            .Select(h => h.Id).Should().Equal(newEntry.Id);

        store.Preferences.ActiveId = null;
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory())).Should().Be(0);
    }

    [Fact]
    public async Task SingleProfile_RecentLegacyRowsRemainUnownedAndInaccessible()
    {
        var recordedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        await using var store = await HistoryStore.CreateAsync(
            [UserA], profileCreatedAt: recordedAt.AddMinutes(-10));
        var legacyOne = await store.SeedAsync(null, recordedAt);
        var legacyTwo = await store.SeedAsync(null, recordedAt.AddMinutes(1));
        store.Scope.ActiveId = UserA;

        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryAsync(legacyOne.Id)).Should().BeNull();
        (await store.Repository.SearchStreamHistoryAsync("shared")).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryByVoiceAsync("same-voice")).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryByPhraseAndVoiceAsync("shared phrase", "same-voice"))
            .Should().BeNull();
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Id = legacyOne.Id, Phrase = "claim without provenance"
        })).Should().Be(0);
        (await store.Repository.DeleteStreamHistoryAsync(legacyTwo)).Should().Be(0);

        var rows = await store.RowsAsync();
        rows.Select(h => h.Id).Should().BeEquivalentTo([legacyOne.Id, legacyTwo.Id]);
        rows.Select(h => h.UserProfileId).Should().OnlyContain(id => id == null);
        rows.Select(h => h.AudioFilePath).Should().OnlyContain(path => path == "original-audio-path.mp3");
        store.Logger.Warnings.Count(m => m.Contains("legacy rows remain unowned"))
            .Should().Be(1, "the reason is logged without repeating it for every read");
        store.Logger.Warnings.Should().Contain(m => m.Contains("no per-row owner identity"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task LegacyRows_WithZeroOrTwoProfiles_StayUnowned(int profileCount)
    {
        string[] profiles = profileCount == 0 ? [] : [UserA, UserB];
        await using var store = await HistoryStore.CreateAsync(profiles);
        await store.SeedAsync(null);
        store.Scope.ActiveId = UserA;

        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.RowsAsync()).Should().ContainSingle().Which.UserProfileId.Should().BeNull();
    }

    [Fact]
    public async Task OlderLegacyRecording_StaysUnownedRatherThanRelyingOnItsAge()
    {
        await using var store = await HistoryStore.CreateAsync([UserA]);
        var legacy = await store.SeedAsync(null, DateTime.UtcNow.AddDays(-20));
        store.Scope.ActiveId = UserA;

        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.RowsAsync()).Single(h => h.Id == legacy.Id)
            .UserProfileId.Should().BeNull();
    }

    [Fact]
    public async Task DeletedProfile_ReplacedSameDay_DoesNotGrantNewUserLegacyAudio()
    {
        var day = new DateTime(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc);
        await using var store = await HistoryStore.CreateAsync(
            [UserA], deviceScope: true, profileCreatedAt: day);
        var legacy = await store.SeedAsync(null, day.AddMinutes(30));
        var profiles = store.Profiles;
        var previousProfile = (await profiles.GetByIdAsync(UserA))!;

        (await profiles.DeleteAsync(previousProfile)).Should().Be(1);
        var replacement = new UserProfile { Id = UserB, CreatedAt = day.AddHours(1) };
        (await profiles.SaveAsync(replacement)).Should().Be(1);
        previousProfile.CreatedAt.Date.Should().Be(replacement.CreatedAt.Date);
        store.Preferences.ActiveId = UserB;

        await using var db = store.NewDb();
        (await db.UserProfiles.AsNoTracking().Select(p => p.Id).ToListAsync())
            .Should().ContainSingle().Which.Should().Be(UserB);
        (await store.Repository.GetAllStreamHistoryAsync()).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryAsync(legacy.Id)).Should().BeNull();
        (await store.Repository.SearchStreamHistoryAsync("shared")).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryByVoiceAsync("same-voice")).Should().BeEmpty();
        (await store.Repository.GetStreamHistoryByPhraseAndVoiceAsync("shared phrase", "same-voice"))
            .Should().BeNull();
        (await store.Repository.SaveStreamHistoryAsync(new StreamHistory
        {
            Id = legacy.Id, Phrase = "new profile attempting to claim old audio"
        })).Should().Be(0);
        (await store.Repository.DeleteStreamHistoryAsync(legacy)).Should().Be(0);

        var newEntry = new StreamHistory { Phrase = "replacement's recording" };
        (await store.Repository.SaveStreamHistoryAsync(newEntry)).Should().Be(1);
        newEntry.UserProfileId.Should().Be(UserB);
        (await store.Repository.GetAllStreamHistoryAsync())
            .Select(h => h.Id).Should().Equal(newEntry.Id);
        (await store.Repository.GetStreamHistoryAsync(legacy.Id)).Should().BeNull();

        var retained = await store.RowsAsync();
        retained.Should().HaveCount(2);
        retained.Single(h => h.Id == legacy.Id).UserProfileId.Should().BeNull();
        retained.Single(h => h.Id == legacy.Id).AudioFilePath.Should().Be("original-audio-path.mp3");
        retained.Single(h => h.Id == newEntry.Id).UserProfileId.Should().Be(UserB);
        var warning = store.Logger.Warnings.Single(m => m.Contains("legacy rows remain unowned"));
        warning.Should().Contain("no per-row owner identity");
        warning.Should().NotContain(UserA);
        warning.Should().NotContain(UserB);
        warning.Should().NotContain(legacy.AudioFilePath!);
    }

    private sealed class HistoryStore : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private ServiceProvider _services = null!;

        public MutableScope Scope { get; } = new();
        public MutablePreferences Preferences { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public StreamHistoryRepository Repository { get; private set; } = null!;

        public UserProfileRepository Profiles =>
            new(_services, NullLogger<UserProfileRepository>.Instance);

        public static async Task<HistoryStore> CreateAsync(
            string[] profileIds, bool deviceScope = false, DateTime? profileCreatedAt = null)
        {
            var store = new HistoryStore();
            await store._connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(store._connection));
            services.AddSingleton<IPreferencesService>(store.Preferences);
            services.AddSingleton<IUserScopeProvider>(
                deviceScope ? new DeviceUserScopeProvider() : store.Scope);
            store._services = services.BuildServiceProvider();
            store.Repository = new StreamHistoryRepository(store._services, store.Logger);

            await using var db = store.NewDb();
            await db.Database.EnsureCreatedAsync();
            db.UserProfiles.AddRange(profileIds.Select(id => new UserProfile
            {
                Id = id,
                CreatedAt = profileCreatedAt ?? DateTime.UtcNow.AddDays(-7)
            }));
            await db.SaveChangesAsync();
            return store;
        }

        public ApplicationDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection).Options);

        public async Task<StreamHistory> SeedAsync(string? ownerId, DateTime? createdAt = null)
        {
            await using var db = NewDb();
            var row = new StreamHistory
            {
                UserProfileId = ownerId,
                Phrase = "shared phrase",
                VoiceId = "same-voice",
                AudioFilePath = "original-audio-path.mp3",
                CreatedAt = createdAt ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.StreamHistories.Add(row);
            await db.SaveChangesAsync();
            return row;
        }

        public async Task<List<StreamHistory>> RowsAsync()
        {
            await using var db = NewDb();
            return await db.StreamHistories.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class MutableScope : IUserScopeProvider
    {
        public string? ActiveId { get; set; }
        public string UserProfileId =>
            TryGetUserProfileId(out var id) ? id : throw new UnauthorizedAccessException();

        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = ActiveId ?? string.Empty;
            return !string.IsNullOrWhiteSpace(userProfileId);
        }
    }

    private sealed class MutablePreferences : IPreferencesService
    {
        public string? ActiveId { get; set; }

        public T Get<T>(string key, T defaultValue) =>
            key == "active_profile_id" && typeof(T) == typeof(string)
                ? (T)(object)(ActiveId ?? string.Empty)
                : defaultValue;

        public void Set<T>(string key, T value)
        {
            if (key == "active_profile_id")
                ActiveId = value?.ToString();
        }

        public void Remove(string key)
        {
            if (key == "active_profile_id")
                ActiveId = null;
        }

        public void Clear() => ActiveId = null;
    }

    private sealed class RecordingLogger : ILogger<StreamHistoryRepository>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(
            LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
