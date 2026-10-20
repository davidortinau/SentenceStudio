using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native;
using SentenceStudio.Services.Theme;
using ComposeScrollState = AndroidX.Compose.ScrollState;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell(MainActivity activity) : IDisposable
{
    private enum AccountEntry { SignIn, Register, Recovery }

    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MutableState<int> _revision = new(0);
    private DevicePreferencesThemeStore _appearanceStore =
        new(new AndroidPreferences(activity));
    private AppearanceSelection? _appearance;
    private bool _appearanceSettingsOpen;
    private ComposeScrollState _settingsScroll = null!;
    private ComposeScrollState _signInScroll = null!;
    private ComposeScrollState _onboardingScroll = null!;
    private ComposeScrollState _dashboardScroll = null!;
    private ComposeScrollState _quizScroll = null!;
    private ComposeScrollState _detailsScroll = null!;
    private bool _insightExpanded;
    private bool _dashboardMenuOpen;
    private AccountEntry _accountEntry;
    private bool _registrationComplete;
    private bool _resetSent;
    private int _onboardingStep;
    private int _onboardingMinutes = 15;
    private string _onboardingCefr = "A2";
    private string[] _suggestedNames = [];
    private bool _loadingSuggestedNames;
    private AndroidServiceHost? _host;
    private IServiceScope? _scope;
    private NativeAuthPresenter? _auth;
    private NativeDashboardPresenter? _dashboard;
    private NativeQuizPresenter? _quiz;
    private AndroidQuizMedia? _media;
    private AndroidQuizPhotoDialog? _photoDialog;
    private FocusRequester? _photoFocus;
    private string? _photoFocusTurnId;
    private int _photoFocusRestoreVersion;
    private string? _displayedQuizTurnId;
    private string? _feedbackPhotoOwner;
    private string? _feedbackPhotoTurnId;
    private string? _feedbackPhotoUri;
    private bool _preview;
    private bool _disposed;

    internal NativeDestination Destination { get; private set; } = NativeDestination.SignIn;
    internal NativeAuthState Auth { get; private set; } =
        new(NativePhase.Idle, NativeDestination.SignIn);
    internal NativeDashboardState Dashboard { get; private set; } =
        new(NativePhase.Idle);
    internal NativeQuizState Quiz { get; private set; } =
        new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
    internal MutableState<string> Email { get; } = new(string.Empty);
    internal MutableState<TextFieldValue> DisplayName { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));
    internal MutableState<TextFieldValue> OnboardingName { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));
    internal MutableState<string> NativeLanguage { get; } = new("English");
    internal MutableState<string> TargetLanguage { get; } = new("Korean");
    internal MutableState<string> Password { get; } = new(string.Empty);
    internal MutableState<string> ConfirmPassword { get; } = new(string.Empty);
    internal MutableState<bool> Remember { get; } = new(false);
    internal MutableState<bool> ShowPassword { get; } = new(false);
    internal MutableState<bool> ShowConfirmPassword { get; } = new(false);
    internal MutableState<TextFieldValue> Answer { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));
    internal MutableState<TextFieldValue> Correction { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));
    internal MutableState<string> Error { get; } = new(string.Empty);
    internal MutableState<bool> Busy { get; } = new(false);

    internal void RefreshForUiMode() => Post(() =>
        _revision.Value = unchecked(_revision.Value + 1));

    private AppearanceSelection CurrentAppearance =>
        _appearance ??= _appearanceStore.TryLoad(out var stored)
            ? stored : AppearanceSelection.Default;

    private void UseAppearanceStore(bool preview)
    {
        _appearanceStore = new DevicePreferencesThemeStore(
            new AndroidPreferences(activity, preview));
        _appearance = null;
    }

    private void OpenAppearanceSettings() => Publish(() =>
    {
        if (Destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan)
            || Busy.Value)
            return;
        _dashboardMenuOpen = false;
        _appearanceSettingsOpen = true;
        Error.Value = string.Empty;
    });

    private void SelectAppearanceMode(ThemeMode mode)
    {
        if (!_appearanceSettingsOpen)
            return;
        var next = CurrentAppearance.WithMode(mode);
        if (next == CurrentAppearance)
            return;
        Execute(async cancellationToken =>
        {
            await _appearanceStore.SaveAsync(next, cancellationToken);
            Publish(() => _appearance = next);
        });
    }

    public void Start() => Execute(InitializeNormalAsync);

    private async Task InitializeNormalAsync(CancellationToken cancellationToken)
    {
        _host = AndroidServiceHost.Create(activity);
        await _host.InitializeAsync(cancellationToken);
        _scope = _host.CreateScope();
        _media = _scope.ServiceProvider.GetRequiredService<AndroidQuizMedia>();
        _auth = _scope.ServiceProvider.GetRequiredService<NativeAuthPresenter>();
        _dashboard = _scope.ServiceProvider.GetRequiredService<NativeDashboardPresenter>();
        _auth.StateChanged += OnAuthChanged;
        _dashboard.StateChanged += OnDashboardChanged;
        await HandleAuthAsync(await _auth.RestoreAsync(cancellationToken), cancellationToken);
    }

    public bool HandleBack()
    {
        if (_appearanceSettingsOpen)
        {
            Publish(() => _appearanceSettingsOpen = false);
            return true;
        }
        if (_photoDialog is not null || Quiz.Overlay == NativeQuizOverlay.FullscreenPhoto)
        {
            DismissOverlay();
            return true;
        }
        if (Destination == NativeDestination.Onboarding
            && _onboardingStep > 0)
        {
            OnboardingPrevious();
            return true;
        }
        if (Destination == NativeDestination.SignIn
            && _accountEntry != AccountEntry.SignIn)
        {
            ReturnToSignIn();
            return true;
        }
        if (!AndroidRoutePolicy.HandlesBack(Destination))
            return false;
        Execute(async cancellationToken =>
        {
            if (_quiz is null)
                return;
            var state = await _quiz.BackAsync(cancellationToken);
            if (AndroidRoutePolicy.AfterQuizBack(state) == NativeDestination.Dashboard
                && state.Destination == NativeDestination.VocabularyQuiz)
            {
                Publish(() => Destination = NativeDestination.Dashboard);
                if (_dashboard is not null)
                    await _dashboard.LoadAsync(cancellationToken);
                return;
            }
            await ApplyQuizStateAsync(state, cancellationToken);
        });
        return true;
    }

    internal bool HandlePhotoEscape()
    {
        if (_photoDialog is null
            || Quiz.Overlay != NativeQuizOverlay.FullscreenPhoto)
            return false;
        DismissOverlay();
        return true;
    }

    public void SignIn() => Execute(async cancellationToken =>
    {
        if (_auth is null)
            throw new InvalidOperationException("Authentication has not initialized.");
        var result = await _auth.SignInAsync(
            Email.Value.Trim(), Password.Value, rememberMe: Remember.Value,
            cancellationToken: cancellationToken);
        Post(() => Password.Value = string.Empty);
        await HandleAuthAsync(result, cancellationToken);
    });

    public void OpenRegistration() => Publish(() =>
    {
        _accountEntry = AccountEntry.Register;
        _registrationComplete = false;
        Password.Value = string.Empty;
        ConfirmPassword.Value = string.Empty;
        ShowPassword.Value = false;
        ShowConfirmPassword.Value = false;
        Error.Value = string.Empty;
    });

    public void OpenRecovery() => Publish(() =>
    {
        _accountEntry = AccountEntry.Recovery;
        _resetSent = false;
        Password.Value = string.Empty;
        Error.Value = string.Empty;
    });

    public void ReturnToSignIn() => Publish(() =>
    {
        _accountEntry = AccountEntry.SignIn;
        Password.Value = string.Empty;
        ConfirmPassword.Value = string.Empty;
        ShowPassword.Value = false;
        ShowConfirmPassword.Value = false;
        Error.Value = string.Empty;
    });

    public void RetryAuth() => Execute(async cancellationToken =>
    {
        if (_auth is null)
            throw new InvalidOperationException("Authentication has not initialized.");
        var result = Auth.UserId is null
            ? await _auth.RestoreAsync(cancellationToken)
            : await _auth.RetryReadinessAsync(cancellationToken);
        await HandleAuthAsync(result, cancellationToken);
    });

    public void SignOut() => Execute(async cancellationToken =>
    {
        Post(() =>
        {
            Remember.Value = false;
            ShowPassword.Value = false;
            ShowConfirmPassword.Value = false;
            Password.Value = string.Empty;
            ConfirmPassword.Value = string.Empty;
            _insightExpanded = false;
            _dashboardMenuOpen = false;
            _appearanceSettingsOpen = false;
            _chooseSelector.Value = (int)ChooseSelector.None;
        });
#if DEBUG
        if (_preview)
        {
            if (_dashboard is not null)
                _dashboard.StateChanged -= OnDashboardChanged;
            if (_quiz is not null)
            {
                _quiz.StateChanged -= OnQuizChanged;
                _quiz.Dispose();
                _quiz = null;
            }
            _scope?.Dispose();
            _host?.Dispose();
            _scope = null;
            _host = null;
            _dashboard = null;
            _preview = false;
            UseAppearanceStore(preview: false);
            Publish(() => Destination = NativeDestination.SignIn);
            await InitializeNormalAsync(cancellationToken);
            return;
        }
#endif
        if (_auth is null)
            throw new InvalidOperationException("Authentication has not initialized.");
        await HandleAuthAsync(await _auth.SignOutAsync(cancellationToken), cancellationToken);
        _media?.ClearForSignedOut();
    });

#if DEBUG
    public void OpenDebugPreview() => Execute(async cancellationToken =>
    {
        if (_preview)
            return;
        if (_auth is not null)
            _auth.StateChanged -= OnAuthChanged;
        if (_dashboard is not null)
            _dashboard.StateChanged -= OnDashboardChanged;
        if (_quiz is not null)
        {
            _quiz.StateChanged -= OnQuizChanged;
            _quiz.Dispose();
            _quiz = null;
        }
        _scope?.Dispose();
        _host?.Dispose();
        _auth = null;
        _host = AndroidServiceHost.CreatePreview(activity);
        await _host.InitializeAsync(cancellationToken);
        _scope = _host.CreateScope();
        _media = _scope.ServiceProvider.GetRequiredService<AndroidQuizMedia>();
        await DebugPreviewFixture.PrepareAsync(_host, _scope, cancellationToken);
        _dashboard = _scope.ServiceProvider.GetRequiredService<NativeDashboardPresenter>();
        _dashboard.StateChanged += OnDashboardChanged;
        _preview = true;
        UseAppearanceStore(preview: true);
        _insightExpanded = false;
        _dashboardMenuOpen = false;
        Publish(() => Destination = NativeDestination.Dashboard);
        await _dashboard.LoadAsync(cancellationToken);
    });
#endif

    public void LoadPlan() => Execute(async cancellationToken =>
    {
        if (_dashboard is null)
            throw new InvalidOperationException("Dashboard has not initialized.");
        await _dashboard.LoadAsync(cancellationToken);
    });

    public void GeneratePlan() => Execute(async cancellationToken =>
    {
        if (_dashboard is null)
            throw new InvalidOperationException("Dashboard has not initialized.");
        await _dashboard.GenerateAsync(cancellationToken);
    });

    public void RefreshPlan() => Execute(async cancellationToken =>
    {
        if (_dashboard is null)
            throw new InvalidOperationException("Dashboard has not initialized.");
        await _dashboard.RefreshAsync(cancellationToken);
    });

    public void RegeneratePlan() => Execute(async cancellationToken =>
    {
        if (_dashboard is null)
            throw new InvalidOperationException("Dashboard has not initialized.");
        await _dashboard.RegenerateAsync(cancellationToken);
    });

    public void OpenQuiz(string planItemId) => Execute(async cancellationToken =>
    {
        if (_dashboard is null || _scope is null)
            throw new InvalidOperationException("The assigned plan is not available.");
        var launch = await _dashboard.SelectAssignedQuizAsync(planItemId, cancellationToken);
        if (launch is null)
        {
            ShowError(Localize(NativeText.QuizUnavailable));
            return;
        }
        if (_quiz is null)
        {
            _quiz = _scope.ServiceProvider.GetRequiredService<NativeQuizPresenter>();
            _quiz.StateChanged += OnQuizChanged;
        }
        Publish(() =>
        {
            Answer.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
            Correction.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
            Quiz = new(NativePhase.Loading, NativeDestination.VocabularyQuiz);
            Destination = NativeDestination.VocabularyQuiz;
        });
        await ApplyQuizStateAsync(await _quiz.OpenAsync(launch, cancellationToken), cancellationToken);
    });

    public void ResumeQuiz() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().ResumeAsync(cancellationToken), cancellationToken));

    public void StartFreshQuiz() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().StartFreshAsync(cancellationToken), cancellationToken));

    public void AnswerQuiz(string response) => Execute(async cancellationToken =>
    {
        if (Quiz is { Phase: NativePhase.Ready, Mode: "MultipleChoice" })
            Publish(() => Quiz = Quiz with { SelectedAnswer = response });
        var previous = Quiz.TurnId;
        var state = await RequiredQuiz().AnswerAsync(response, cancellationToken);
        await ApplyQuizStateAsync(state, cancellationToken);
        if (!_preview && state.Phase == NativePhase.Feedback && !state.RequiresCorrection
            && state.TurnId == previous)
            _ = AutoAdvanceAsync(previous);
    });

    public void SubmitTextAnswer() => AnswerQuiz(Answer.Value.Text ?? string.Empty);

    public void NextQuestion() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().NextAsync(cancellationToken), cancellationToken));

    public void SubmitCorrection() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().SubmitCorrectionAsync(
                Correction.Value.Text ?? string.Empty, cancellationToken),
            cancellationToken));

    public void OverrideQuiz() => Execute(async cancellationToken =>
    {
        var turnId = Quiz.TurnId;
        var state = await RequiredQuiz().OverrideAsCorrectAsync(cancellationToken);
        await ApplyQuizStateAsync(state, cancellationToken);
        if (!_preview && state.Phase == NativePhase.Feedback
            && !state.RequiresCorrection && state.TurnId == turnId)
            _ = AutoAdvanceAsync(turnId);
    });

    public void RetryQuizSave() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            Quiz.Destination == NativeDestination.QuizSummary
                ? await RequiredQuiz().DoneAsync(cancellationToken)
                : await RequiredQuiz().RetryPendingSaveAsync(cancellationToken),
            cancellationToken));

    public void ContinueRound() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().ContinueRoundAsync(cancellationToken), cancellationToken));

    public void DoneQuiz() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().DoneAsync(cancellationToken), cancellationToken));

    public void OpenPhoto()
    {
        if (Quiz.Phase != NativePhase.Ready
            || Quiz.Prompt?.ShowPhoto != true
            || Quiz.Overlay != NativeQuizOverlay.None
            || !_preview && !HasDashboardOwner(Auth.UserId))
            return;
        var state = RequiredQuiz().OpenPhoto();
        if (state.Overlay != NativeQuizOverlay.FullscreenPhoto)
        {
            ApplyQuizState(state);
            return;
        }
        if (state.Prompt?.PhotoUri is not { } uri || _media is null
            || !_media.TryGetPhoto(uri, out var bitmap) || bitmap is null)
        {
            ApplyQuizState(RequiredQuiz().DismissOverlay());
            ShowError(Localize(NativeText.QuizUnavailable));
            return;
        }
        if (ShowPhotoDialog(bitmap, state.TurnId, uri))
            ApplyQuizState(state);
        else
            ApplyQuizState(RequiredQuiz().DismissOverlay());
    }

    private bool ShowPhotoDialog(
        global::Android.Graphics.Bitmap bitmap, string? turnId, string photoUri)
    {
        var ownerId = Auth.UserId;
        if (string.IsNullOrWhiteSpace(turnId)
            || string.IsNullOrWhiteSpace(ownerId))
            return false;
        AndroidQuizPhotoDialog? viewer = null;
        try
        {
            viewer = new AndroidQuizPhotoDialog(
                activity, bitmap, ownerId, turnId, photoUri,
                Localize("Common_Close"), Localize(NativeText.QuizPhoto),
                Localize("VocabQuiz_FullscreenPhoto"),
                () => OnPhotoDismissed(viewer!));
            _photoDialog?.Dispose();
            _photoDialog = viewer;
            _photoFocusTurnId = turnId;
            return true;
        }
        catch (Exception ex)
        {
            viewer?.Dispose();
            _photoDialog = null;
            _photoFocusTurnId = null;
            ShowFailure(ex);
            return false;
        }
    }

    private void OnPhotoDismissed(AndroidQuizPhotoDialog viewer)
    {
        if (!ReferenceEquals(_photoDialog, viewer))
            return;
        _photoDialog = null;
        viewer.Dispose();
        if (_quiz?.State.Overlay == NativeQuizOverlay.FullscreenPhoto)
            ApplyQuizState(_quiz.DismissOverlay());
        QueuePhotoFocusRestore();
    }

    public void DismissOverlay()
    {
        var viewer = _photoDialog;
        _photoDialog = null;
        viewer?.Dispose();
        if (_quiz?.State.Overlay is not (null or NativeQuizOverlay.None))
            ApplyQuizState(_quiz.DismissOverlay());
        if (viewer is not null)
            QueuePhotoFocusRestore();
    }

    private void ClosePhotoDialogWithoutRestore()
    {
        var viewer = _photoDialog;
        _photoDialog = null;
        _photoFocusTurnId = null;
        viewer?.Dispose();
    }

    private void QueuePhotoFocusRestore()
    {
        var turn = _photoFocusTurnId;
        _photoFocusTurnId = null;
        if (turn is null)
            return;
        Publish(() =>
        {
            if (Destination == NativeDestination.VocabularyQuiz
                && HasDashboardOwner(Auth.UserId)
                && Quiz.TurnId == turn
                && Quiz.Phase == NativePhase.Ready
                && Quiz.Prompt?.ShowPhoto == true)
                _photoFocusRestoreVersion++;
        });
    }

    public void PlayPrompt() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().PlayPromptAsync(cancellationToken), cancellationToken));

    public void SetPhotoVisible(bool visible) => Execute(async cancellationToken =>
    {
        var turnId = Quiz.TurnId;
        if (turnId is not null)
            await ApplyQuizStateAsync(
                await RequiredQuiz().SetPhotoVisibleAsync(
                    visible, turnId, cancellationToken), cancellationToken);
    });

    private async Task AutoAdvanceAsync(string? turnId)
    {
        if (turnId is null || _quiz is null)
            return;
        var quiz = _quiz;
        var owner = Auth.UserId;
        try
        {
            var state = await quiz.AutoAdvanceAsync(_lifetime.Token);
            await _actions.WaitAsync(_lifetime.Token);
            try
            {
                if (!_disposed && ReferenceEquals(_quiz, quiz)
                    && Auth.UserId == owner
                    && AndroidRoutePolicy.HandlesBack(Destination))
                    await ApplyQuizStateAsync(state, _lifetime.Token);
            }
            finally
            {
                _actions.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }

    private NativeQuizPresenter RequiredQuiz() => _quiz
        ?? throw new InvalidOperationException("No assigned quiz has been opened.");

    private async Task HandleAuthAsync(
        NativeAuthState state, CancellationToken cancellationToken)
    {
        Publish(() =>
        {
            if (Auth.UserId != state.UserId)
            {
                ClosePhotoDialogWithoutRestore();
                _media?.ClearForSignedOut();
                Dashboard = new(NativePhase.Loading);
                Quiz = new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
                _displayedQuizTurnId = null;
                ClearFeedbackPhoto();
                _insightExpanded = false;
                _appearanceSettingsOpen = false;
                _chooseSelector.Value = (int)ChooseSelector.None;
            }
            Auth = state;
            Destination = AndroidRoutePolicy.AfterAuth(state);
            if (Destination != NativeDestination.SignIn)
            {
                _accountEntry = AccountEntry.SignIn;
                Password.Value = string.Empty;
                ConfirmPassword.Value = string.Empty;
            }
        });
        if (state.Phase == NativePhase.Ready
            && state.Destination == NativeDestination.Dashboard)
        {
            if (_dashboard is not null)
                await _dashboard.LoadAsync(cancellationToken);
        }
        else if (state.Phase == NativePhase.Ready
            && state.Destination == NativeDestination.Onboarding)
            await LoadOnboardingAsync(state.UserId, cancellationToken);
    }

    private async Task ApplyQuizStateAsync(
        NativeQuizState state, CancellationToken cancellationToken)
    {
        if (state.Prompt is { ShowPhoto: true, PhotoUri: { } photoUri }
            && state.TurnId is { } turnId
            && (_media is null || !_media.TryGetPhoto(photoUri, out _)))
            state = await RequiredQuiz().PhotoFailedAsync(turnId, cancellationToken);
        ApplyQuizState(state);
        if (state.Destination == NativeDestination.TodaysPlan
            && state.Phase == NativePhase.Ready && _dashboard is not null)
            await _dashboard.LoadAsync(cancellationToken);
    }

    private void ApplyQuizState(NativeQuizState state)
    {
        Publish(() =>
        {
            UpdateQuizState(state);
            Destination = AndroidRoutePolicy.AfterQuiz(state);
        });
    }

    private void OnAuthChanged(NativeAuthState state) => Publish(() =>
    {
        if (Auth.UserId != state.UserId)
        {
            ClosePhotoDialogWithoutRestore();
            _media?.ClearForSignedOut();
            Dashboard = new(NativePhase.Loading);
            Quiz = new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
            _displayedQuizTurnId = null;
            ClearFeedbackPhoto();
            _insightExpanded = false;
            _appearanceSettingsOpen = false;
            _chooseSelector.Value = (int)ChooseSelector.None;
        }
        Auth = state;
    });
    private void OnDashboardChanged(NativeDashboardState state) =>
        Publish(() => Dashboard = _preview || HasDashboardOwner(Auth.UserId)
            ? _dashboard?.State ?? state
            : new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
    private void OnQuizChanged(NativeQuizState state) =>
        Publish(() => UpdateQuizState(_preview || HasDashboardOwner(Auth.UserId)
            ? _quiz?.State ?? state
            : new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizUnavailable)));

    private void UpdateQuizState(NativeQuizState state)
    {
        if (_photoDialog is { } viewer
            && !AndroidQuizPhotoPresentationPolicy.CanPresent(
                state, Auth.UserId, viewer.OwnerId, viewer.TurnId,
                viewer.PhotoUri))
            ClosePhotoDialogWithoutRestore();
        if (!AndroidQuizFeedbackPhotoPolicy.CanDisplay(
                state, Auth.UserId, _feedbackPhotoOwner,
                _feedbackPhotoTurnId, _feedbackPhotoUri))
            ClearFeedbackPhoto();
        if (AndroidQuizInputPolicy.ShouldClearForNewTurn(_displayedQuizTurnId, state))
        {
            Answer.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
            Correction.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
            _displayedQuizTurnId = state.TurnId;
        }
        else if (state.Phase is NativePhase.ResumeChoice or NativePhase.Summary
            || state.Destination == NativeDestination.TodaysPlan)
        {
            _displayedQuizTurnId = null;
        }
        Quiz = state;
    }

    private void ClearFeedbackPhoto()
    {
        _feedbackPhotoOwner = null;
        _feedbackPhotoTurnId = null;
        _feedbackPhotoUri = null;
    }

    private void Execute(Func<CancellationToken, Task> action) => _ = ExecuteAsync(action);

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        var entered = false;
        try
        {
            await _actions.WaitAsync(_lifetime.Token);
            entered = true;
            Post(() =>
            {
                Busy.Value = true;
                Error.Value = string.Empty;
            });
            await action(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
        finally
        {
            if (entered)
            {
                Post(() => Busy.Value = false);
                _actions.Release();
            }
        }
    }

    private void ShowFailure(Exception ex)
    {
        if (ex is InvalidOperationException
            && (ex.Message.Contains("pending migrations", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("PendingModelChangesWarning", StringComparison.Ordinal)))
            global::Android.Util.Log.Error("SentenceStudio.Native",
                "Android SQLite migration preflight refused startup; existing data was not reset.");
        else
            global::Android.Util.Log.Error(
                "SentenceStudio.Native", $"Android journey failed ({ex.GetType().Name}).");
        ShowError(Localize(NativeText.UnexpectedError));
    }

    private void ShowError(string message) => Post(() => Error.Value = message);

    private void Publish(Action update) => Post(() =>
    {
        update();
        if (_photoDialog is not null
            && Destination != NativeDestination.VocabularyQuiz)
            ClosePhotoDialogWithoutRestore();
        activity.UpdateBackHandling(_appearanceSettingsOpen
            || AndroidRoutePolicy.HandlesBack(Destination)
            || _photoDialog is not null
            || Destination == NativeDestination.Onboarding
                && _onboardingStep > 0
            || Destination == NativeDestination.SignIn
                && _accountEntry != AccountEntry.SignIn);
        _revision.Value = unchecked(_revision.Value + 1);
    });

    private void Post(Action update)
    {
        if (!_disposed)
            activity.RunOnUiThread(() =>
            {
                if (!_disposed)
                    update();
            });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        if (_auth is not null)
            _auth.StateChanged -= OnAuthChanged;
        if (_dashboard is not null)
            _dashboard.StateChanged -= OnDashboardChanged;
        if (_quiz is not null)
        {
            _quiz.StateChanged -= OnQuizChanged;
            _quiz.Dispose();
        }
        ClosePhotoDialogWithoutRestore();
        _scope?.Dispose();
        _host?.Dispose();
        _lifetime.Dispose();
    }
}
