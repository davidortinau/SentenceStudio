using MauiReactor;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Timer;
using MauiReactor.Shapes;

namespace SentenceStudio.Components;

/// <summary>
/// Reusable timer bar component for displaying activity session time.
/// Shows elapsed time with pause/resume controls.
/// Designed to be used in navigation bar or as a toolbar item.
/// </summary>
class ActivityTimerBarState
{
    public TimeSpan ElapsedTime { get; set; } = TimeSpan.Zero;
    public bool IsRunning { get; set; } = false;
    public bool IsActive { get; set; } = false;
    public bool IsInitialized { get; set; } = false;
}

partial class ActivityTimerBar : Component<ActivityTimerBarState>
{
    [Inject]
    IActivityTimerService _timerService;

    [Inject]
    ILogger<ActivityTimerBar> _logger;

    protected override void OnMounted()
    {
        _logger.LogDebug("🚀 ActivityTimerBar.OnMounted() START");
        base.OnMounted();

        _logger.LogDebug("⏱️ ActivityTimerBar.OnMounted() called");
        _logger.LogDebug("⏱️ Timer service IsActive: {IsActive}", _timerService.IsActive);
        _logger.LogDebug("⏱️ Timer service IsRunning: {IsRunning}", _timerService.IsRunning);
        _logger.LogDebug("⏱️ Timer service ElapsedTime: {ElapsedTime}", _timerService.ElapsedTime);

        // Subscribe to timer events
        _timerService.TimerStateChanged += OnTimerStateChanged;
        _timerService.TimerTick += OnTimerTick;

        _logger.LogDebug("⏱️ Timer events subscribed");

        // Initialize state and mark as initialized
        SetState(s =>
        {
            s.IsActive = _timerService.IsActive;
            s.IsRunning = _timerService.IsRunning;
            s.ElapsedTime = _timerService.ElapsedTime;
            s.IsInitialized = true;
        });

        _logger.LogDebug("✅ State initialized - IsActive: {IsActive}, IsRunning: {IsRunning}, IsInitialized: {IsInitialized}", State.IsActive, State.IsRunning, State.IsInitialized);
    }

    protected override void OnWillUnmount()
    {
        // Unsubscribe from timer events
        _timerService.TimerStateChanged -= OnTimerStateChanged;
        _timerService.TimerTick -= OnTimerTick;

        base.OnWillUnmount();
    }

    private void OnTimerStateChanged(object? sender, EventArgs e)
    {
        SetState(s =>
        {
            s.IsActive = _timerService.IsActive;
            s.IsRunning = _timerService.IsRunning;
            s.ElapsedTime = _timerService.ElapsedTime;
        });
    }

    private void OnTimerTick(object? sender, TimeSpan elapsed)
    {
        SetState(s => s.ElapsedTime = elapsed);
    }

    public override VisualNode Render()
    {
        _logger.LogDebug("🎯 Render() CALLED - IsInitialized: {IsInitialized}, IsActive: {IsActive}, IsRunning: {IsRunning}, Elapsed: {Elapsed}", State.IsInitialized, State.IsActive, State.IsRunning, State.ElapsedTime);

        // Show placeholder until initialized or when not active
        if (!State.IsInitialized || !State.IsActive)
        {
            _logger.LogDebug("⏱️ Returning gray placeholder Label");
            return Label("⏱️ --:--")
                .FontSize(16)
                .FontAttributes(MauiControls.FontAttributes.Bold)
                .TextColor(Colors.Gray);
        }

        var minutes = (int)State.ElapsedTime.TotalMinutes;
        var seconds = State.ElapsedTime.Seconds;
        var timeText = $"⏱️ {minutes:00}:{seconds:00}";

        _logger.LogDebug("⏱️ Returning active timer Label: {TimeText}", timeText);

        return Label(timeText)
            .FontSize(16)
            .FontAttributes(MauiControls.FontAttributes.Bold)
            .TextColor(BootstrapTheme.Current.GetOnBackground());
    }
}
