using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using ComposeScrollState = AndroidX.Compose.ScrollState;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell(MainActivity activity) : IDisposable
{
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MutableState<int> _revision = new(0);
    private ComposeScrollState _signInScroll = null!;
    private ComposeScrollState _dashboardScroll = null!;
    private ComposeScrollState _quizScroll = null!;
    private bool _insightExpanded;
    private bool _dashboardMenuOpen;
    private AndroidServiceHost? _host;
    private IServiceScope? _scope;
    private NativeAuthPresenter? _auth;
    private NativeDashboardPresenter? _dashboard;
    private NativeQuizPresenter? _quiz;
    private string? _displayedQuizTurnId;
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
    internal MutableState<string> Password { get; } = new(string.Empty);
    internal MutableState<bool> Remember { get; } = new(false);
    internal MutableState<bool> ShowPassword { get; } = new(false);
    internal MutableState<string> Answer { get; } = new(string.Empty);
    internal MutableState<string> Correction { get; } = new(string.Empty);
    internal MutableState<string> Error { get; } = new(string.Empty);
    internal MutableState<bool> Busy { get; } = new(false);

    internal void RefreshForUiMode() => Post(() =>
        _revision.Value = unchecked(_revision.Value + 1));

    public void Start() => Execute(InitializeNormalAsync);

    private async Task InitializeNormalAsync(CancellationToken cancellationToken)
    {
        _host = AndroidServiceHost.Create(activity);
        await _host.InitializeAsync(cancellationToken);
        _scope = _host.CreateScope();
        _auth = _scope.ServiceProvider.GetRequiredService<NativeAuthPresenter>();
        _dashboard = _scope.ServiceProvider.GetRequiredService<NativeDashboardPresenter>();
        _auth.StateChanged += OnAuthChanged;
        _dashboard.StateChanged += OnDashboardChanged;
        await HandleAuthAsync(await _auth.RestoreAsync(cancellationToken), cancellationToken);
    }

    public bool HandleBack()
    {
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
            _insightExpanded = false;
            _dashboardMenuOpen = false;
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
            Publish(() => Destination = NativeDestination.SignIn);
            await InitializeNormalAsync(cancellationToken);
            return;
        }
#endif
        if (_auth is null)
            throw new InvalidOperationException("Authentication has not initialized.");
        await HandleAuthAsync(await _auth.SignOutAsync(cancellationToken), cancellationToken);
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
        await DebugPreviewFixture.PrepareAsync(_host, _scope, cancellationToken);
        _dashboard = _scope.ServiceProvider.GetRequiredService<NativeDashboardPresenter>();
        _dashboard.StateChanged += OnDashboardChanged;
        _preview = true;
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
            ShowError("This assigned quiz is no longer available. Refresh Today's Plan.");
            return;
        }
        if (_quiz is null)
        {
            _quiz = _scope.ServiceProvider.GetRequiredService<NativeQuizPresenter>();
            _quiz.StateChanged += OnQuizChanged;
        }
        Publish(() =>
        {
            Answer.Value = string.Empty;
            Correction.Value = string.Empty;
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

    public void SubmitTextAnswer() => AnswerQuiz(Answer.Value);

    public void NextQuestion() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().NextAsync(cancellationToken), cancellationToken));

    public void SubmitCorrection() => Execute(async cancellationToken =>
        await ApplyQuizStateAsync(
            await RequiredQuiz().SubmitCorrectionAsync(
                Correction.Value, cancellationToken), cancellationToken));

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

    public void OpenDetails() => ApplyQuizState(RequiredQuiz().OpenDetails());
    public void OpenPhoto() => ApplyQuizState(RequiredQuiz().OpenPhoto());
    public void DismissOverlay() => ApplyQuizState(RequiredQuiz().DismissOverlay());

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
            Auth = state;
            Destination = AndroidRoutePolicy.AfterAuth(state);
        });
        if (state.Phase == NativePhase.Ready
            && state.Destination == NativeDestination.Dashboard)
        {
            if (_dashboard is not null)
                await _dashboard.LoadAsync(cancellationToken);
        }
    }

    private async Task ApplyQuizStateAsync(
        NativeQuizState state, CancellationToken cancellationToken)
    {
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

    private void OnAuthChanged(NativeAuthState state) => Publish(() => Auth = state);
    private void OnDashboardChanged(NativeDashboardState state) =>
        Publish(() => Dashboard = state);
    private void OnQuizChanged(NativeQuizState state) =>
        Publish(() => UpdateQuizState(state));

    private void UpdateQuizState(NativeQuizState state)
    {
        if (AndroidQuizInputPolicy.ShouldClearForNewTurn(_displayedQuizTurnId, state))
        {
            Answer.Value = string.Empty;
            Correction.Value = string.Empty;
            _displayedQuizTurnId = state.TurnId;
        }
        else if (state.Phase is NativePhase.ResumeChoice or NativePhase.Summary
            || state.Destination == NativeDestination.TodaysPlan)
        {
            _displayedQuizTurnId = null;
        }
        Quiz = state;
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
        global::Android.Util.Log.Error(
            "SentenceStudio.Native", $"Android journey failed ({ex.GetType().Name}): {ex.Message}");
        ShowError(ex is InvalidOperationException
            && ex.Message.Contains("PendingModelChangesWarning", StringComparison.Ordinal)
            ? "Shared SQLite migrations do not match the Android data model. No database was reset."
            : $"Something went wrong ({ex.GetType().Name}). Check the Android logs and retry.");
    }

    private void ShowError(string message) => Post(() => Error.Value = message);

    private void Publish(Action update) => Post(() =>
    {
        update();
        activity.UpdateBackHandling(AndroidRoutePolicy.HandlesBack(Destination));
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
        _scope?.Dispose();
        _host?.Dispose();
        _lifetime.Dispose();
    }
}
