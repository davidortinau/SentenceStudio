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
    private bool _resourceFormFromDashboard;
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
    /// <summary>The open resource detail came from a Study link; its Back returns to Study.</summary>
    internal bool IsResourceFormFromStudy => _resourceFormFromDashboard && IsResourcesFormOpen;
    /// <summary>An open smart resource: the page is read, not edited.</summary>
    internal bool IsResourceFormReadOnly => _resources is { IsFormOpen: true, IsCreatingForm: false } resources
        && resources.FormState.IsSmartResource;
    internal bool IsConflictPresented =>
        _resources?.FormState.Phase == NativeResourceDetailPhase.Conflict;
    internal bool IsChooseOwnSelectorPresented => _chooseOwnSelector is not null;
    internal string ChooseOwnSearch => _chooseOwnSearch;
    internal bool IsQuizDetailsPresented =>
        _quizState.Overlay == NativeQuizOverlay.LearningDetails;
    internal int SelectedTab => _selectedTab;
    internal bool IsSkillsOpen => _skills?.IsOpen == true;
    internal bool IsSkillFormOpen => _skills?.IsFormOpen == true;
    internal bool IsSkillDeletePresented =>
        _skills?.IsDeleteConfirmationPresented == true;
    internal bool IsSkillDeletePending => _skills?.IsDeletePending == true;
    internal long SkillsListVersion => _skills?.ListVersion ?? -1;
    internal long SkillFormVersion => _skills?.FormVersion ?? -1;
    internal string SkillFormTitle => Text(_skills?.FormState.Creating == true
        ? "SkillAdd_Title" : "SkillEdit_Title");
    /// <summary>The scene reads it only while Settings is in front.</summary>
    internal int SettingsProjectionVersion => !_settingsOpen ? -1
        : HashCode.Combine(_settingsGeneration, _settings?.ViewVersion ?? -2,
            SamSettingsVersion, _profileSummary);
    internal string Localize(string key) => Text(key);
    internal string DashboardTitle => Text(_dashboardState.IsNewUser
        ? "Dashboard_Welcome" : "Nav_Study");
    internal bool IsNewDashboardUser => _dashboardState.IsNewUser;
    internal string SignOutTitle => Text("Nav_Logout");
    internal bool CanRegeneratePlan =>
        _dashboardState.Mode == NativeDashboardMode.TodaysPlan
        && _dashboardState.Phase == NativePhase.Ready && !_busy;
    internal bool NavigationBlocked => _busy
        || _resources?.IsNavigationBlocked == true
        || _skills?.IsPending == true
        || _vocabulary?.IsNavigationBlocked == true
        || IsSamSlotCheckingShown;

    private bool _launching = true;

    /// <summary>
    /// The app is starting: the launch look (the launch screen's color and logo) stays
    /// until sign-in is resolved, so a remembered session goes straight to Study and Sign In
    /// is drawn only when there is no session to restore or the restore failed.
    /// </summary>
    internal bool IsLaunching => _launching;

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
            NativeAudioSession.UsePlayback(_logger);
            var preferences = _services.Provider.GetRequiredService<IPreferencesService>();
            _rememberMe = preferences.Get("remember_me", false);
            if (_rememberMe)
                _email = preferences.Get("remember_email", string.Empty);
            await _services.InitializeDatabaseAsync(_lifetime.Token);
#if DEBUG
            if (NativePreviewFixture.FromAssembly() is { } fixture)
            {
                await RunPreviewJourneyAsync(fixture);
                _launching = false;
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
                _auth.StateChanged += OnAuthProgress;
                _dashboard = _services.Provider
                    .GetRequiredService<NativeDashboardPresenter>();
                _dashboard.StateChanged += OnDashboardProgress;
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
            _launching = false;
            _logger?.LogError(ex, "Native iOS SwiftUI startup failed.");
            ClearSamForAccountBoundary();
            _destination = NativeDestination.SignIn;
            _preview = false;
            _authenticatedSample = false;
            _quiz?.Dispose();
            _quiz = null;
            _quizLaunch = null;
            _authState = new(NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: NativeText.UnexpectedError);
        }
        _launching = false;
        Publish();
    }

    private async Task RouteAfterAuthAsync()
    {
        if (_authState.Phase != NativePhase.Ready)
            return;
        if (_authState.Destination == NativeDestination.Dashboard)
        {
            _destination = NativeDestination.Dashboard;
            if (_launching)
            {
                // Study shows at once (its plan loads in place), not after the plan load.
                _launching = false;
                Publish();
            }
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
            {
                var profile = await _services!.Provider
                    .GetRequiredService<UserProfileRepository>()
                    .GetByIdAsync(owner, _lifetime.Token);
                _dashboardName = profile?.Name;
                if (profile is not null)
                    _profileSummary = (profile.Name ?? string.Empty,
                        profile.NativeLanguage ?? string.Empty,
                        profile.TargetLanguage ?? string.Empty);
            }
            if (_dashboard is not null)
            {
                _dashboardState = await _dashboard.LoadAfterSignInAsync(_lifetime.Token);
            }
            RefreshSamAvailability();
        }
        else if (_authState.Destination == NativeDestination.Onboarding)
        {
            if (_services is null || _authState.UserId is not { Length: > 0 } owner)
            {
                _authState = _authState with {
                    Phase = NativePhase.Blocked,
                    ErrorKey = NativeText.InvalidCredentials
                };
                ClearSamForAccountBoundary();
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
                ClearSamForAccountBoundary();
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
            ResetOnboardingNames();
        }
    }

    private void EnsureResources()
    {
        if (_resources is not null || _services is null)
            return;
        var readiness = _services.Provider.GetRequiredService<INativeSyncReadiness>();
        _resources = new LearningResourcesCoordinator(
            _services.Provider.GetRequiredService<NativeResourcePresenter>(),
            _services.Provider
                .GetRequiredService<ILogger<LearningResourcesCoordinator>>(),
            Text, () => readiness.SyncGeneration);
        _resources.Changed += Publish;
        _resources.FormOpenChanged += open =>
        {
            if (!open)
                ReturnToDashboardAfterResourceForm();
            Publish();
        };
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

    private SwiftView RenderSignInForm(SourceUiStyle style) =>
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
            retry: RetryAuthReadiness,
            forgotPassword: () => OpenAccountEntry(AccountEntry.Recovery),
            createAccount: () => OpenAccountEntry(AccountEntry.Register),
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
            _onboardingCreatingStarter,
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
            registerNext: next => _onboardingNextButton = next,
            suggestions: OnboardingSuggestions);

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
        // Source Onboarding.razor NextStep: leaving the target language step loads names.
        if (_onboardingStep == 2)
            LoadOnboardingNames(_onboardingDraft.TargetLanguage);
        _onboardingStep = Math.Min(5, _onboardingStep + 1);
        Publish();
    }

    private async Task FinishOnboardingAsync(bool createStarter)
    {
        if (_auth is null)
            return;
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
            ClearSamForAccountBoundary();
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
                else if (NativeActivityCatalog.IsImplemented(route))
                    Run(() => OpenChooseOwnActivityAsync(route));
            },
            AddResource: OpenResourceAddFromDashboard,
            OpenQuiz: id => Run(() => OpenPlanCardAsync(id)),
            OpenResource: OpenResourceFromDashboard,
            PreviewPlanVocabulary: OpenFlashcardPreview),
            isTablet: style.ViewportWidth >= 768);

    internal SwiftView RenderDashboardContent(SourceUiStyle style) =>
        DashboardView.WithToolbar(RenderDashboard(style), style, Text,
            _dashboardState.IsNewUser, _busy, _dashboardRefreshing,
            CanRegeneratePlan,
            new DashboardToolbarActions(RefreshDashboard, RegeneratePlan,
                OpenActivityLog));

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
                // Source Index.razor:804-811: no target language sends the learner to onboarding.
                if (!await RouteToOnboardingForMissingTargetLanguageAsync())
                    _dashboardStarterErrorKey = "NativeResources_SetTargetLanguage";
                return;
            }
            var created = await _services.Provider
                .GetRequiredService<NativeResourcePresenter>()
                .CreateStarterAsync(_lifetime.Token);
            if (created.ErrorKey == "NativeResources_SetTargetLanguage"
                && await RouteToOnboardingForMissingTargetLanguageAsync())
                return;
            if (created.Result is NativeResourceMutationResult.Success
                or NativeResourceMutationResult.AlreadyPresent)
            {
                if (created.Result == NativeResourceMutationResult.Success)
                    await _dashboard.EnsureStarterSkillAsync(_lifetime.Token);
                _dashboardState = await _dashboard.LoadAsync(_lifetime.Token);
                // A resource made on this device: read the list again.
                _resources?.ReloadList();
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

    /// <summary>
    /// The Settings tab: the profile row first (iOS Settings starts with your name), the
    /// settings, then Sign out last (both were in the Dashboard More menu).
    /// </summary>
    internal SwiftView RenderSettings(SourceUiStyle style)
    {
        if (!_settingsOpen)
            return SettingsView.Loading(style, Text);
        var signOut = SettingsView.SignOut(style, SignOutTitle, enabled: true, SignOut);
        var settings = _settings;
        var state = settings?.State;
        if (settings is null || state is not { IsOpen: true, OwnerBlocked: false,
                Quiz: { } quiz })
            return SettingsView.OwnerUnavailable(style, Text, signOut);
        return SettingsView.Page(style, Appearance.Selection,
            quiz, Text,
            theme => SetAppearance(settings, Appearance.Selection.WithTheme(theme)),
            mode => SetAppearance(settings, Appearance.Selection.WithMode(mode)),
            scale => SetAppearance(settings, Appearance.Selection.WithFontScale(scale)),
            language => SelectSettingsVoiceLanguage(settings, language),
            changed => settings.Edit(changed),
            () => SaveQuizSettings(settings), state.FeedbackKey,
            state.VoiceLoading, state.VoiceErrorKey, RenderSamSettings(style),
            header: SettingsView.ProfileRow(style, Text, _profileSummary, OpenProfile),
            footer: signOut);
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
        return _resources is { } resources
            ? resources.RenderBrowser(style)
            : Views.Text(Text("Common_Loading"));
    }

    internal SwiftView RenderResourceForm(SourceUiStyle style) =>
        _resources?.RenderForm(style)
        ?? Views.Text(Text("NativeResources_LoadError"));

    internal void OpenSkills() => OpenSkills(create: false);

    /// <summary>The Skills page; with <paramref name="create"/>, its Add form opens above it.</summary>
    private void OpenSkills(bool create)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsSkillsOpen || IsQuizOpen || IsResourcesFormOpen || _services is null)
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
        RunLibrary(async () =>
        {
            if (await _skills!.OpenAsync(expectedOwner))
            {
                _refreshChooseOwnAfterSkills =
                    _dashboardState.Mode == NativeDashboardMode.ChooseOwn;
                if (create)
                    _skills.OpenCreate();
            }
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
        if (_selectedTab == NativeTabs.Study && _refreshChooseOwnAfterSkills)
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

    internal void OnResourcesFormPopped()
    {
        _resources?.OnFormPopped();
        ReturnToDashboardAfterResourceForm();
    }

    // Source Today's Insight resource titles link to /resources/{id}; Back from
    // that detail returns to the plan, so a detail opened from the Dashboard
    // returns to the Dashboard tab when it closes.
    private void OpenResourceFromDashboard(string resourceId)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || string.IsNullOrWhiteSpace(resourceId) || IsQuizOpen
            || _dashboardState.Phase != NativePhase.Ready
            || _dashboardState.Narrative?.Resources.Any(resource =>
                resource.Id == resourceId) != true)
            return;
        if (HasLibraryEditor)
        {
            ShowLibraryEditor();
            return;
        }
        EnsureResources();
        if (_resources is null)
            return;
        // A smart resource being read in the Library gives way to the one Study opens;
        // the detail sits above whatever page the Library shows and Back returns to Study.
        if (IsResourcesFormOpen)
            _resources.OnFormPopped();
        _resourceFormFromDashboard = true;
        _selectedTab = NativeTabs.Library;
        ReadResourcesIfStale();
        _resources.Open(resourceId);
        if (!IsResourcesFormOpen)
        {
            _resourceFormFromDashboard = false;
            _selectedTab = NativeTabs.Study;
        }
        Publish();
    }

    private void ReturnToDashboardAfterResourceForm()
    {
        if (!_resourceFormFromDashboard || IsResourcesFormOpen)
            return;
        _resourceFormFromDashboard = false;
        _selectedTab = NativeTabs.Study;
        Publish();
    }

    internal void SearchResources(string query) => _resources?.Search(query);

    internal void CancelResourceDelete() => _resources?.CancelDelete();

    internal void ConfirmResourceDelete() => _resources?.ConfirmDelete();

    internal void ReloadResourceDraft() => _resources?.ReloadDraft();

    internal void OnFileImporterDismissed() => _resources?.OnFileImporterDismissed();

    internal void OnResourceFileImported(SwiftImportedFile file) =>
        _resources?.OnFileImported(file);

    internal void OnResourceFileImportError(string code) =>
        _resources?.OnFileImportError(code);

    /// <summary>
    /// Study's Add resource opens the form above the Library's Resources page, so the saved
    /// resource is in front of the learner when the form closes (source: the browser).
    /// </summary>
    private void OpenResourceAddFromDashboard() => OpenResourceCreate();

    /// <summary>
    /// The resource form above the Library's Resources page (Study's Add resource and the
    /// Library's quick add): the saved resource shows on the page when the form closes.
    /// </summary>
    private void OpenResourceCreate()
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked || IsQuizOpen)
            return;
        if (HasLibraryEditor)
        {
            ShowLibraryEditor();
            return;
        }
        EnsureResources();
        if (_resources is null)
            return;
        if (IsResourcesFormOpen)
            _resources.OnFormPopped();
        // Vocabulary, Skills or Minimal Pairs (and Recent lookups above Vocabulary) close.
        if (IsLibraryPageOpen && !_libraryResourcesOpen)
            ResetLibraryToList();
        _resourceFormFromDashboard = false;
        _libraryErrorKey = null;
        _libraryResourcesOpen = true;
        _selectedTab = NativeTabs.Library;
        ReadResourcesIfStale();
        _resources.OpenCreate();
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
        ClearSamForAccountBoundary();
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
        ReleaseLibraryPairs();
        _libraryResourcesOpen = false;
        _libraryErrorKey = null;
        _profileSummary = null;
        ResetSamSlot();
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
        ReleaseActivity();
        _feedbackAutoAdvanceCanceled = false;
        _selectedTab = NativeTabs.Study;
        _destination = NativeDestination.SignIn;
    }

    private void Run(Func<Task> action)
    {
        if (_disposed || _busy)
            return;
        _ = RunSafelyAsync(action);
    }

    /// <summary>A Library row's action: a failure is the Library's, not Study's plan.</summary>
    private void RunLibrary(Func<Task> action) => RunWithFailure(action, OnLibraryActionFailed);

    /// <summary>An action off Study whose failure is shown where it happened (not as Study's plan error).</summary>
    private void RunWithFailure(Func<Task> action, Action onFailure)
    {
        if (_disposed || _busy)
            return;
        _ = RunSafelyAsync(action, onFailure);
    }

    private async Task RunSafelyAsync(Func<Task> action, Action? onFailure = null)
    {
        _busy = true;
        // A render failure is logged and the action still runs, so _busy is always
        // cleared below; otherwise every later action and Back is refused until restart.
        PublishLoggingFailure();
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
            if (onFailure is not null)
                onFailure();
            else if (_destination == NativeDestination.Dashboard)
                _dashboardState = _dashboardState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.PlanError };
            else if (_destination is NativeDestination.VocabularyQuiz
                or NativeDestination.QuizSummary)
                _quizState = _quizState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.QuizUnavailable };
            else if (_destination == NativeDestination.Onboarding)
                _onboardingErrorKey = "NativeOnboarding_SaveFailed";
            else if (_destination == NativeDestination.Activity)
                _activityPopPending = false;
            else
                _authState = _authState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.UnexpectedError };
        }
        finally
        {
            _busy = false;
            var queuedNavigation = _quizNavigationAfterAudio;
            _quizNavigationAfterAudio = QuizAudioNavigation.None;
            try
            {
                if (_nativePopPending && !_handlingNativePop && IsQuizOpen)
                    HandlePoppedQuiz();
                else if (_activityPopPending && IsActivityOpen && !_disposed)
                    HandlePoppedActivity();
                else if (queuedNavigation != QuizAudioNavigation.None
                    && !_disposed && IsQuizOpen)
                    Run(queuedNavigation == QuizAudioNavigation.Back
                        ? BackFromQuizAsync : AdvanceQuizAsync);
                else
                    Publish();
            }
            catch (Exception ex)
            {
                // This task is not awaited: log instead of leaving an unobserved exception.
                _logger?.LogError(ex, "Native iOS render after an action failed.");
            }
        }
    }

    private void PublishLoggingFailure()
    {
        try
        {
            Publish();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS render before an action failed.");
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
        ReleaseActivity();
        _activityToasts?.Dispose();
        _libraryPairsToasts?.Dispose();
        _resources?.Dispose();
        ReleaseVocabulary();
        ReleaseLibraryPairs();
        _skills?.Dispose();
        _profile?.Dispose();
        _activityLog?.Dispose();
        _settings?.Dispose();
        _services?.Dispose();
        _lifetime.Dispose();
    }
}
