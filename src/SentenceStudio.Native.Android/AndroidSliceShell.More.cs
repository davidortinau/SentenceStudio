using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.ActivityLog;
using SentenceStudio.Native.Features.Profile;
using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Native.Features.Skills;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Services;
using SentenceStudio.Services.Theme;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The Dashboard More menu destinations (source NavMenu: Profile, Settings, Skills,
/// Vocabulary, Activity Log), matching the iOS More menu. Each screen covers the
/// Dashboard tab like a pushed route (tabs hidden, Back returns) and uses the
/// shared owner-gated coordinator iOS uses; an owner change or sign-out closes it.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private enum MoreScreen { None, Settings, Profile, Skills, Vocabulary, ActivityLog }

    private MoreScreen _more;
    private string? _moreMenu;
    private SettingsCoordinator? _settings;
    private static readonly TimeSpan TextSizeSettleDelay = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _textSizeSettle;
    private Task _appearanceSaves = Task.CompletedTask;
    private int _appearanceRequest;
    private ProfileCoordinator? _profile;
    private SkillsCoordinator? _skills;
    private VocabularyBrowser? _vocabulary;
    private ActivityLogCoordinator? _activityLog;

    private bool IsMoreScreenOpen => _more != MoreScreen.None;

    private ComposableNode RenderMoreScreen(AndroidVisualPalette palette) => _more switch
    {
        MoreScreen.Settings => RenderSettings(palette),
        MoreScreen.Profile => RenderProfile(palette),
        MoreScreen.Skills => RenderSkills(palette),
        MoreScreen.Vocabulary => RenderVocabulary(palette),
        MoreScreen.ActivityLog => RenderActivityLog(palette),
        _ => RenderDashboard(palette),
    };

    /// <summary>The verified owner for a More screen; null fails closed.</summary>
    private string? MoreOwner()
    {
        if (_scope is null)
            return null;
        string? owner = null;
#if DEBUG
        if (_preview)
            owner = DebugPreviewFixture.OwnerId;
#endif
        if (!_preview && Auth is {
                Phase: NativePhase.Ready,
                Destination: NativeDestination.Dashboard,
                UserId: { Length: > 0 } authenticated })
            owner = authenticated;
        return HasDashboardOwner(owner) ? owner : null;
    }

    private void OpenSettings() => OpenMoreScreen(MoreScreen.Settings);
    private void OpenProfile() => OpenMoreScreen(MoreScreen.Profile);
    private void OpenSkills() => OpenMoreScreen(MoreScreen.Skills);
    private void OpenVocabulary() => OpenMoreScreen(MoreScreen.Vocabulary);
    private void OpenActivityLog() => OpenMoreScreen(MoreScreen.ActivityLog);

    private void OpenMoreScreen(MoreScreen screen) => Publish(() =>
    {
        if (Destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan)
            || Busy.Value || IsMoreScreenOpen || _scope is null)
            return;
        _dashboardMenuOpen = false;
        _moreMenu = null;
        Error.Value = string.Empty;
        var owner = MoreOwner();
        if (owner is null)
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "A More screen refused an absent or untrusted owner.");
        _more = screen;
        var provider = _scope.ServiceProvider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        switch (screen)
        {
            case MoreScreen.Settings:
                OpenSettingsCoordinator(provider, scope, owner);
                OpenSamSettings();
                break;
            case MoreScreen.Profile when owner is not null:
                var profile = new ProfileCoordinator(
                    provider.GetRequiredService<UserProfileRepository>(), scope,
                    provider.GetRequiredService<ILogger<ProfileCoordinator>>(),
                    provider.GetRequiredService<INativeProfileRemoteStore>());
                profile.Changed += OnMoreChanged;
                _profile = profile;
                RunMore(() => profile.OpenAsync(owner, _lifetime.Token));
                break;
            case MoreScreen.Skills when owner is not null:
                var skills = new SkillsCoordinator(
                    provider.GetRequiredService<SkillProfileRepository>(), scope,
                    provider.GetRequiredService<ILogger<SkillsCoordinator>>());
                skills.Changed += OnMoreChanged;
                _skills = skills;
                // Not thread-safe by design (iOS main thread): runs on the UI thread.
                RunMoreOnUi(() => skills.OpenAsync(owner));
                break;
            case MoreScreen.Vocabulary when owner is not null:
                var vocabulary = new VocabularyBrowser(
                    provider.GetRequiredService<INativeResourceStore>(), scope,
                    provider.GetRequiredService<IPreferencesService>(),
                    provider.GetRequiredService<INativeQuizMedia>(),
                    provider.GetRequiredService<ILogger<VocabularyBrowser>>(),
                    Localize);
                vocabulary.Changed += OnMoreChanged;
                _vocabulary = vocabulary;
                RunMoreOnUi(() => vocabulary.OpenAsync(owner));
                break;
            case MoreScreen.ActivityLog when owner is not null:
                var log = new ActivityLogCoordinator(
                    new ActivityLogReader(
                        provider.GetRequiredService<IServiceScopeFactory>(),
                        provider.GetRequiredService<ILogger<ActivityLogReader>>()),
                    scope, provider.GetRequiredService<ILogger<ActivityLogCoordinator>>());
                log.Changed += OnMoreChanged;
                _activityLog = log;
                RunMore(() => log.OpenAsync(owner, _lifetime.Token));
                break;
        }
    });

    /// <summary>Back from a More screen, innermost layer first (menu, dialog, form, sheet).</summary>
    private bool HandleMoreBack()
    {
        if (!IsMoreScreenOpen)
            return false;
        if (_moreMenu is not null)
        {
            Publish(() => _moreMenu = null);
            return true;
        }
        switch (_more)
        {
            case MoreScreen.Profile when _profile?.State.ShowDeleteConfirmation == true:
                _profile.CancelDelete();
                return true;
            case MoreScreen.Skills when _skills is { } skills:
                if (skills.IsPending)
                    return true;
                if (skills.IsDeleteConfirmationPresented)
                {
                    skills.CancelDelete();
                    return true;
                }
                if (skills.IsFormOpen)
                {
                    skills.CancelForm();
                    return true;
                }
                break;
            case MoreScreen.Vocabulary when _vocabulary is { } vocabulary:
                if (vocabulary.IsNavigationBlocked)
                    return true;
                if (vocabulary.IsPhoneFilterPresented)
                {
                    vocabulary.CancelPhoneFilters();
                    return true;
                }
                if (vocabulary.IsFormOpen)
                {
                    vocabulary.CloseWord();
                    return true;
                }
                break;
        }
        Publish(CloseMoreScreen);
        return true;
    }

    private void CloseMoreScreen()
    {
        if (_skills?.IsPending == true || _vocabulary?.IsNavigationBlocked == true)
            return;
        _more = MoreScreen.None;
        _moreMenu = null;
        ReleaseMoreCoordinators();
    }

    /// <summary>Owner change, sign-out or disposal drops every More screen and its drafts.</summary>
    private void ResetMoreScreens()
    {
        _more = MoreScreen.None;
        _moreMenu = null;
        ReleaseMoreCoordinators();
    }

    private void ReleaseMoreCoordinators()
    {
        if (_settings is { } settings)
        {
            settings.Changed -= OnMoreChanged;
            // A text size chosen just before closing is still saved (owner-gated).
            var saves = FlushAppearanceSave(settings);
            if (saves.IsCompleted)
                settings.Dispose();
            else
                _ = saves.ContinueWith(_ => settings.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        if (_profile is { } profile)
        {
            profile.Changed -= OnMoreChanged;
            profile.Dispose();
        }
        if (_skills is { } skills)
        {
            skills.Changed -= OnMoreChanged;
            skills.Close();
            skills.Dispose();
        }
        if (_vocabulary is { } vocabulary)
        {
            vocabulary.Changed -= OnMoreChanged;
            vocabulary.Dispose();
        }
        if (_activityLog is { } log)
        {
            log.Changed -= OnMoreChanged;
            log.Dispose();
        }
        _settings = null;
        _profile = null;
        _skills = null;
        _vocabulary = null;
        _activityLog = null;
    }

    // Coordinators raise Changed on whichever thread finished the work.
    private void OnMoreChanged() => Publish(() => { });

    /// <summary>Re-renders now for a synchronous edit (text input keeps its IME composition).</summary>
    private void RenderMoreNow()
    {
        if (global::Android.OS.Looper.MyLooper() == global::Android.OS.Looper.MainLooper)
        {
            if (!_disposed)
                ApplyPublishedState();
        }
        else
            Publish(() => { });
    }

    private void ToggleMoreMenu(string key) => Publish(() =>
        _moreMenu = _moreMenu == key ? null : key);

    private void CloseMoreMenu() => Publish(() => _moreMenu = null);

    /// <summary>Thread-safe coordinators (locked state) read off the UI thread.</summary>
    private void RunMore(Func<Task> action) => _ = Task.Run(() => GuardMoreAsync(action));

    /// <summary>UI-thread coordinators (Skills, Vocabulary) start on the UI thread.</summary>
    private void RunMoreOnUi(Func<Task> action) => Post(() => _ = GuardMoreAsync(action));

    private async Task GuardMoreAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }

    // ------------------------------------------------------------ Settings

    private void OpenSettingsCoordinator(
        IServiceProvider provider, DeviceUserScopeProvider scope, string? owner)
    {
        if (owner is null)
            return;
        var settings = new SettingsCoordinator(scope,
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<NativeQuizSettings>(),
            provider.GetRequiredService<ILogger<SettingsCoordinator>>(),
            allowVoiceDiscovery: !_preview);
        settings.Changed += OnMoreChanged;
        if (!settings.Open(owner))
        {
            settings.Changed -= OnMoreChanged;
            settings.Dispose();
            return;
        }
        _settings = settings;
        if (settings.State.Quiz is { } draft)
            _ = LoadSettingsVoicesAsync(settings, owner, draft.VoiceLanguage);
    }

    private async Task LoadSettingsVoicesAsync(
        SettingsCoordinator settings, string owner, string language)
    {
        try
        {
            await settings.LoadVoicesAsync(owner, language, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // Settings closed while voices loaded.
        }
    }

    private void SelectSettingsVoiceLanguage(string language)
    {
        CloseMoreMenu();
        if (_settings is { } settings
            && settings.SelectVoiceLanguage(language) is { } owner)
            _ = LoadSettingsVoicesAsync(settings, owner, language);
    }

    private void EditQuizSettings(Func<QuizSettingsDraft, QuizSettingsDraft> change)
    {
        if (_settings?.Edit(change) == true)
            RenderMoreNow();
    }

    private void SaveQuizSettings() => _settings?.SaveQuiz();

    /// <summary>A theme or mode tap: shown now, saved now off the UI thread.</summary>
    private void SetAppearance(AppearanceSelection selection) =>
        ChangeAppearance(selection, settle: false);

    /// <summary>
    /// A text-size slider step: applied live, saved once the drag settles. The
    /// Compose facade's Slider passes no onValueChangeFinished, so a step that no
    /// other step follows within <see cref="TextSizeSettleDelay"/> ends the drag.
    /// </summary>
    private void PreviewTextSize(double fontScale) =>
        ChangeAppearance(CurrentAppearance.WithFontScale(fontScale), settle: true);

    // UI-thread state: each change supersedes any pending text-size save, and
    // saves run on the thread pool one at a time in request order.
    private void ChangeAppearance(AppearanceSelection selection, bool settle)
    {
        if (_settings is not { } settings || selection == CurrentAppearance)
            return;
        _appearance = selection;
        AndroidFontScale.Apply(activity, selection.FontScale);
        RenderMoreNow();
        CancelTextSizeSettle();
        var request = ++_appearanceRequest;
        if (!settle)
        {
            QueueAppearanceSave(settings, selection, request);
            return;
        }
        var delay = new CancellationTokenSource();
        _textSizeSettle = delay;
        _ = SaveTextSizeWhenSettledAsync(settings, selection, request, delay.Token);
    }

    private async Task SaveTextSizeWhenSettledAsync(SettingsCoordinator settings,
        AppearanceSelection selection, int request, CancellationToken settle)
    {
        try
        {
            await Task.Delay(TextSizeSettleDelay, settle).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // A newer change, or closing Settings, took over this save.
        }
        Post(() =>
        {
            if (request != _appearanceRequest || settle.IsCancellationRequested)
                return;
            CancelTextSizeSettle();
            QueueAppearanceSave(settings, selection, request);
        });
    }

    private void CancelTextSizeSettle()
    {
        if (_textSizeSettle is not { } settle)
            return;
        _textSizeSettle = null;
        settle.Cancel();
        settle.Dispose();
    }

    private void QueueAppearanceSave(
        SettingsCoordinator settings, AppearanceSelection selection, int request)
    {
        var previous = _appearanceSaves;
        var save = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            // Owner-gated; on failure it shows NativeText.SaveFailed and returns null.
            return settings.SaveAppearance(selection, SaveDeviceAppearance);
        });
        // A failed save never stops the saves queued after it.
        _appearanceSaves = save.ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = ReportAppearanceSaveAsync(save, request);
    }

    private async Task ReportAppearanceSaveAsync(
        Task<AppearanceSelection?> save, int request)
    {
        AppearanceSelection? saved;
        try
        {
            saved = await save.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
            saved = null;
        }
        if (saved is not null)
            return;
        Post(() =>
        {
            if (request != _appearanceRequest)
                return; // A newer change decides what is shown.
            // Not saved: show the stored appearance again instead of an unsaved one.
            _appearance = null;
            AndroidFontScale.Apply(activity, CurrentAppearance.FontScale);
            RenderMoreNow();
        });
    }

    /// <summary>Closing Settings saves a pending text size now; returns the save queue.</summary>
    private Task FlushAppearanceSave(SettingsCoordinator settings)
    {
        if (_textSizeSettle is not null)
        {
            CancelTextSizeSettle();
            QueueAppearanceSave(settings, CurrentAppearance, _appearanceRequest);
        }
        return _appearanceSaves;
    }

    /// <summary>The same AppAppearance token the source device store and iOS write.</summary>
    private static AppearanceSelection SaveDeviceAppearance(
        IPreferencesService preferences, AppearanceSelection selection)
    {
        preferences.Set(DevicePreferencesThemeStore.AppearanceKey, selection.ToToken());
        return selection;
    }

    /// <summary>Source text size applies app-wide at launch and after any configuration change.</summary>
    private void ApplyAppearanceFontScale() =>
        activity.Window?.DecorView?.Post(() =>
        {
            if (!_disposed)
                AndroidFontScale.Apply(activity, CurrentAppearance.FontScale);
        });
}
