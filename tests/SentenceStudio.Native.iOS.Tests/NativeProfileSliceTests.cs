using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Profile;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeProfileSliceTests
{
    private const string OwnerA = "isolated-profile-a";
    private const string OwnerB = "isolated-profile-b";

    [Fact]
    public async Task OwnedReadDraftCancelAndReopenNeverModifyRealSqliteRows()
    {
        using var fixture = new InMemoryProfiles();
        await fixture.AddAsync(
            new UserProfile {
                Id = OwnerA, Name = "Original", Email = "a@example.invalid",
                NativeLanguage = "English", TargetLanguage = "Korean",
                DisplayLanguage = "Korean", PreferredSessionMinutes = 20,
                TargetCEFRLevel = "B1"
            },
            new UserProfile {
                Id = OwnerB, Name = "Other account", Email = "b@example.invalid",
                PreferredSessionMinutes = 30
            });
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();

        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.Equal(ProfilePhase.Ready, coordinator.State.Phase);
        Assert.Equal("Original", coordinator.State.Draft.Name);
        Assert.Equal("a@example.invalid", coordinator.State.Draft.Email);
        Assert.Equal("Korean", coordinator.State.Draft.DisplayLanguage);
        Assert.Equal(20, coordinator.State.Draft.PreferredSessionMinutes);
        Assert.Equal("B1", coordinator.State.Draft.TargetCefrLevel);

        Assert.True(coordinator.SetName("Discarded name"));
        Assert.True(coordinator.SetSessionMinutes(45));
        Assert.True(coordinator.SetNativeLanguage("Japanese"));
        Assert.True(coordinator.SetTargetLanguage("French"));
        Assert.True(coordinator.SetDisplayLanguage("English"));
        Assert.True(coordinator.SetTargetCefrLevel("C2"));
        Assert.False(coordinator.SetSessionMinutes(99));
        Assert.False(coordinator.SetNativeLanguage("not a source language"));
        coordinator.RequestDelete();
        Assert.True(coordinator.State.ShowDeleteConfirmation);
        coordinator.CancelDelete();
        Assert.False(coordinator.State.ShowDeleteConfirmation);
        coordinator.Close();
        Assert.Equal(ProfilePhase.Closed, coordinator.State.Phase);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);

        var first = await fixture.GetAsync(OwnerA);
        var other = await fixture.GetAsync(OwnerB);
        Assert.Equal("Original", first!.Name);
        Assert.Equal("English", first.NativeLanguage);
        Assert.Equal("Korean", first.TargetLanguage);
        Assert.Equal("Korean", first.DisplayLanguage);
        Assert.Equal(20, first.PreferredSessionMinutes);
        Assert.Equal("Other account", other!.Name);
        Assert.Equal(30, other.PreferredSessionMinutes);

        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.Equal("Original", coordinator.State.Draft.Name);
        Assert.Equal(20, coordinator.State.Draft.PreferredSessionMinutes);
    }

    [Fact]
    public async Task AbsentForeignAndSwitchedOwnersFailClosedWithoutExposingDraft()
    {
        using var fixture = new InMemoryProfiles();
        await fixture.AddAsync(
            new UserProfile { Id = OwnerA, Name = "Private A" },
            new UserProfile { Id = OwnerB, Name = "Private B" });
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();

        Assert.False(await coordinator.OpenAsync(null));
        Assert.False(await coordinator.OpenAsync(OwnerB));
        Assert.False(coordinator.IsOpen);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.Equal("Private A", coordinator.State.Draft.Name);

        fixture.Scope.SetActiveUser(OwnerB);
        Assert.Equal(ProfilePhase.Blocked, coordinator.State.Phase);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        Assert.False(coordinator.SetName("Cross-owner draft"));
        coordinator.RequestDelete();
        Assert.False(coordinator.State.ShowDeleteConfirmation);
        coordinator.Close();
        fixture.Scope.SetActiveUser(null);
        Assert.False(await coordinator.OpenAsync(OwnerA));
        Assert.Equal("Private A", (await fixture.GetAsync(OwnerA))!.Name);
        Assert.Equal("Private B", (await fixture.GetAsync(OwnerB))!.Name);
    }

    [Theory]
    [InlineData(OwnerB)]
    [InlineData(null)]
    public async Task RevokingOwnerClearsMountedProfileWithoutWaitingForAnAction(
        string? nextOwner)
    {
        using var fixture = new InMemoryProfiles();
        await fixture.AddAsync(
            new UserProfile {
                Id = OwnerA, Name = "Private A", Email = "a@example.invalid"
            },
            new UserProfile {
                Id = OwnerB, Name = "Private B", Email = "b@example.invalid"
            });
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("Unsaved A"));
        coordinator.RequestDelete();
        Assert.True(coordinator.State.ShowDeleteConfirmation);

        var beforeVersion = coordinator.ViewVersion;
        var notifications = new List<(ProfileState State, int Version)>();
        coordinator.Changed += () =>
            notifications.Add((coordinator.State, coordinator.ViewVersion));

        fixture.Scope.SetActiveUser(nextOwner);

        Assert.Single(notifications);
        Assert.Equal(ProfilePhase.Blocked, notifications[0].State.Phase);
        Assert.Equal(ProfileDraft.Empty, notifications[0].State.Draft);
        Assert.False(notifications[0].State.ShowDeleteConfirmation);
        Assert.True(notifications[0].Version > beforeVersion);
        Assert.Equal(notifications[0].Version, coordinator.ViewVersion);
        Assert.True(coordinator.IsOpen);
        Assert.Equal("Private A", (await fixture.GetAsync(OwnerA))!.Name);
        Assert.Equal("Private B", (await fixture.GetAsync(OwnerB))!.Name);
    }

    [Fact]
    public async Task ClosedOrDisposedProfileDoesNotReceiveLaterScopeNotifications()
    {
        using var fixture = new InMemoryProfiles();
        await fixture.AddAsync(
            new UserProfile { Id = OwnerA, Name = "Private A" },
            new UserProfile { Id = OwnerB, Name = "Private B" });
        fixture.Scope.SetActiveUser(OwnerA);
        var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        var notifications = 0;
        coordinator.Changed += () => notifications++;

        coordinator.Close();
        var closedVersion = coordinator.ViewVersion;
        fixture.Scope.SetActiveUser(OwnerB);
        Assert.Equal(0, notifications);
        Assert.Equal(closedVersion, coordinator.ViewVersion);
        Assert.Equal(ProfilePhase.Closed, coordinator.State.Phase);

        Assert.True(await coordinator.OpenAsync(OwnerB));
        Assert.Equal("Private B", coordinator.State.Draft.Name);
        notifications = 0;
        coordinator.Dispose();
        var disposedVersion = coordinator.ViewVersion;
        fixture.Scope.SetActiveUser(null);
        Assert.Equal(0, notifications);
        Assert.Equal(disposedVersion, coordinator.ViewVersion);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
    }

    [Fact]
    public async Task AuthenticationTransitionAndSameOwnerRepublishRevokeOldDraft()
    {
        using var fixture = new InMemoryProfiles();
        await fixture.AddAsync(new UserProfile {
            Id = OwnerA, Name = "Private A"
        });
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetName("Unsaved A"));

        var beforeVersion = coordinator.ViewVersion;
        fixture.Scope.SetActiveUser(OwnerA);
        Assert.Equal(ProfilePhase.Blocked, coordinator.State.Phase);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        Assert.True(coordinator.ViewVersion > beforeVersion);

        coordinator.Close();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.SetEmail("unsaved@example.invalid"));
        var published = fixture.Scope.CaptureStamp();
        beforeVersion = coordinator.ViewVersion;

        Assert.True(fixture.Scope.TryBeginAuthentication(published, out var transition));
        Assert.Equal(ProfilePhase.Blocked, coordinator.State.Phase);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        Assert.True(coordinator.ViewVersion > beforeVersion);

        Assert.True(fixture.Scope.TryPublishAuthenticatedUser(
            OwnerA, transition, CancellationToken.None, out var nextPublished));
        coordinator.Close();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.True(fixture.Scope.TryClearIfCurrent(nextPublished));
        Assert.Equal(ProfilePhase.Blocked, coordinator.State.Phase);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        Assert.Equal("Private A", (await fixture.GetAsync(OwnerA))!.Name);
    }

    [Theory]
    [InlineData(OwnerB)]
    [InlineData(null)]
    public async Task RevokedInFlightReadCannotRestoreOldOwnersProfile(
        string? nextOwner)
    {
        var hold = new HoldProfileRead();
        using var fixture = new InMemoryProfiles(hold);
        await fixture.AddAsync(
            new UserProfile {
                Id = OwnerA, Name = "Private A", Email = "a@example.invalid"
            },
            new UserProfile {
                Id = OwnerB, Name = "Private B", Email = "b@example.invalid"
            });
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));

        hold.Arm();
        var pendingRead = coordinator.ReloadAsync();
        try
        {
            await hold.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var beforeVersion = coordinator.ViewVersion;
            var notifications = new List<ProfileState>();
            coordinator.Changed += () => notifications.Add(coordinator.State);

            fixture.Scope.SetActiveUser(nextOwner);

            Assert.Single(notifications);
            Assert.Equal(ProfilePhase.Blocked, notifications[0].Phase);
            Assert.Equal(ProfileDraft.Empty, notifications[0].Draft);
            Assert.True(coordinator.ViewVersion > beforeVersion);
            hold.Release();
            await pendingRead;
            Assert.Single(notifications);
            Assert.Equal(ProfilePhase.Blocked, coordinator.State.Phase);
            Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
            Assert.Equal("Private A", (await fixture.GetAsync(OwnerA))!.Name);
            Assert.Equal("Private B", (await fixture.GetAsync(OwnerB))!.Name);
        }
        finally
        {
            hold.Release();
        }
    }

    [Fact]
    public async Task MissingOwnedRowNeverFallsBackToAnotherAccountOrCreatesOne()
    {
        using var fixture = new InMemoryProfiles();
        await fixture.AddAsync(new UserProfile {
            Id = OwnerB, Name = "Only B"
        });
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();

        Assert.False(await coordinator.OpenAsync(OwnerA));
        Assert.True(coordinator.IsOpen);
        Assert.Equal(ProfilePhase.Missing, coordinator.State.Phase);
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        Assert.False(coordinator.SetName("Never create A"));
        await coordinator.ReloadAsync();
        Assert.Equal(ProfilePhase.Missing, coordinator.State.Phase);
        Assert.Null(await fixture.GetAsync(OwnerA));
        Assert.Equal("Only B", (await fixture.GetAsync(OwnerB))!.Name);
    }

    [Fact]
    public async Task SparseOwnedRowUsesSourceProfileFallbacksWithoutWritingThem()
    {
        using var fixture = new InMemoryProfiles();
        await fixture.AddAsync(new UserProfile {
            Id = OwnerA, Name = null, Email = null,
            NativeLanguage = "", TargetLanguage = "",
            DisplayLanguage = "", PreferredSessionMinutes = 0,
            TargetCEFRLevel = null
        });
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();

        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.Equal(ProfileDraft.Empty, coordinator.State.Draft);
        coordinator.Close();
        var stored = await fixture.GetAsync(OwnerA);
        Assert.Equal("", stored!.NativeLanguage);
        Assert.Equal(0, stored.PreferredSessionMinutes);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void ProfileControlsAndBlockedActionsUseLocalizedCopy(string resourceFile)
    {
        var resources = XDocument.Load(Path.Combine(Head,
            "../SentenceStudio.Shared/Resources/Strings", resourceFile))
            .Descendants("data").ToArray();
        foreach (var key in new[]
        {
            "Nav_Profile", "Profile_PageTitle",
            "Profile_PersonalInformation", "Profile_Name", "Profile_Email",
            "Profile_LanguageSettings", "Profile_NativeLanguage",
            "Profile_TargetLanguage", "Profile_DisplayLanguage",
            "Profile_LearningPreferences", "Profile_SessionDuration",
            "Profile_TargetCEFRLevel", "Profile_SaveProfile",
            "Profile_ChangePassword", "Profile_CurrentPassword",
            "Profile_NewPassword", "Profile_ConfirmNewPassword",
            "Profile_DangerZone", "Profile_DeleteMyAccount",
            "Profile_ConfirmDelete", "Profile_DeleteConfirmationMessage",
            "Profile_Cancel", "Profile_Delete",
            "NativeProfile_OwnerUnavailable", "NativeProfile_NotFound",
            "NativeProfile_SaveUnavailable",
            "NativeProfile_AccountActionUnavailable"
        })
            Assert.True(resources.Any(entry =>
                entry.Attribute("name")?.Value == key
                && !string.IsNullOrWhiteSpace(entry.Element("value")?.Value)),
                $"{resourceFile} lacks {key}");
    }

    [Fact]
    public void SwiftUIProfileHasOwnedRouteSourceControlsAndNoWriteOrExport()
    {
        var view = Source("Features/Profile/ProfileView.cs");
        var scene = Source("App/ApplicationScene.Profile.cs");
        var app = Source("App/ApplicationController.Profile.cs");
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        foreach (var control in new[]
        {
            "native.profile.name", "native.profile.email",
            "native.profile.language.native", "native.profile.language.target",
            "native.profile.language.display", "native.profile.minutes.",
            "native.profile.level.", "native.profile.save",
            "native.profile.password.current", "native.profile.password.new",
            "native.profile.password.confirm", "native.profile.delete.request"
        })
            Assert.Contains(control, view);
        Assert.Contains("NativeOnboardingOptions.NativeLanguages", view);
        Assert.Contains("NativeOnboardingOptions.SessionMinutes", view);
        Assert.Contains("NativeOnboardingOptions.CefrLevels", view);
        Assert.Contains("Views.Scroll(content)", view);
        Assert.Contains("enabled: false", view);
        Assert.Contains("delete.IsEnabled = false;", scene);
        Assert.Contains("Views.Alert(_profileSlot,", scene);
        Assert.Contains("_dashboardStack.Push(\"profile\",", scene);
        Assert.Contains("_renderedProfileVersion != _app.ProfileViewVersion", scene);
        Assert.Contains("Replace(_profileSlot, _app.RenderProfile(_style))", scene);
        Assert.Contains("Profile_PageTitle", scene);
        Assert.Contains("NativePreviewFixture.FromAssembly()?.Owner", app);
        Assert.Contains("scope.TryGetUserProfileId(out var activeOwner)", app);
        Assert.Contains("GetByIdAsync(owner, cancellationToken)",
            Source("Features/Profile/ProfileCoordinator.cs"));
        Assert.Contains("native.dashboard.profile", dashboard);
        Assert.DoesNotContain("ExportAllData", view);
        Assert.DoesNotContain("SaveAsync(", view + app);
        Assert.DoesNotContain("DeleteAccountAsync(", view + app);
        Assert.DoesNotContain("ChangePasswordAsync(", view + app);
    }

    private static string Head => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!,
        "../../src/SentenceStudio.Native.iOS"));

    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(Head, name));

    private static string CurrentFile([CallerFilePath] string file = "") => file;

    private sealed class InMemoryProfiles : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _services;
        internal DeviceUserScopeProvider Scope { get; } = new();

        internal InMemoryProfiles(DbCommandInterceptor? interceptor = null)
        {
            _connection.Open();
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options => {
                options.UseSqlite(_connection);
                if (interceptor is not null)
                    options.AddInterceptors(interceptor);
            });
            services.AddSingleton(Scope);
            services.AddSingleton<ILogger<UserProfileRepository>>(
                NullLogger<UserProfileRepository>.Instance);
            services.AddSingleton<UserProfileRepository>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Database.EnsureCreated();
        }

        internal ProfileCoordinator Coordinator() =>
            new(_services.GetRequiredService<UserProfileRepository>(), Scope,
                NullLogger<ProfileCoordinator>.Instance);

        internal async Task AddAsync(params UserProfile[] profiles)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.UserProfiles.AddRange(profiles);
            await db.SaveChangesAsync();
        }

        internal async Task<UserProfile?> GetAsync(string id)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.UserProfiles.AsNoTracking()
                .SingleOrDefaultAsync(profile => profile.Id == id);
        }

        public void Dispose()
        {
            _services.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class HoldProfileRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        internal Task ReadStarted => _readStarted.Task;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UserProfile", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _readStarted.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
