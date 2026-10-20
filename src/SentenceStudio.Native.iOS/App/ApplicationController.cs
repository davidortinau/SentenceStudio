using CoreFoundation;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Authentication;
using SentenceStudio.Native.iOS.Features.Dashboard;
using SentenceStudio.Native.iOS.Features.LearningResources;
using SentenceStudio.Native.iOS.Features.Onboarding;
using SentenceStudio.Native.iOS.Features.Quiz;
using SentenceStudio.Native.iOS.Features.Settings;
using SentenceStudio.Native.iOS.Features.Skills;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationController : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private NativeServices? _services;
    private NativeAuthPresenter? _auth;
    private NativeDashboardPresenter? _dashboard;
    private NativeQuizPresenter? _quiz;
    private NativeQuizLaunch? _quizLaunch;
    private LearningResourcesCoordinator? _resources;
    private SkillsCoordinator? _skills;
    private ILogger<ApplicationController>? _logger;
    private NativeDestination _destination = NativeDestination.SignIn;
    private NativeAuthState _authState =
        new(NativePhase.Loading, NativeDestination.SignIn);
    private NativeDashboardState _dashboardState = new(NativePhase.Idle);
    private NativeQuizState _quizState =
        new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
    private NativeOnboardingDraft _onboardingDraft =
        new("", "English", "Korean");
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _answer = string.Empty;
    private bool _quizInputFocused;
    private bool _quizCorrectionCompleted;
    private string _sentenceInput = string.Empty;
    private string? _sentenceDraftRestoredTurn;
    private CancellationTokenSource? _autoAdvanceCancellation;
    private bool _feedbackAutoAdvanceCanceled;
    private DateTimeOffset? _autoAdvanceDisplayStartedAt;
    private SettingsCoordinator? _settings;
    private SwiftView? _signInSubmit;
    private SwiftView? _onboardingNextButton;
    private string _chooseOwnSearch = string.Empty;
    private string? _onboardingOwner;
    private string? _onboardingErrorKey;
    private string? _chooseOwnSelector;
    private bool _rememberMe;
    private bool _showPassword;
    private bool _insightExpanded;
    private bool _settingsOpen;
    private bool _refreshChooseOwnAfterSkills;
    private bool _dashboardStarterBusy;
    private string? _dashboardStarterErrorKey;
    private string? _dashboardName;
    private bool _preview;
    private bool _authenticatedSample;
    private bool _nativePopPending;
    private bool _handlingNativePop;
    private bool _busy;
    private bool _dashboardRefreshing;
    private bool _started;
    private bool _disposed;
    private int _onboardingStep;
    private int _onboardingStepsDone;
    private bool _onboardingCreatingStarter;
    private int _selectedTab;
    private int _skillsOriginTab;
    private static bool OnboardingDurabilityAvailable => false;
    internal SourceUiAppearance Appearance { get; private set; } =
        LoadAppearance();

    private static SourceUiAppearance LoadAppearance()
    {
#if DEBUG
        return SourceUiAppearance.Load(new NativePreferences(),
            NativePreviewFixture.FromAssembly()?.AppearanceToken,
            preferStoredPreviewAppearance: NSBundle.MainBundle.BundleIdentifier
                == SettingsOwnerAccess.FixtureBundleId);
#else
        return SourceUiAppearance.Load(new NativePreferences());
#endif
    }

    internal event Action? Changed;
    internal event Action<string>? ResourceFormTitleChanged;
    internal NativeDestination Destination => _destination;
    internal bool IsBusy => _busy;
    internal bool IsDashboardRefreshing => _dashboardRefreshing;
    internal bool IsQuizOpen => _destination is
        NativeDestination.VocabularyQuiz or NativeDestination.QuizSummary;
    internal bool IsQuizReturnPending => _nativePopPending;
    internal bool IsResourcesFormOpen => _resources?.IsFormOpen == true;
    internal string ResourcesSearchText =>
        _resources?.ListState.SearchText ?? string.Empty;
    internal bool IsStarterBusy =>
        _dashboardStarterBusy || _resources?.IsStarterBusy == true;
    internal string StarterBusyTitle => Text(_dashboardStarterBusy
        ? "Dashboard_SettingUp" : "Resources_CreatingStarter");
    internal bool IsFileImporterPresented => _resources?.IsFileImporterPresented == true;
    internal bool IsQuickAddSuggestionsPresented => _resources?.IsQuickAddOpen == true;
    internal bool HasQuickAddField => _resources?.HasQuickAddField == true;
    internal string? QuickAddFieldAutomationId => _resources?.QuickAddFieldAutomationId;
    internal SwiftView RenderResourceQuickAddSuggestions(SourceUiStyle style) =>
        _resources?.RenderQuickAddSuggestions(style)
            ?? Views.VStack();
    internal void DismissResourceQuickAddSuggestions() =>
        _resources?.DismissQuickAddSuggestions();
    internal long ResourceFormProjectionVersion =>
        _resources?.FormProjectionVersion ?? -1;
    internal long ResourceQuickAddSuggestionsVersion =>
        _resources?.QuickAddSuggestionsVersion ?? -1;
    internal bool IsDeleteConfirmationPresented =>
        _resources?.FormState.ShowDeleteConfirmation == true;
    internal bool IsResourceDeletePending =>
        _resources?.IsDeletePending == true;
    internal string ResourceFormTitle => _resources?.IsCreatingForm == true
        ? Text("ResourceAdd_Title")
        : _resources?.FormState.Title is { Length: > 0 } title
            ? title : Text("Resources_Title");
    internal bool IsConflictPresented =>
        _resources?.FormState.Phase == NativeResourceDetailPhase.Conflict;
    internal bool IsChooseOwnSelectorPresented => _chooseOwnSelector is not null;
    internal string ChooseOwnSearch => _chooseOwnSearch;
    internal bool IsQuizDetailsPresented =>
        _quizState.Overlay == NativeQuizOverlay.LearningDetails;
    internal int SelectedTab => _selectedTab;
    internal bool IsSettingsOpen => _settingsOpen;
    internal bool IsSkillsOpen => _skills?.IsOpen == true;
    internal bool IsSkillFormOpen => _skills?.IsFormOpen == true;
    internal bool IsSkillDeletePresented =>
        _skills?.IsDeleteConfirmationPresented == true;
    internal bool IsSkillDeletePending => _skills?.IsDeletePending == true;
    internal int SkillsOriginTab => _skillsOriginTab;
    internal long SkillsListVersion => _skills?.ListVersion ?? -1;
    internal long SkillFormVersion => _skills?.FormVersion ?? -1;
    internal string SkillFormTitle => Text(_skills?.FormState.Creating == true
        ? "SkillAdd_Title" : "SkillEdit_Title");
    internal int SettingsProjectionVersion => _settings?.ViewVersion ?? -1;
    internal string Localize(string key) => Text(key);
    internal string DashboardTitle => Text(_dashboardState.IsNewUser
        ? "Dashboard_Welcome" : "Dashboard_Title");
    internal bool IsNewDashboardUser => _dashboardState.IsNewUser;
    internal string SignOutTitle => Text("Nav_Logout");
    internal bool CanRegeneratePlan =>
        _dashboardState.Mode == NativeDashboardMode.TodaysPlan
        && _dashboardState.Phase == NativePhase.Ready && !_busy;
    internal bool NavigationBlocked => _busy
        || _resources?.IsNavigationBlocked == true
        || _skills?.IsPending == true
        || _vocabulary?.IsNavigationBlocked == true;

    internal async void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        try
        {
            _services = NativeServices.Create();
            _logger = _services.Provider
                .GetRequiredService<ILogger<ApplicationController>>();
            var preferences = _services.Provider.GetRequiredService<IPreferencesService>();
            _rememberMe = preferences.Get("remember_me", false);
            if (_rememberMe)
                _email = preferences.Get("remember_email", string.Empty);
            await _services.InitializeDatabaseAsync(_lifetime.Token);
#if DEBUG
            if (NativePreviewFixture.FromAssembly() is { } fixture)
            {
                await RunPreviewJourneyAsync(fixture);
                Publish();
                return;
            }
#endif
            if (NativeApiEndpoint.FromAssembly() is null)
                _authState = new(NativePhase.Blocked, NativeDestination.SignIn,
                    ErrorKey: NativeText.SignInUnavailable);
            else
            {
                _auth = _services.Provider.GetRequiredService<NativeAuthPresenter>();
                _dashboard = _services.Provider
                    .GetRequiredService<NativeDashboardPresenter>();
                _authState = await _auth.RestoreAsync(_lifetime.Token);
                await RouteAfterAuthAsync();
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS SwiftUI startup failed.");
            _destination = NativeDestination.SignIn;
            _preview = false;
            _authenticatedSample = false;
            _quiz?.Dispose();
            _quiz = null;
            _quizLaunch = null;
            _authState = new(NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: NativeText.UnexpectedError);
        }
        Publish();
    }

    private async Task RouteAfterAuthAsync()
    {
        if (_authState.Phase != NativePhase.Ready)
            return;
        if (_authState.Destination == NativeDestination.Dashboard)
        {
            _destination = NativeDestination.Dashboard;
#if DEBUG
            if (_services is not null)
            {
                var authenticatedSample = await NativePreviewFixture
                    .ForAuthenticatedOwnerAsync(
                        _services.Provider, _authState.UserId, _lifetime.Token);
                if (authenticatedSample is not null)
                {
                    await RunPreviewJourneyAsync(authenticatedSample,
                        authenticated: true);
                    return;
                }
            }
#endif
            EnsureResources();
            if (_authState.UserId is { Length: > 0 } owner)
                _dashboardName = (await _services!.Provider
                    .GetRequiredService<UserProfileRepository>()
                    .GetByIdAsync(owner, _lifetime.Token))?.Name;
            if (_dashboard is not null)
            {
                _dashboardState = await _dashboard.LoadAsync(_lifetime.Token);
                if (_dashboardState is { Phase: NativePhase.Ready, HasPendingSync: true })
                    _dashboardState = await _dashboard.RefreshAsync(_lifetime.Token);
            }
        }
        else if (_authState.Destination == NativeDestination.Onboarding)
        {
            if (_services is null || _authState.UserId is not { Length: > 0 } owner)
            {
                _authState = _authState with {
                    Phase = NativePhase.Blocked,
                    ErrorKey = NativeText.InvalidCredentials
                };
                _destination = NativeDestination.SignIn;
                return;
            }
            _destination = NativeDestination.Onboarding;
            if (_onboardingOwner == owner)
                return;
            var profile = await _services.Provider
                .GetRequiredService<UserProfileRepository>()
                .GetByIdAsync(owner, _lifetime.Token);
            if (_auth?.State is not {
                Phase: NativePhase.Ready,
                Destination: NativeDestination.Onboarding,
                UserId: { } currentOwner } || currentOwner != owner)
            {
                _authState = _auth?.State ?? _authState;
                _destination = NativeDestination.SignIn;
                return;
            }
            _onboardingOwner = owner;
            _onboardingDraft = new NativeOnboardingDraft(
                profile?.Name ?? string.Empty,
                string.IsNullOrWhiteSpace(profile?.NativeLanguage)
                    ? "English" : profile.NativeLanguage,
                string.IsNullOrWhiteSpace(profile?.TargetLanguage)
                    ? "Korean" : profile.TargetLanguage);
            _onboardingStep = 0;
            _onboardingErrorKey = null;
            _onboardingStepsDone = 0;
        }
    }

    private void EnsureResources()
    {
        if (_resources is not null || _services is null)
            return;
        _resources = new LearningResourcesCoordinator(
            _services.Provider.GetRequiredService<NativeResourcePresenter>(),
            _services.Provider
                .GetRequiredService<ILogger<LearningResourcesCoordinator>>(),
            Text, OpenSkills, OpenVocabulary);
        _resources.Changed += Publish;
        _resources.FormOpenChanged += _ => Publish();
        _resources.FileImporterChanged += _ => Publish();
        _resources.FormTitleChanged += title => ResourceFormTitleChanged?.Invoke(title);
    }

    private void EnsureSkills()
    {
        if (_skills is not null || _services is null)
            return;
        _skills = new SkillsCoordinator(
            _services.Provider.GetRequiredService<SkillProfileRepository>(),
            _services.Provider.GetRequiredService<DeviceUserScopeProvider>(),
            _services.Provider.GetRequiredService<ILogger<SkillsCoordinator>>());
        _skills.Changed += Publish;
    }

    internal SwiftView RenderSignIn(SourceUiStyle style) =>
        SignInView.Page(
            style, _authState, _email, _password, _rememberMe, _showPassword,
            _busy, _auth is not null
                && _authState.Phase != NativePhase.Loading, Text,
            emailChanged: value =>
            {
                _email = value;
                UpdateSignInSubmitAvailability();
            },
            passwordChanged: value =>
            {
                _password = value;
                UpdateSignInSubmitAvailability();
            },
            rememberChanged: value => _rememberMe = value,
            togglePassword: () =>
            {
                _showPassword = !_showPassword;
                Publish();
            },
            signIn: () => Run(async () =>
            {
                if (_auth is null || _authState.Phase == NativePhase.Loading
                    || string.IsNullOrWhiteSpace(_email)
                    || string.IsNullOrWhiteSpace(_password))
                    return;
                var password = _password;
                _password = string.Empty;
                _authState = await _auth.SignInAsync(
                    _email, password, _rememberMe, _lifetime.Token);
                await RouteAfterAuthAsync();
            }),
            retry: () => Run(async () =>
            {
                if (_auth is null)
                    return;
                _authState = _authState.Phase == NativePhase.PendingSync
                    && _authState.UserId is not null
                    ? await _auth.RetryReadinessAsync(_lifetime.Token)
                    : await _auth.RestoreAsync(_lifetime.Token);
                await RouteAfterAuthAsync();
            }),
            registerSubmit: submit => _signInSubmit = submit);

    private void UpdateSignInSubmitAvailability()
    {
        if (_signInSubmit is { } submit)
            SourceUiStyle.SetBootstrapLoginAvailability(submit, Appearance,
                !_busy
                    && _auth is not null
                    && _authState.Phase != NativePhase.Loading
                    && !string.IsNullOrWhiteSpace(_email)
                    && !string.IsNullOrWhiteSpace(_password));
    }

    internal SwiftView RenderOnboarding(SourceUiStyle style) =>
        OnboardingView.Page(
            style, _onboardingStep, _onboardingDraft, _onboardingErrorKey,
            _authState.HasPendingSync, _busy, _onboardingStepsDone,
            _onboardingCreatingStarter, OnboardingDurabilityAvailable,
            Text, changed: draft =>
            {
                _onboardingDraft = draft;
                if (_onboardingStep == 3)
                {
                    if (_onboardingNextButton is not null)
                        SourceUiStyle.SetCustomButtonAvailability(
                            _onboardingNextButton, Appearance, SourceButtonRole.Primary,
                            !_busy && !string.IsNullOrWhiteSpace(draft.Name));
                }
                else if (_onboardingStep != 4)
                    Publish();
            },
            back: () =>
            {
                if (_busy)
                    return;
                _onboardingStep = Math.Max(0, _onboardingStep - 1);
                Publish();
            },
            next: NextOnboardingStep,
            finish: createStarter =>
            {
                _onboardingCreatingStarter = createStarter;
                Run(() => FinishOnboardingAsync(createStarter));
            },
            signOut: () => Run(SignOutAsync),
            registerNext: next => _onboardingNextButton = next);

    private void NextOnboardingStep()
    {
        if (_busy || (_onboardingStep switch
            {
                1 => string.IsNullOrWhiteSpace(_onboardingDraft.NativeLanguage),
                2 => string.IsNullOrWhiteSpace(_onboardingDraft.TargetLanguage),
                3 => string.IsNullOrWhiteSpace(_onboardingDraft.Name),
                _ => false
            }))
            return;
        _onboardingStep = Math.Min(5, _onboardingStep + 1);
        Publish();
    }

    private async Task FinishOnboardingAsync(bool createStarter)
    {
        if (_auth is null)
            return;
        if (!OnboardingDurabilityAvailable)
        {
            _onboardingErrorKey = "Coach_Hint_Unknown";
            return;
        }
        _onboardingErrorKey = null;
        _onboardingStepsDone = 0;
        var saved = await _auth.SaveOnboardingProfileAsync(
            _onboardingDraft, _lifetime.Token);
        _onboardingStepsDone = saved.ContentStepsDone;
        if (saved.Outcome != NativeOnboardingOutcome.Success)
        {
            SetOnboardingFailure(saved);
            return;
        }
        Publish();
        var completed = await _auth.FinishOnboardingAsync(
            createStarter, _lifetime.Token);
        _onboardingStepsDone = completed.ContentStepsDone;
        if (completed.Outcome != NativeOnboardingOutcome.Success)
        {
            SetOnboardingFailure(completed);
            return;
        }
        _authState = _auth.State;
        if (_authState is not {
            Phase: NativePhase.Ready,
            Destination: NativeDestination.Dashboard })
        {
            _onboardingErrorKey = "NativeOnboarding_SaveFailed";
            return;
        }
        await RouteAfterAuthAsync();
    }

    private void SetOnboardingFailure(NativeOnboardingResult result)
    {
        if (result.Outcome == NativeOnboardingOutcome.Blocked)
        {
            _authState = new(NativePhase.Blocked, NativeDestination.SignIn,
                ErrorKey: result.ErrorKey ?? NativeText.InvalidCredentials);
            _destination = NativeDestination.SignIn;
            return;
        }
        _onboardingErrorKey = result.ErrorKey is "Resources_StarterFailed"
            ? "NativeOnboarding_StarterFailed"
            : result.ErrorKey ?? "NativeOnboarding_SaveFailed";
    }

    internal SwiftView RenderDashboard(SourceUiStyle style) =>
        DashboardView.Page(
            style, _dashboardState, _busy, Text,
            starterBusy: _dashboardStarterBusy,
            starterErrorKey: _dashboardStarterErrorKey,
            profileName: _dashboardName,
            insightExpanded: _insightExpanded,
            actions: new DashboardActions(
            Generate: () => Run(async () => _dashboardState =
                await _dashboard!.GenerateAsync(_lifetime.Token)),
            CreateStarter: () => Run(CreateDashboardStarterAsync),
            ToggleInsight: () =>
            {
                _insightExpanded = !_insightExpanded;
                Publish();
            },
            SelectMode: mode => Run(() => SetDashboardModeAsync(mode)),
            OpenResourceSelector: () => OpenChooseOwnSelector("resources"),
            OpenSkillSelector: () => OpenChooseOwnSelector("skill"),
            RetryChooseOwn: () => Run(async () => _dashboardState =
                await _dashboard!.LoadChooseOwnAsync(_lifetime.Token)),
            ExpandActivities: expanded =>
            {
                _dashboardState = _dashboard!.SetChooseOwnExpanded(expanded);
                Publish();
            },
            LaunchChooseOwnActivity: route =>
            {
                if (route == "vocab-quiz")
                    Run(OpenChooseOwnQuizAsync);
            },
            AddResource: OpenResourceAddFromDashboard,
            OpenQuiz: id => Run(() => OpenAssignedQuizAsync(id))),
            isTablet: style.ViewportWidth >= 768);

    internal SwiftView RenderDashboardContent(SourceUiStyle style) =>
        DashboardView.WithToolbar(RenderDashboard(style), style, Text,
            _dashboardState.IsNewUser, _busy, _dashboardRefreshing,
            CanRegeneratePlan, SignOutTitle,
            new DashboardToolbarActions(RefreshDashboard, RegeneratePlan,
                OpenSettings, OpenProfile, OpenSkills, OpenVocabulary,
                OpenActivityLog, SignOut));

    private async Task CreateDashboardStarterAsync()
    {
        if (_services is null || _dashboard is null
            || !_dashboardState.IsNewUser || _dashboardStarterBusy)
            return;
        _dashboardStarterErrorKey = null;
        _dashboardStarterBusy = true;
        Publish();
        try
        {
            if (_authState.UserId is not { Length: > 0 } owner)
            {
                _dashboardStarterErrorKey = "Dashboard_StarterCreateFailed";
                return;
            }
            var profile = await _services.Provider
                .GetRequiredService<UserProfileRepository>()
                .GetByIdAsync(owner, _lifetime.Token);
            if (profile is null)
            {
                _dashboardStarterErrorKey = "Dashboard_StarterCreateFailed";
                return;
            }
            if (string.IsNullOrWhiteSpace(profile.TargetLanguage))
            {
                _dashboardStarterErrorKey = "NativeResources_SetTargetLanguage";
                return;
            }
            var created = await _services.Provider
                .GetRequiredService<NativeResourcePresenter>()
                .CreateStarterAsync(_lifetime.Token);
            if (created.Result is NativeResourceMutationResult.Success
                or NativeResourceMutationResult.AlreadyPresent)
            {
                _dashboardState = await _dashboard.LoadAsync(_lifetime.Token);
                _resources?.OnBrowserAppeared();
            }
            else
                _dashboardStarterErrorKey = created.ErrorKey switch
                {
                    "Resources_SetNativeLanguage"
                        or "NativeResources_SetTargetLanguage"
                        or "NativeResources_SavedReloadError" => created.ErrorKey,
                    _ => "Dashboard_StarterCreateFailed"
                };
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Owned starter resource could not be opened.");
            _dashboardStarterErrorKey = "Dashboard_StarterCreateFailed";
        }
        finally
        {
            _dashboardStarterBusy = false;
        }
    }

    internal SwiftView RenderSettings(SourceUiStyle style)
    {
        var settings = _settings;
        var state = settings?.State;
        if (settings is null || state is not { IsOpen: true, OwnerBlocked: false,
                Quiz: { } quiz })
            return SettingsView.OwnerUnavailable(style, Text);
        return SettingsView.Page(style, Appearance.Selection,
            quiz, Text,
            theme => SetAppearance(settings, Appearance.Selection.WithTheme(theme)),
            mode => SetAppearance(settings, Appearance.Selection.WithMode(mode)),
            scale => SetAppearance(settings, Appearance.Selection.WithFontScale(scale)),
            language => SelectSettingsVoiceLanguage(settings, language),
            changed => settings.Edit(changed),
            () => SaveQuizSettings(settings), state.FeedbackKey,
            state.VoiceLoading, state.VoiceErrorKey);
    }

    internal void OpenSettings()
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != 0
            || NavigationBlocked || _services is null || _settingsOpen
            || IsProfileOpen || IsActivityLogOpen || IsSkillsOpen
            || IsVocabularyOpen || IsResourcesFormOpen || IsQuizOpen)
            return;
#if DEBUG
        const bool debugFixtureBuild = true;
        static string? PreviewOwner() => NativePreviewFixture.FromAssembly()?.Owner;
#else
        const bool debugFixtureBuild = false;
        static string? PreviewOwner() => null;
#endif
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        var preferences = provider.GetRequiredService<IPreferencesService>();
        var owner = SettingsOwnerAccess.Resolve(
            _authState, _preview, debugFixtureBuild,
            NSBundle.MainBundle.BundleIdentifier, PreviewOwner,
            scope, preferences, _logger!);
        if (owner is null)
        {
            _settingsOpen = true;
            Publish();
            return;
        }
        var settings = new SettingsCoordinator(scope,
            preferences,
            provider.GetRequiredService<NativeQuizSettings>(),
            provider.GetRequiredService<ILogger<SettingsCoordinator>>(),
            allowVoiceDiscovery: !_preview);
        settings.Changed += OnSettingsChanged;
        _settings = settings;
        if (!settings.Open(owner))
        {
            _logger?.LogWarning("Settings refused an unavailable owner.");
            settings.Dispose();
            _settings = null;
        }
        _settingsOpen = true;
        Publish();
        if (_settings is not null)
            _ = LoadSettingsVoicesAsync(
                settings, owner, settings.State.Quiz!.VoiceLanguage);
    }

    internal void OnSettingsPopped()
    {
        _settings?.Dispose();
        _settings = null;
        _settingsOpen = false;
        Publish();
    }

    private void OnSettingsChanged() => Publish();

    private void SelectSettingsVoiceLanguage(
        SettingsCoordinator settings, string language)
    {
        if (!_settingsOpen || !ReferenceEquals(_settings, settings))
            return;
        if (settings.SelectVoiceLanguage(language) is { } owner)
            _ = LoadSettingsVoicesAsync(settings, owner, language);
    }

    private Task LoadSettingsVoicesAsync(
        SettingsCoordinator settings, string owner, string language) =>
        settings.LoadVoicesAsync(owner, language, _lifetime.Token);

    private void SetAppearance(
        SettingsCoordinator settings, AppearanceSelection selection)
    {
        if (!_settingsOpen || !ReferenceEquals(_settings, settings)
            || selection == Appearance.Selection)
            return;
        if (settings.SaveAppearance(selection) is { } saved)
        {
            Appearance = saved;
            Publish();
        }
    }

    private void SaveQuizSettings(SettingsCoordinator settings)
    {
        if (_settingsOpen && ReferenceEquals(_settings, settings))
            settings.SaveQuiz();
    }

    internal void RefreshDashboard()
    {
        if (_disposed || _busy || _dashboard is null)
            return;
        _dashboardRefreshing = true;
        Run(async () =>
        {
            try
            {
                _dashboardState = await _dashboard.RefreshAsync(_lifetime.Token);
            }
            finally
            {
                _dashboardRefreshing = false;
            }
        });
    }

    internal void RegeneratePlan() => Run(async () =>
        _dashboardState = await _dashboard!.RegenerateAsync(_lifetime.Token));

    internal void SignOut() => Run(SignOutAsync);

    private async Task SetDashboardModeAsync(NativeDashboardMode mode)
    {
        if (_dashboard is null)
            return;
        _dashboardState = _dashboard.SetMode(mode);
        _dashboardState = mode == NativeDashboardMode.ChooseOwn
            ? await _dashboard.LoadChooseOwnAsync(_lifetime.Token)
            : await _dashboard.LoadAsync(_lifetime.Token);
    }

    internal SwiftView RenderResources(SourceUiStyle style)
    {
        EnsureResources();
        return _resources?.RenderBrowser(style)
            ?? Views.Text(Text("Common_Loading"));
    }

    internal SwiftView RenderResourceForm(SourceUiStyle style) =>
        _resources?.RenderForm(style)
        ?? Views.Text(Text("NativeResources_LoadError"));

    internal void SelectTab(int index)
    {
        if (_destination != NativeDestination.Dashboard
            || IsSkillsOpen || IsProfileOpen || IsVocabularyOpen
            || IsActivityLogOpen)
            return;
        _selectedTab = index == 1 ? 1 : 0;
        if (_selectedTab == 1)
            _resources?.OnBrowserAppeared();
        Publish();
        if (_selectedTab == 0 && _refreshChooseOwnAfterSkills)
            ReloadChooseOwnAfterSkills();
    }

    internal void OpenSkills()
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsSkillsOpen || _settingsOpen || IsProfileOpen || IsQuizOpen
            || IsResourcesFormOpen || _services is null)
            return;
        var scope = _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
        var expectedOwner = _authState is {
            Phase: NativePhase.Ready,
            Destination: NativeDestination.Dashboard,
            UserId: { Length: > 0 } authenticated }
            ? authenticated : null;
#if DEBUG
        if (expectedOwner is null && _preview)
            expectedOwner = NativePreviewFixture.FromAssembly()?.Owner;
#endif
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !scope.TryGetUserProfileId(out var current)
            || current != expectedOwner)
        {
            _logger?.LogWarning("Skills navigation refused an absent or untrusted owner.");
            return;
        }
        EnsureSkills();
        _skillsOriginTab = _selectedTab;
        Run(async () =>
        {
            if (await _skills!.OpenAsync(expectedOwner))
                _refreshChooseOwnAfterSkills =
                    _dashboardState.Mode == NativeDashboardMode.ChooseOwn;
        });
    }

    internal SwiftView RenderSkillsList(SourceUiStyle style) =>
        _skills is { IsOpen: true } coordinator
            ? SkillsView.Page(coordinator.ListState, style, Text,
                coordinator.OpenCreate,
                id => _ = coordinator.OpenEditAsync(id),
                () => _ = coordinator.ReloadAsync())
            : Views.Text(Text("Common_Loading"));

    internal SwiftView RenderSkillForm(SourceUiStyle style) =>
        _skills is { IsFormOpen: true } coordinator
            ? SkillProfileFormView.Page(coordinator.FormState, style, Text,
                new SkillProfileFormActions(
                    coordinator.SetTitle, coordinator.SetDescription,
                    () => _ = coordinator.SaveAsync(),
                    coordinator.CancelForm,
                    () => _ = coordinator.RetryEditAsync(),
                    coordinator.RequestDelete))
            : Views.Text(Text("Common_Loading"));

    internal void CancelSkillDelete() => _skills?.CancelDelete();

    internal void ConfirmSkillDelete()
    {
        if (_skills is { } coordinator)
            _ = coordinator.ConfirmDeleteAsync();
    }

    internal void OnSkillFormPopped() => _skills?.CancelForm();

    internal void OnSkillsPopped()
    {
        if (!IsSkillsOpen)
            return;
        _skills!.Close();
        if (_selectedTab == 0 && _refreshChooseOwnAfterSkills)
            ReloadChooseOwnAfterSkills();
    }

    private void ReloadChooseOwnAfterSkills()
    {
        _refreshChooseOwnAfterSkills = false;
        if (_dashboard is null
            || _dashboardState.Mode != NativeDashboardMode.ChooseOwn)
            return;
        Run(async () => _dashboardState =
            await _dashboard.LoadChooseOwnAsync(_lifetime.Token));
    }

    internal void OnResourcesFormPopped() => _resources?.OnFormPopped();

    internal void OnResourcesAppeared() => _resources?.OnBrowserAppeared();

    internal void SearchResources(string query) => _resources?.Search(query);

    internal void CancelResourceDelete() => _resources?.CancelDelete();

    internal void ConfirmResourceDelete() => _resources?.ConfirmDelete();

    internal void ReloadResourceDraft() => _resources?.ReloadDraft();

    internal void OnFileImporterDismissed() => _resources?.OnFileImporterDismissed();

    internal void OnResourceFileImported(SwiftImportedFile file) =>
        _resources?.OnFileImported(file);

    internal void OnResourceFileImportError(string code) =>
        _resources?.OnFileImportError(code);

    private void OpenResourceAddFromDashboard()
    {
        EnsureResources();
        _selectedTab = 1;
        _resources?.OnBrowserAppeared();
        _resources?.OpenCreate();
        Publish();
    }

    private void OpenChooseOwnSelector(string kind)
    {
        if (_dashboardState.Mode != NativeDashboardMode.ChooseOwn
            || _dashboardState.ChooseOwn is not { ErrorKey: null })
            return;
        _chooseOwnSelector = kind;
        _chooseOwnSearch = string.Empty;
        Publish();
    }

    internal void DismissChooseOwnSelector()
    {
        _chooseOwnSelector = null;
        _chooseOwnSearch = string.Empty;
        Publish();
    }

    internal SwiftView RenderChooseOwnSelector(SourceUiStyle style) =>
        ChooseOwnSelectorView.Page(style, _chooseOwnSelector,
            _dashboardState.ChooseOwn, _chooseOwnSearch, Text,
            SelectChooseOwn, DismissChooseOwnSelector);

    internal void FilterChooseOwn(string query)
    {
        if (_chooseOwnSelector is null)
            return;
        _chooseOwnSearch = query;
        Publish();
    }

    private void SelectChooseOwn(string? id)
    {
        if (_dashboard is null || _dashboardState.ChooseOwn is not { } state)
            return;
        if (_chooseOwnSelector == "resources")
        {
            if (id is null)
                return;
            var selected = state.SelectedResourceIds.IsDefault
                ? new List<string>() : state.SelectedResourceIds.ToList();
            if (!selected.Remove(id))
                selected.Add(id);
            _dashboardState = _dashboard.SelectChooseOwn(selected,
                state.SelectedSkillId);
            if (_dashboardState is {
                ChooseOwn: { ErrorKey: not null } } ||
                _dashboardState.Phase == NativePhase.Blocked)
                DismissChooseOwnSelector();
        }
        else
        {
            _dashboardState = _dashboard.SelectChooseOwn(
                state.SelectedResourceIds, id);
            DismissChooseOwnSelector();
        }
        Publish();
    }

    private async Task SignOutAsync()
    {
        CancelAutoAdvance();
        if (_preview)
        {
#if DEBUG
            var owner = NativePreviewFixture.FromAssembly()?.Owner
                ?? throw new InvalidOperationException(
                    "The Debug preview owner is unavailable.");
            var preferences = _services!.Provider.GetRequiredService<IPreferencesService>();
            var scope = _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
            if (preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty)
                    != owner
                || !scope.TryGetUserProfileId(out var currentOwner)
                || currentOwner != owner)
                throw new InvalidOperationException(
                    "The Debug preview owner changed before leaving the preview.");
            preferences.Remove(UserProfileRepository.ActiveProfileIdKey);
            scope.SetActiveUser(null);
#endif
            _preview = false;
            _auth ??= _services!.Provider.GetRequiredService<NativeAuthPresenter>();
            _authState = new(NativePhase.Ready, NativeDestination.SignIn);
        }
        else
        {
            if (_auth is null)
                throw new InvalidOperationException("Authentication has not initialized.");
            _authState = await _auth.SignOutAsync(_lifetime.Token);
        }
        _resources?.Dispose();
        _resources = null;
        ReleaseVocabulary();
        _skills?.Dispose();
        _skills = null;
        _profile?.Dispose();
        _profile = null;
        _activityLog?.Dispose();
        _activityLog = null;
        _settings?.Dispose();
        _settings = null;
        _refreshChooseOwnAfterSkills = false;
        _authenticatedSample = false;
        _insightExpanded = false;
        _settingsOpen = false;
        _dashboardStarterBusy = false;
        _dashboardStarterErrorKey = null;
        _dashboardName = null;
        _dashboardState = new(NativePhase.Idle);
        _chooseOwnSelector = null;
        _onboardingOwner = null;
        _quiz?.Dispose();
        _quiz = null;
        _quizLaunch = null;
        _feedbackAutoAdvanceCanceled = false;
        _selectedTab = 0;
        _destination = NativeDestination.SignIn;
    }

    private void Run(Func<Task> action)
    {
        if (_disposed || _busy)
            return;
        _ = RunSafelyAsync(action);
    }

    private async Task RunSafelyAsync(Func<Task> action)
    {
        _busy = true;
        Publish();
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS SwiftUI action failed.");
            if (_destination == NativeDestination.Dashboard)
                _dashboardState = _dashboardState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.PlanError };
            else if (_destination is NativeDestination.VocabularyQuiz
                or NativeDestination.QuizSummary)
                _quizState = _quizState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.QuizUnavailable };
            else if (_destination == NativeDestination.Onboarding)
                _onboardingErrorKey = "NativeOnboarding_SaveFailed";
            else
                _authState = _authState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.UnexpectedError };
        }
        finally
        {
            _busy = false;
            var queuedNavigation = _quizNavigationAfterAudio;
            _quizNavigationAfterAudio = QuizAudioNavigation.None;
            if (_nativePopPending && !_handlingNativePop && IsQuizOpen)
                HandlePoppedQuiz();
            else if (queuedNavigation != QuizAudioNavigation.None
                && !_disposed && IsQuizOpen)
                Run(queuedNavigation == QuizAudioNavigation.Back
                    ? BackFromQuizAsync : AdvanceQuizAsync);
            else
                Publish();
        }
    }

    private static string Text(string key) =>
        NativeLocalizedText.Get(key,
            System.Globalization.CultureInfo.CurrentUICulture);

    private void Publish()
    {
        if (_disposed)
            return;
        if (NSThread.IsMain)
            Changed?.Invoke();
        else
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (!_disposed)
                    Changed?.Invoke();
            });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelAutoAdvance();
        CancelQuizAudioPlayback();
        _lifetime.Cancel();
        _quiz?.Dispose();
        _resources?.Dispose();
        ReleaseVocabulary();
        _skills?.Dispose();
        _profile?.Dispose();
        _activityLog?.Dispose();
        _settings?.Dispose();
        _services?.Dispose();
        _lifetime.Dispose();
    }
}
