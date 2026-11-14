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
/// The pages pushed above a tab root (source NavMenu destinations), as in the iOS tab
/// shell: History (Activity Log) above Study; Vocabulary, Skills, Import Content and
/// YouTube import above the Library list; Profile above Settings. Each tab keeps its own
/// page while another tab is shown (<see cref="MorePage"/>), Back returns to the tab
/// root, and each page uses the shared owner-gated coordinator iOS uses; an owner change
/// or sign-out closes them all. Settings itself is the Settings tab root.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private enum MoreScreen { None, Profile, Skills, Vocabulary, ActivityLog, ImportContent, YouTubeImport }

    private string? _moreMenu;
    private SettingsCoordinator? _settings;
    // The size a text-size drag shows until release applies it; null when none.
    private double? _textSizePreview;
    private Task _appearanceSaves = Task.CompletedTask;
    private int _appearanceRequest;
    private ProfileCoordinator? _profile;
    private SkillsCoordinator? _skills;
    private VocabularyBrowser? _vocabulary;
    private ActivityLogCoordinator? _activityLog;

    private bool IsMoreScreenOpen => MorePage != MoreScreen.None;

    private ComposableNode RenderMoreScreen(AndroidVisualPalette palette) => MorePage switch
    {
        MoreScreen.Profile => RenderProfile(palette),
        MoreScreen.Skills => RenderSkills(palette),
        MoreScreen.Vocabulary => RenderVocabulary(palette),
        MoreScreen.ActivityLog => RenderActivityLog(palette),
        MoreScreen.ImportContent => RenderImportContent(palette),
        MoreScreen.YouTubeImport => RenderYouTubeImport(palette),
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

    private void OpenProfile() => OpenMoreScreen(MoreScreen.Profile);
    private void OpenSkills(bool create = false) => OpenMoreScreen(MoreScreen.Skills, create);
    private void OpenVocabulary() => OpenMoreScreen(MoreScreen.Vocabulary);
    private void OpenActivityLog() => OpenMoreScreen(MoreScreen.ActivityLog);

    /// <summary>Opens a page above the current tab root; <paramref name="create"/> opens Skills on its create form.</summary>
    private void OpenMoreScreen(MoreScreen screen, bool create = false) => Publish(() =>
    {
        if (Destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan)
            || Busy.Value || IsMoreScreenOpen || _scope is null)
            return;
        _moreMenu = null;
        Error.Value = string.Empty;
        var owner = MoreOwner();
        if (owner is null)
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "A More screen refused an absent or untrusted owner.");
        MorePage = screen;
        var provider = _scope.ServiceProvider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        switch (screen)
        {
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
                RunMoreOnUi(async () =>
                {
                    // Library quick add: the create form opens above the list once it is read.
                    if (await skills.OpenAsync(owner) && create && ReferenceEquals(_skills, skills))
                        skills.OpenCreate();
                });
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
            case MoreScreen.ImportContent:
                OpenImportContentCoordinator(provider, scope, owner);
                break;
            case MoreScreen.YouTubeImport:
                OpenYouTubeImportCoordinator(provider, scope, owner);
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
        switch (MorePage)
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
                if (HandleRecentLookupsBack())
                    return true;
                if (vocabulary.IsNavigationBlocked)
                    return true;
                if (vocabulary.IsDeleteConfirmationPresented)
                {
                    vocabulary.CancelDelete();
                    return true;
                }
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
                // The duplicate page sits under a word its Edit opened, so it closes after the word.
                if (vocabulary.DuplicatePanel is not null)
                {
                    vocabulary.CloseDuplicates();
                    return true;
                }
                break;
            case MoreScreen.ImportContent when HandleImportContentBack():
                return true;
            case MoreScreen.YouTubeImport when HandleYouTubeBack():
                return true;
        }
        Publish(CloseMoreScreen);
        return true;
    }

    /// <summary>Back or the page's own close: the current tab's page closes unless it is saving.</summary>
    private void CloseMoreScreen()
    {
        var page = MorePage;
        if (page == MoreScreen.None || IsMorePageBlocked(page))
            return;
        MorePage = MoreScreen.None;
        _moreMenu = null;
        ReleaseMorePage(page);
        // The profile row shows a renamed learner or new languages at once.
        if (page == MoreScreen.Profile)
            _ = RefreshProfileSummaryAsync();
    }

    /// <summary>A page that is saving cannot close (Back waits, the tab bar is disabled).</summary>
    private bool IsMorePageBlocked(MoreScreen page) => page switch
    {
        MoreScreen.Profile => _profile?.State.IsSaving == true,
        MoreScreen.Skills => _skills?.IsPending == true,
        MoreScreen.Vocabulary => _vocabulary?.IsNavigationBlocked == true,
        MoreScreen.ImportContent => _importContent?.IsNavigationBlocked == true
            || _importWords?.IsNavigationBlocked == true,
        MoreScreen.YouTubeImport => IsYouTubeNavigationBlocked,
        _ => false,
    };

    /// <summary>Owner change, sign-out or disposal drops every tab's page, Settings and their drafts.</summary>
    private void ResetMoreScreens()
    {
        Array.Clear(_tabPages);
        _moreMenu = null;
        ReleaseMoreCoordinators();
        ResetTabs();
    }

    private void ReleaseMoreCoordinators()
    {
        foreach (var page in Enum.GetValues<MoreScreen>())
            ReleaseMorePage(page);
        ReleaseSettings();
    }

    private void ReleaseMorePage(MoreScreen page)
    {
        switch (page)
        {
            case MoreScreen.Profile when _profile is { } profile:
                _profile = null;
                profile.Changed -= OnMoreChanged;
                profile.Dispose();
                break;
            case MoreScreen.Skills when _skills is { } skills:
                _skills = null;
                skills.Changed -= OnMoreChanged;
                skills.Close();
                skills.Dispose();
                break;
            case MoreScreen.Vocabulary:
                CloseRecentLookups();
                if (_vocabulary is { } vocabulary)
                {
                    _vocabulary = null;
                    vocabulary.Changed -= OnMoreChanged;
                    vocabulary.Dispose();
                }
                break;
            case MoreScreen.ActivityLog when _activityLog is { } log:
                _activityLog = null;
                log.Changed -= OnMoreChanged;
                log.Dispose();
                break;
            case MoreScreen.ImportContent:
                ReleaseImportContent();
                break;
            case MoreScreen.YouTubeImport:
                ReleaseYouTubeImport();
                break;
        }
    }

    /// <summary>The Settings tab's coordinator: kept between visits for its owner (as on iOS).</summary>
    private void ReleaseSettings()
    {
        // A text-size drag still held when Settings closes was never released: not applied.
        _textSizePreview = null;
        if (_settings is not { } settings)
            return;
        _settings = null;
        settings.Changed -= OnMoreChanged;
        // Appearance saves already queued finish (owner-gated) before disposal.
        var saves = _appearanceSaves;
        if (saves.IsCompleted)
            settings.Dispose();
        else
            _ = saves.ContinueWith(_ => settings.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

    /// <summary>A theme or mode tap or a released text size: shown now, saved off the UI thread.</summary>
    private void SetAppearance(AppearanceSelection selection)
    {
        if (_settings is not { } settings || selection == CurrentAppearance)
            return;
        _appearance = selection;
        AndroidFontScale.Apply(activity, selection.FontScale);
        RenderMoreNow();
        QueueAppearanceSave(settings, selection, ++_appearanceRequest);
    }

    /// <summary>
    /// A text-size slider move (drag, tap, D-pad key down): shown on the slider and its
    /// label only. The app-wide scale is not changed until <see cref="CommitTextSize"/>.
    /// </summary>
    private void PreviewTextSize(double fontScale)
    {
        if (_settings is null || _textSizePreview == fontScale)
            return;
        _textSizePreview = fontScale;
        RenderMoreNow();
    }

    /// <summary>
    /// The slider's onValueChangeFinished: a released drag or tap, a D-pad key up, or an
    /// accessibility set-progress. Applies the previewed size app-wide once and saves it.
    /// </summary>
    private void CommitTextSize()
    {
        if (_textSizePreview is not { } fontScale)
            return;
        _textSizePreview = null;
        SetAppearance(CurrentAppearance.WithFontScale(fontScale));
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
