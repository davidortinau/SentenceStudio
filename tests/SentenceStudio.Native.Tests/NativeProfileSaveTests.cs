using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Profile;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Native profile Save (Captain decision 8d): server first, then the local row with
/// exactly the server's values; failures and owner changes leave the local row as it was.
/// </summary>
public sealed class NativeProfileSaveTests
{
    private const string OwnerA = "save-profile-a";
    private const string OwnerB = "save-profile-b";

    [Fact]
    public async Task SuccessWritesTheServerValuesLocally_AndLeavesOtherFieldsAndOwners()
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote
        {
            Respond = (_, fields) => new(NativeProfileRemoteOutcome.Saved,
                fields with { Name = "Server Name" }, "server@example.invalid")
        };
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("  Draft Name  "));
        Assert.True(coordinator.SetNativeLanguage("Japanese"));
        Assert.True(coordinator.SetTargetLanguage("French"));
        Assert.True(coordinator.SetDisplayLanguage("Korean"));
        Assert.True(coordinator.SetSessionMinutes(45));
        Assert.True(coordinator.SetTargetCefrLevel("C1"));

        Assert.True(await coordinator.SaveAsync());

        var sent = Assert.Single(remote.Requests);
        Assert.Equal(OwnerA, sent.Owner);
        Assert.Equal(new NativeProfileFields("Draft Name", "Japanese", "French", "Korean", 45, "C1"),
            sent.Fields);
        var state = coordinator.State;
        Assert.Equal(ProfileSaveStatus.Saved, state.SaveStatus);
        Assert.Equal("Profile_Saved", state.SaveMessageKey);
        Assert.Equal("Server Name", state.Draft.Name);
        Assert.Equal("server@example.invalid", state.Draft.Email);
        var stored = await fixture.GetAsync(OwnerA);
        Assert.Equal("Server Name", stored.Name);
        Assert.Equal("Japanese", stored.NativeLanguage);
        Assert.Equal("French", stored.TargetLanguage);
        Assert.Equal("Korean", stored.DisplayLanguage);
        Assert.Equal(45, stored.PreferredSessionMinutes);
        Assert.Equal("C1", stored.TargetCEFRLevel);
        Assert.Equal("a@example.invalid", stored.Email);
        Assert.Equal("sk-a-local", stored.OpenAI_APIKey);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerB), "Owner B");
    }

    [Theory]
    [InlineData(NativeProfileRemoteOutcome.Rejected, "NativeProfile_SaveRejected")]
    [InlineData(NativeProfileRemoteOutcome.TransportFailure, "Login_ConnectionError")]
    [InlineData(NativeProfileRemoteOutcome.Unavailable, "NativeProfile_SaveUnavailable")]
    public async Task RemoteFailureLeavesLocalRowAndShowsTheError(
        NativeProfileRemoteOutcome outcome, string key)
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote { Respond = (_, _) => new(outcome) };
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("Never stored"));

        Assert.False(await coordinator.SaveAsync());

        Assert.Equal(ProfileSaveStatus.Failed, coordinator.State.SaveStatus);
        Assert.Equal(key, coordinator.State.SaveMessageKey);
        Assert.Equal("Never stored", coordinator.State.Draft.Name);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerA), "Owner A");
    }

    [Fact]
    public async Task UnchangedOutOfListValuesAreKept_WhileTheNameChanges()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SetLegacyChoicesAsync(OwnerA);
        var remote = new FakeRemote();
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("Renamed"));

        Assert.True(await coordinator.SaveAsync());

        var sent = Assert.Single(remote.Requests);
        Assert.Equal(new NativeProfileFields("Renamed", "english", "Italian", "English", 25,
            "Advanced"), sent.Fields);
        var stored = await fixture.GetAsync(OwnerA);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal("english", stored.NativeLanguage);
        Assert.Equal("Italian", stored.TargetLanguage);
        Assert.Equal(25, stored.PreferredSessionMinutes);
        Assert.Equal("Advanced", stored.TargetCEFRLevel);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerB), "Owner B");
    }

    [Fact]
    public async Task ANewOutOfListValueIsRefused()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SetLegacyChoicesAsync(OwnerA);
        var remote = new FakeRemote();
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));

        // The pickers only apply listed choices; a changed value must be listed.
        Assert.False(coordinator.SetTargetLanguage("Portuguese"));
        Assert.False(coordinator.SetSessionMinutes(35));
        Assert.False(coordinator.SetTargetCefrLevel("Expert"));
        Assert.Equal("Italian", coordinator.State.Draft.TargetLanguage);

        var stored = coordinator.State.Draft;
        Assert.True(ProfileCoordinator.IsSavable(stored with { Name = "Renamed" }, stored));
        Assert.False(ProfileCoordinator.IsSavable(stored with { TargetLanguage = "Portuguese" }, stored));
        Assert.False(ProfileCoordinator.IsSavable(stored with { NativeLanguage = "ENGLISH" }, stored));
        Assert.False(ProfileCoordinator.IsSavable(stored with { PreferredSessionMinutes = 35 }, stored));
        Assert.False(ProfileCoordinator.IsSavable(stored with { TargetCefrLevel = "Expert" }, stored));
        Assert.True(ProfileCoordinator.IsSavable(stored with { TargetLanguage = "Korean" }, stored));
        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task RemoteExceptionIsAnHonestFailureWithoutALocalWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote
        {
            Respond = (_, _) => throw new InvalidOperationException("boom")
        };
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));

        Assert.False(await coordinator.SaveAsync());

        Assert.Equal("NativeProfile_SaveFailed", coordinator.State.SaveMessageKey);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerA), "Owner A");
    }

    [Fact]
    public async Task NoRemoteStoreReportsUnavailable()
    {
        using var fixture = await Fixture.CreateAsync();
        using var coordinator = fixture.Coordinator(remote: null);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("Not saved"));

        Assert.False(await coordinator.SaveAsync());

        Assert.Equal("NativeProfile_SaveUnavailable", coordinator.State.SaveMessageKey);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerA), "Owner A");
    }

    [Fact]
    public async Task OwnerChangeDuringSaveDiscardsTheResultWithoutALocalWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote { Hold = true };
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("Owner A edit"));

        var save = coordinator.SaveAsync();
        await remote.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.State.IsSaving);
        fixture.Scope.SetActiveUser(OwnerB);
        remote.Release();

        Assert.False(await save.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ProfilePhase.Blocked, coordinator.State.Phase);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerA), "Owner A");
        fixture.AssertUntouched(await fixture.GetAsync(OwnerB), "Owner B");
    }

    [Fact]
    public async Task DoubleTapSharesOneRequest_AndEditsWaitForTheSave()
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote { Hold = true };
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("Tapped twice"));

        var first = coordinator.SaveAsync();
        var second = coordinator.SaveAsync();
        await remote.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.SetName("Typed during save"));
        Assert.False(coordinator.State.CanSave);
        remote.Release();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(remote.Requests);
        Assert.Equal("Tapped twice", (await fixture.GetAsync(OwnerA)).Name);
        Assert.True(coordinator.State.CanSave);
        Assert.True(coordinator.SetName("Editable again"));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task InvalidDraftNeverSends(string name)
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote();
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName(name));

        Assert.False(await coordinator.SaveAsync());

        Assert.Empty(remote.Requests);
        Assert.Equal("NativeProfile_SaveInvalid", coordinator.State.SaveMessageKey);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerA), "Owner A");
    }

    [Fact]
    public async Task OverlongNameNeverSends()
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote();
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName(new string('n', 201)));

        Assert.False(await coordinator.SaveAsync());

        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task AbsentOrSignedOutOwnerFailsClosed()
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote();
        using var coordinator = fixture.Coordinator(remote);

        Assert.False(await coordinator.SaveAsync());
        Assert.True(await coordinator.OpenAsync(OwnerA));
        fixture.Scope.SetActiveUser(null);
        Assert.False(await coordinator.SaveAsync());

        Assert.Empty(remote.Requests);
        fixture.AssertUntouched(await fixture.GetAsync(OwnerA), "Owner A");
    }

    [Fact]
    public async Task LocalWriteFailureAfterServerSaveIsReportedHonestly()
    {
        using var fixture = await Fixture.CreateAsync();
        var remote = new FakeRemote
        {
            // The server accepted, then the local row disappeared before the write.
            Respond = (_, fields) => new(NativeProfileRemoteOutcome.Saved, fields),
            BeforeReturn = () => fixture.DeleteAsync(OwnerA)
        };
        using var coordinator = fixture.Coordinator(remote);
        Assert.True(await coordinator.OpenAsync(OwnerA));

        Assert.False(await coordinator.SaveAsync());

        Assert.Equal(ProfileSaveStatus.Failed, coordinator.State.SaveStatus);
        Assert.Equal("NativeProfile_SaveLocalFailed", coordinator.State.SaveMessageKey);
        Assert.Null(await fixture.FindAsync(OwnerA));
    }

    private sealed class FakeRemote : INativeProfileRemoteStore
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Hold { get; init; }
        internal Func<string, NativeProfileFields, NativeProfileSaveResult> Respond { get; init; } =
            (_, fields) => new(NativeProfileRemoteOutcome.Saved, fields);
        internal Func<Task>? BeforeReturn { get; init; }
        internal List<(string Owner, NativeProfileFields Fields)> Requests { get; } = [];
        internal Task Started => _started.Task;
        internal void Release() => _release.TrySetResult();

        public Task<NativeProfileRemoteOutcome> SaveOnboardingAsync(
            string owner, NativeOnboardingDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<NativeProfileSaveResult> SaveProfileAsync(
            string owner, NativeProfileFields fields, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add((owner, fields));
            _started.TrySetResult();
            if (Hold)
                await _release.Task.WaitAsync(cancellationToken);
            var result = Respond(owner, fields);
            if (BeforeReturn is not null)
                await BeforeReturn();
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _services;
        internal DeviceUserScopeProvider Scope { get; } = new();

        private Fixture()
        {
            _connection.Open();
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
            DeviceInterpreterQueryGuard.Register(services);
            services.AddSingleton(Scope);
            services.AddSingleton<ILogger<UserProfileRepository>>(
                NullLogger<UserProfileRepository>.Instance);
            services.AddSingleton<UserProfileRepository>();
            _services = services.BuildServiceProvider();
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            using var scope = fixture._services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.UserProfiles.AddRange(Seed(OwnerA, "Owner A", "a@example.invalid", "sk-a-local"),
                Seed(OwnerB, "Owner B", "b@example.invalid", "sk-b-local"));
            await db.SaveChangesAsync();
            fixture.Scope.SetActiveUser(OwnerA);
            return fixture;
        }

        internal ProfileCoordinator Coordinator(INativeProfileRemoteStore? remote) =>
            new(_services.GetRequiredService<UserProfileRepository>(), Scope,
                NullLogger<ProfileCoordinator>.Instance, remote);

        internal async Task<UserProfile> GetAsync(string id) =>
            await FindAsync(id) ?? throw new InvalidOperationException($"No row {id}.");

        internal async Task<UserProfile?> FindAsync(string id)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
        }

        /// <summary>Gives a row values the shared pickers do not list (an older profile).</summary>
        internal async Task SetLegacyChoicesAsync(string id)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.UserProfiles.SingleAsync(p => p.Id == id);
            profile.NativeLanguage = "english";
            profile.TargetLanguage = "Italian";
            profile.PreferredSessionMinutes = 25;
            profile.TargetCEFRLevel = "Advanced";
            await db.SaveChangesAsync();
        }

        internal async Task DeleteAsync(string id)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.UserProfiles.Where(p => p.Id == id).ExecuteDeleteAsync();
        }

        internal void AssertUntouched(UserProfile stored, string name)
        {
            Assert.Equal(name, stored.Name);
            Assert.Equal("English", stored.NativeLanguage);
            Assert.Equal("Korean", stored.TargetLanguage);
            Assert.Equal("English", stored.DisplayLanguage);
            Assert.Equal(20, stored.PreferredSessionMinutes);
            Assert.Equal("B1", stored.TargetCEFRLevel);
        }

        private static UserProfile Seed(string id, string name, string email, string key) => new()
        {
            Id = id, Name = name, Email = email, NativeLanguage = "English",
            TargetLanguage = "Korean", DisplayLanguage = "English", PreferredSessionMinutes = 20,
            TargetCEFRLevel = "B1", OpenAI_APIKey = key, CreatedAt = DateTime.UtcNow
        };

        public void Dispose()
        {
            _services.Dispose();
            _connection.Dispose();
        }
    }
}
