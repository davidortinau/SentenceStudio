using CoreFoundation;
using Foundation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services;
using UIKit;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS;

public sealed class NativeAppController : IDisposable
{
    private readonly UIWindow _window;
    private readonly UINavigationController _navigation;
    private readonly NavigationObserver _navigationObserver;
    private readonly CancellationTokenSource _lifetime = new();
    private NativeServices? _services;
    private NativeAuthPresenter? _auth;
    private NativeDashboardPresenter? _dashboard;
    private NativeQuizPresenter? _quiz;
    private NativeQuizLaunch? _quizLaunch;
    private ILogger<NativeAppController>? _logger;
    private NativeDestination _destination = NativeDestination.SignIn;
    private NativeAuthState _authState = new(NativePhase.Loading, NativeDestination.SignIn);
    private NativeDashboardState _dashboardState = new(NativePhase.Idle);
    private NativeQuizState _quizState = new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
    private string _answer = string.Empty;
    private string _correction = string.Empty;
    private NativeSignInViewController? _signInModal;
    private NativeVisualViewController? _dashboardController;
    private NativeVisualViewController? _quizController;
    private UITabBarController? _tabs;
    private IosResourcesCoordinator? _resources;
    private UIAction? _quizBackAction;
    private bool _insightExpanded;
    private bool _preview;
    private bool _authenticatedSample;
    private bool _quizWasPushed;
    private bool _nativePopPending;
    private bool _handlingNativePop;
    private bool _busy;
    private bool _disposed;

    public NativeAppController(UIWindow window, UINavigationController navigation)
    {
        _window = window;
        _navigation = navigation;
        _navigationObserver = new NavigationObserver(OnNavigationDidShow);
        _navigation.Delegate = _navigationObserver;
    }

    public async void Start()
    {
        try
        {
            _services = NativeServices.Create();
            _logger = _services.Provider.GetRequiredService<ILogger<NativeAppController>>();
            await _services.InitializeDatabaseAsync(_lifetime.Token);
#if DEBUG
            if (NativePreviewFixture.FromAssembly() is { } fixture)
            {
                await RunPreviewJourneyAsync(fixture);
                OnMain(Render);
                return;
            }
#endif
            if (NativeApiEndpoint.FromAssembly() is null)
            {
                _authState = new(NativePhase.Blocked, NativeDestination.SignIn,
                    ErrorKey: NativeText.SignInUnavailable);
            }
            else
            {
                _auth = _services.Provider.GetRequiredService<NativeAuthPresenter>();
                _dashboard = _services.Provider.GetRequiredService<NativeDashboardPresenter>();
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
            _logger?.LogError(ex, "Native iOS startup failed.");
            _destination = NativeDestination.SignIn;
            _preview = false;
            _authenticatedSample = false;
            _quiz?.Dispose();
            _quiz = null;
            _quizLaunch = null;
            _authState = new(NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: NativeText.UnexpectedError);
        }
        OnMain(Render);
    }

#if DEBUG
    private async Task RunPreviewJourneyAsync(
        NativePreviewFixture fixture, bool authenticated = false)
    {
        var provider = _services!.Provider;
        await fixture.SeedAsync(provider);
        _preview = !authenticated;
        _authenticatedSample = authenticated;
        _destination = NativeDestination.Dashboard;
        _dashboard = provider.GetRequiredService<NativeDashboardPresenter>();
        _dashboardState = await _dashboard.LoadAsync(_lifetime.Token);
        if (_dashboardState.Phase != NativePhase.Ready)
            throw new InvalidOperationException("The preview plan did not load through the shared presenter.");
        if (fixture.Surface == "dashboard")
            return;

        var card = fixture.SelectAssignedQuizCard(_dashboardState);
        if (card is null)
            return;

        await OpenAssignedQuizAsync(card.Id);
        if (_quiz is null)
            throw new InvalidOperationException("The preview quiz launch context is invalid.");
        if (_quizState.Phase == NativePhase.ResumeChoice)
        {
            if (fixture.Surface is "resume" or "mc-resume")
                return;
            _quizState = await _quiz.ResumeAsync(_lifetime.Token);
        }
        else if (fixture.Surface is "resume" or "mc-resume")
        {
            throw new InvalidOperationException("A preview resume choice needs a saved quiz session.");
        }

        if (fixture.ShouldShowRestoredSummary(_quizState))
        {
            _destination = NativeDestination.QuizSummary;
            return;
        }

        if (fixture.Surface is "back" or "mc-back")
        {
            _quizState = await _quiz.BackAsync(_lifetime.Token);
            if (_quizState.Destination != NativeDestination.TodaysPlan)
                throw new InvalidOperationException("The preview back action did not preserve the plan.");
            await ReturnToDashboardAsync();
            return;
        }
        if (fixture.Surface == "mc")
        {
            if (_quizState is not { Phase: NativePhase.Ready, Mode: "MultipleChoice" })
                throw new InvalidOperationException("The preview MC question did not use shared quiz choices.");
            return;
        }
        if (fixture.Surface is "question" or "quiz")
        {
            if (_quizState.Phase is not (NativePhase.Ready or NativePhase.Feedback))
                throw new InvalidOperationException("The preview question could not be restored.");
            return;
        }
        if (fixture.Surface == "feedback"
            && _quizState.Phase == NativePhase.Ready)
            await AnswerPreviewQuestionAsync(provider);
        if (fixture.Surface == "feedback")
        {
            if (_quizState.Phase != NativePhase.Feedback)
                throw new InvalidOperationException("The real quiz did not persist feedback.");
            return;
        }

        for (var step = 0; step < 20 && _quizState.Phase != NativePhase.Summary; step++)
        {
            if (_quizState.Phase == NativePhase.Ready)
                await AnswerPreviewQuestionAsync(provider);
            if (_quizState.Phase != NativePhase.Feedback || _quizState.RequiresCorrection)
                throw new InvalidOperationException("The real quiz could not grade a preview turn.");
            _quizState = await _quiz.NextAsync(_lifetime.Token);
        }
        if (_quizState.Phase != NativePhase.Summary)
            throw new InvalidOperationException("The real quiz did not reach its persisted round summary.");
        _destination = NativeDestination.QuizSummary;
        if (fixture.Surface == "complete")
        {
            _quizState = await _quiz.DoneAsync(_lifetime.Token);
            if (_quizState.Destination != NativeDestination.TodaysPlan)
                throw new InvalidOperationException("The real quiz did not complete its assigned plan item.");
            await ReturnToDashboardAsync();
            if (!_dashboardState.Cards.Any(item =>
                    item.Id == card.Id && item.IsCompleted))
                throw new InvalidOperationException("The completed quiz card was not persisted.");
        }
    }

    private async Task AnswerPreviewQuestionAsync(IServiceProvider provider)
    {
        var prompt = _quizState.Prompt
            ?? throw new InvalidOperationException("A visible quiz question is required.");
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = await db.VocabularyWords.AsNoTracking()
            .SingleAsync(item => item.Id == prompt.WordId, _lifetime.Token);
        var answer = prompt.IsTargetLanguagePrompt
            ? word.NativeLanguageTerm
            : word.TargetLanguageTerm;
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("A preview answer needs a stored vocabulary term.");
        _quizState = await _quiz!.AnswerAsync(answer, _lifetime.Token);
    }
#endif

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
                var authenticatedSample = await NativePreviewFixture.ForAuthenticatedOwnerAsync(
                    _services.Provider, _authState.UserId, _lifetime.Token);
                if (authenticatedSample is not null)
                {
                    await RunPreviewJourneyAsync(authenticatedSample, authenticated: true);
                    return;
                }
            }
#endif
            if (_dashboard is not null)
            {
                _dashboardState = await _dashboard.GenerateAsync(_lifetime.Token);
                if (_dashboardState is { Phase: NativePhase.Ready, HasPendingSync: true })
                    _dashboardState = await _dashboard.RefreshAsync(_lifetime.Token);
            }
        }
        else if (_authState.Destination == NativeDestination.Onboarding)
        {
            _authState = _authState with {
                Phase = NativePhase.Blocked, ErrorKey = NativeText.SignInUnavailable };
        }
    }

    private void Render()
    {
        if (_disposed)
            return;
        if (!NSThread.IsMain)
        {
            OnMain(Render);
            return;
        }

        if (_destination == NativeDestination.Dashboard && _dashboard is not null)
        {
            var dashboardController = ShowDashboard(CreateDashboardPage());
            _navigation.SetNavigationBarHidden(false, false);
            if (!ReferenceEquals(_navigation.ViewControllers?.FirstOrDefault(),
                    dashboardController))
                _navigation.SetViewControllers([dashboardController], false);
            else if (_navigation.ViewControllers?.Length > 1)
            {
                _quizWasPushed = false;
                _navigation.PopToRootViewController(true);
            }
            if (_signInModal is { } previousSignIn)
            {
                _signInModal = null;
                previousSignIn.Dispose();
            }
            ShowTopLevelTabs();
#if AILOHA_DEBUG
            _ = TagInputsWhenRealizedAsync();
#endif
            return;
        }

        if (_destination is NativeDestination.VocabularyQuiz or NativeDestination.QuizSummary
            && _quiz is not null)
        {
            var canRetry = _quizState.Phase == NativePhase.Error
                && ((_quizState.Destination == NativeDestination.QuizSummary
                        && _quizState.ErrorKey == NativeText.SaveFailed)
                    || (_quizState.CurrentTurn == 0
                        && _quizState.ErrorKey == NativeText.ConnectionError
                        && _quizLaunch is not null));
            var page = NativeFeatureViews.Quiz(
                _quizState, _answer, _busy, canRetry, CreateBanner(), Text,
                answerChanged: value => _answer = value,
                selectChoice: value => {
                    _quizState = _quizState with { SelectedAnswer = value };
                    Submit(async () => {
                        _quizState = await _quiz.AnswerAsync(value, _lifetime.Token);
                        ScheduleAutoAdvance();
                    });
                },
                playPrompt: () => Submit(async () =>
                    _quizState = await _quiz.PlayPromptAsync(_lifetime.Token)),
                submitText: () => Submit(async () => {
                    _quizState = await _quiz.AnswerAsync(_answer, _lifetime.Token);
                    ScheduleAutoAdvance();
                }),
                correctionChanged: value => _correction = value,
                correction: _correction,
                submitCorrection: () => Submit(async () => {
                    _quizState = await _quiz.SubmitCorrectionAsync(_correction, _lifetime.Token);
                    ScheduleAutoAdvance();
                }),
                overrideAsCorrect: () => Submit(async () => {
                    _quizState = await _quiz.OverrideAsCorrectAsync(_lifetime.Token);
                    ScheduleAutoAdvance();
                }),
                next: () => Submit(async () => {
                    _quizState = await _quiz.NextAsync(_lifetime.Token);
                    _answer = string.Empty;
                    _correction = string.Empty;
                    if (_quizState.Phase == NativePhase.Summary)
                        _destination = NativeDestination.QuizSummary;
                }),
                resume: () => Submit(async () =>
                    _quizState = await _quiz.ResumeAsync(_lifetime.Token)),
                startFresh: () => Submit(async () =>
                    _quizState = await _quiz.StartFreshAsync(_lifetime.Token)),
                continueRound: () => Submit(async () => {
                    _quizState = await _quiz.ContinueRoundAsync(_lifetime.Token);
                    _destination = NativeDestination.VocabularyQuiz;
                }),
                done: () => Submit(ReturnFromQuizAsync),
                retry: () => Submit(RetryQuizAsync));
            var dashboardController = _dashboardController
                ?? ShowDashboard(CreateDashboardPage());
            _quizController ??= new NativeVisualViewController();
            _quizController.HidesBottomBarWhenPushed = true;
            _quizController.Show(page, Text(NativeText.Quiz),
                quiz: _quizState,
                answerChanged: value => _answer = value,
                correctionChanged: value => _correction = value,
                busy: _busy);
            _navigation.SetNavigationBarHidden(false, false);
            if (!_nativePopPending
                && !ReferenceEquals(_navigation.TopViewController, _quizController))
            {
                if (!ReferenceEquals(_navigation.ViewControllers?.FirstOrDefault(),
                        dashboardController))
                    _navigation.SetViewControllers([dashboardController], false);
                _navigation.PushViewController(_quizController, true);
                _quizWasPushed = true;
            }
            if (_navigation.InteractivePopGestureRecognizer is { } popGesture)
                popGesture.Enabled = !_busy;
            if (_signInModal is { } previousSignIn)
            {
                _signInModal = null;
                previousSignIn.Dispose();
            }
#if AILOHA_DEBUG
            _ = TagInputsWhenRealizedAsync();
#endif
            return;
        }

        if (_destination == NativeDestination.SignIn && !_preview)
        {
            HideTopLevelTabs();
            if (_signInModal is null)
            {
                _signInModal = new NativeSignInViewController(
                    (email, password, rememberMe) => Submit(async () => {
                        if (_auth is null)
                            return;
                        _authState = await _auth.SignInAsync(email, password,
                            rememberMe: rememberMe,
                            cancellationToken: _lifetime.Token);
                        await RouteAfterAuthAsync();
                    }),
                    () => Submit(async () => {
                        if (_auth is null)
                            return;
                        _authState = _authState.Phase == NativePhase.PendingSync
                            && _authState.UserId is not null
                            ? await _auth.RetryReadinessAsync(_lifetime.Token)
                            : await _auth.RestoreAsync(_lifetime.Token);
                        await RouteAfterAuthAsync();
                    }));
            }
            if (!ReferenceEquals(_navigation.TopViewController, _signInModal))
            {
                _quizWasPushed = false;
                _navigation.SetNavigationBarHidden(true, false);
                _navigation.SetViewControllers([_signInModal], false);
            }
            _signInModal?.ShowState(_authState, _busy, _auth is not null);
        }
#if AILOHA_DEBUG
        if (_signInModal is null)
            _ = TagInputsWhenRealizedAsync();
#endif
    }

    private SwiftView CreateDashboardPage() =>
        NativeFeatureViews.Dashboard(
            _dashboardState, _busy, CreateBanner(), Text,
            refresh: () => Submit(async () => _dashboardState =
                await _dashboard!.RefreshAsync(_lifetime.Token)),
            generate: () => Submit(async () => _dashboardState =
                await _dashboard!.GenerateAsync(_lifetime.Token)),
            insightExpanded: _insightExpanded,
            toggleInsight: () => {
                _insightExpanded = !_insightExpanded;
                Render();
            });

    private void ShowTopLevelTabs()
    {
        if (_tabs is not null || _services is null)
            return;
        _resources = new IosResourcesCoordinator(
            _services.Provider.GetRequiredService<NativeResourcePresenter>(),
            _services.Provider.GetRequiredService<ILogger<IosResourcesCoordinator>>(),
            Text);
        _navigation.TabBarItem = new UITabBarItem(
            Text("Dashboard_Title"), UIImage.GetSystemImage("house"), 0)
        {
            AccessibilityIdentifier = NativeAccess.DashboardTab,
        };
        _resources.Navigation.TabBarItem = new UITabBarItem(
            Text("Resources_Title"), UIImage.GetSystemImage("books.vertical"), 1)
        {
            AccessibilityIdentifier = NativeAccess.ResourcesTab,
        };
        var tabs = new UITabBarController();
        // Detach the old window root before making it a child of the tab controller.
        _window.RootViewController = tabs;
        tabs.ViewControllers = [_navigation, _resources.Navigation];
        tabs.SelectedIndex = 0;
        _tabs = tabs;
    }

    private void HideTopLevelTabs()
    {
        if (_tabs is not { } tabs)
            return;
        tabs.ViewControllers = [];
        _window.RootViewController = _navigation;
        _resources?.Dispose();
        _resources = null;
        tabs.Dispose();
        _tabs = null;
    }

    private NativeVisualViewController ShowDashboard(SwiftView page)
    {
        _dashboardController ??= new NativeVisualViewController();
        _dashboardController.Show(page, Text("Dashboard_Title"),
            dashboard: _dashboardState,
            openQuiz: OpenQuiz,
            regenerate: () => Submit(async () => _dashboardState =
                await _dashboard!.RegenerateAsync(_lifetime.Token)),
            signOut: () => Submit(SignOutAsync),
            insightExpanded: _insightExpanded,
            preview: _preview,
            busy: _busy);
        var navigationItem = _dashboardController.NavigationItem;
        var backItem = navigationItem.BackBarButtonItem;
        if (backItem is null)
        {
            backItem = new UIBarButtonItem(string.Empty, UIBarButtonItemStyle.Plain, null, null);
            navigationItem.BackBarButtonItem = backItem;
        }
        backItem.AccessibilityLabel = Text("Dashboard_Title");
        return _dashboardController;
    }

    private void OnNavigationDidShow(UIViewController shown)
    {
        if (_disposed || !ReferenceEquals(_navigation.TopViewController, shown))
            return;
        if (ReferenceEquals(shown, _quizController))
        {
            _quizBackAction ??= UIAction.Create(_ => Submit(BackFromQuizAsync));
            _quizController!.NavigationItem.BackAction = _quizBackAction;
            return;
        }
        if (ReferenceEquals(shown, _dashboardController) && _quizController is not null)
            _quizController.NavigationItem.BackAction = null;
        if (!_quizWasPushed || _nativePopPending
            || !ReferenceEquals(shown, _dashboardController)
            || _destination is not (NativeDestination.VocabularyQuiz
                or NativeDestination.QuizSummary))
            return;

        _quizWasPushed = false;
        _nativePopPending = true;
        if (!_busy)
            HandlePoppedQuiz();
    }

    private void HandlePoppedQuiz()
    {
        if (_handlingNativePop || !_nativePopPending)
            return;
        _handlingNativePop = true;
        Submit(async () => {
            try
            {
                await BackFromQuizAsync();
            }
            finally
            {
                _handlingNativePop = false;
                _nativePopPending = false;
            }
        });
    }

#if AILOHA_DEBUG
    private async Task TagInputsWhenRealizedAsync()
    {
        try
        {
            await Task.Delay(250, _lifetime.Token);
            OnMain(() => {
                if (_disposed || _navigation.View is not { } root)
                    return;
                if (_quizState.Phase == NativePhase.Ready && _quizState.Mode == "Text")
                    AilohaDebug.TagInput(root, Text(NativeText.AnswerRequired), NativeAccess.QuizAnswer);
            });
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "The native iOS Debug agent could not tag a realized input.");
        }
    }
#endif

    private void OpenQuiz(string cardId) => Submit(() => OpenAssignedQuizAsync(cardId));

    private async Task OpenAssignedQuizAsync(string cardId)
    {
        if (_dashboard is null || _services is null)
            throw new InvalidOperationException("The native dashboard is not available.");
        var launch = await _dashboard.SelectAssignedQuizAsync(cardId, _lifetime.Token);
        if (launch is null)
        {
            _dashboardState = _dashboardState with {
                Phase = NativePhase.Error, ErrorKey = NativeText.QuizUnavailable };
            return;
        }
        _quiz?.Dispose();
        _quiz = _services.Provider.GetRequiredService<NativeQuizPresenter>();
        _quizLaunch = launch;
        _destination = NativeDestination.VocabularyQuiz;
        _quizState = await _quiz.OpenAsync(launch, _lifetime.Token);
        if (_quizState.Phase == NativePhase.Summary)
            _destination = NativeDestination.QuizSummary;
    }

    private async Task ReturnFromQuizAsync()
    {
        if (_quiz is null)
            return;
        _quizState = await _quiz.DoneAsync(_lifetime.Token);
        if (_quizState.Destination == NativeDestination.TodaysPlan)
            await ReturnToDashboardAsync();
    }

    private async Task BackFromQuizAsync()
    {
        if (_quiz is null)
            return;
        _logger?.LogInformation("Native iOS quiz Back invoked in {Phase} at turn {Turn}.",
            _quizState.Phase, _quizState.CurrentTurn);
        if (_quizState.CurrentTurn == 0
            && _quizState.Phase is (NativePhase.ResumeChoice or NativePhase.Empty
                or NativePhase.Error or NativePhase.Blocked)
            && _quizState.Destination != NativeDestination.QuizSummary)
        {
            await ReturnToDashboardAsync();
            return;
        }

        _quizState = await _quiz.BackAsync(_lifetime.Token);
        if (_quizState is { Destination: NativeDestination.TodaysPlan, Phase: NativePhase.Ready })
            await ReturnToDashboardAsync();
    }

    private async Task RetryQuizAsync()
    {
        if (_quiz is null || _quizState.Phase != NativePhase.Error)
            return;
        if (_quizState is { Destination: NativeDestination.QuizSummary, ErrorKey: NativeText.SaveFailed })
        {
            await ReturnFromQuizAsync();
        }
        else if (_quizState.CurrentTurn == 0
                 && _quizState.ErrorKey == NativeText.ConnectionError
                 && _quizLaunch is not null)
        {
            _quizState = await _quiz.OpenAsync(_quizLaunch, _lifetime.Token);
        }
    }

    private async Task ReturnToDashboardAsync()
    {
        _destination = NativeDestination.Dashboard;
        if (_dashboard is not null)
            _dashboardState = _preview || _authenticatedSample
                ? await _dashboard.LoadAsync(_lifetime.Token)
                : await _dashboard.RefreshAsync(_lifetime.Token);
        _quiz?.Dispose();
        _quiz = null;
        _quizLaunch = null;
    }

    private async Task SignOutAsync()
    {
        if (_preview)
        {
#if DEBUG
            var fixtureOwner = NativePreviewFixture.FromAssembly()?.Owner
                ?? throw new InvalidOperationException("The Debug preview owner is unavailable.");
            var preferences = _services!.Provider.GetRequiredService<IPreferencesService>();
            var scope = _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
            if (preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty)
                    != fixtureOwner
                || !scope.TryGetUserProfileId(out var currentOwner)
                || currentOwner != fixtureOwner)
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

        _authenticatedSample = false;
        _insightExpanded = false;
        _dashboardState = new(NativePhase.Idle);
        _destination = NativeDestination.SignIn;
    }

    private SwiftView? CreateBanner()
    {
        if (_preview)
            return Views.Text("Local preview - not authenticated")
                .WithAccessibilityIdentifier("native.preview.fixture");
        if (_authenticatedSample)
            return Views.Text("Local test content - authenticated")
                .WithAccessibilityIdentifier("native.auth.sample");
        return null;
    }

    private async void Submit(Func<Task> action)
    {
        if (_busy || _disposed)
            return;
        _busy = true;
        Render();
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
            _logger?.LogError(ex, "Native iOS action failed.");
            if (_destination == NativeDestination.Dashboard)
                _dashboardState = _dashboardState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.PlanError };
            else if (_destination is NativeDestination.VocabularyQuiz or NativeDestination.QuizSummary)
                _quizState = _quizState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.QuizUnavailable };
            else
                _authState = _authState with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.UnexpectedError };
        }
        finally
        {
            OnMain(() => {
                _busy = false;
                if (_nativePopPending && !_handlingNativePop
                    && _destination is (NativeDestination.VocabularyQuiz
                        or NativeDestination.QuizSummary))
                    HandlePoppedQuiz();
                else
                    Render();
            });
        }
    }

    private async void ScheduleAutoAdvance()
    {
        if (_quiz is not { } quiz || _quizState.Phase != NativePhase.Feedback
            || _quizState.RequiresCorrection)
            return;

        try
        {
            var state = await quiz.AutoAdvanceAsync(_lifetime.Token);
            if (_disposed || !ReferenceEquals(_quiz, quiz))
                return;
            _quizState = state;
            if (state.Phase == NativePhase.Summary)
                _destination = NativeDestination.QuizSummary;
            OnMain(Render);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS quiz auto-advance failed.");
            _quizState = _quizState with {
                Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed };
            OnMain(Render);
        }
    }

    private static string Text(string key)
    {
        var localized = LocalizationManager.Instance.GetString(
            key, System.Globalization.CultureInfo.CurrentUICulture);
        if (localized == key)
            throw new InvalidOperationException($"Missing shared localization resource: {key}");
        return localized;
    }

    private void OnMain(Action action)
    {
        if (NSThread.IsMain)
            action();
        else
            DispatchQueue.MainQueue.DispatchAsync(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _quiz?.Dispose();
        _navigation.WeakDelegate = null;
        if (_tabs is not null)
            _tabs.ViewControllers = [];
        _resources?.Dispose();
        _tabs?.Dispose();
        _navigationObserver.Dispose();
        _dashboardController?.Dispose();
        if (_quizController is not null)
            _quizController.NavigationItem.BackAction = null;
        _quizBackAction?.Dispose();
        _quizController?.Dispose();
        _signInModal?.Dispose();
        _signInModal = null;
        _services?.Dispose();
        _lifetime.Dispose();
    }

    private sealed class NavigationObserver(Action<UIViewController> onShown)
        : UINavigationControllerDelegate
    {
        public override void DidShowViewController(
            UINavigationController navigationController,
            UIViewController viewController, bool animated) =>
            onShown(viewController);
    }
}
