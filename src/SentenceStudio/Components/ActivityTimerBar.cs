using MauiReactor;
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

    protected override void OnMounted()
    {
        System.Diagnostics.Debug.WriteLine("🚀 ActivityTimerBar.OnMounted() START");
        base.OnMounted();

        System.Diagnostics.Debug.WriteLine("⏱️ ActivityTimerBar.OnMounted() called");
        System.Diagnostics.Debug.WriteLine($"⏱️ Timer service IsActive: {_timerService.IsActive}");
        System.Diagnostics.Debug.WriteLine($"⏱️ Timer service IsRunning: {_timerService.IsRunning}");
        System.Diagnostics.Debug.WriteLine($"⏱️ Timer service ElapsedTime: {_timerService.ElapsedTime}");

        // Subscribe to timer events
        _timerService.TimerStateChanged += OnTimerStateChanged;
        _timerService.TimerTick += OnTimerTick;

        System.Diagnostics.Debug.WriteLine("⏱️ Timer events subscribed");

        // Initialize state and mark as initialized
        SetState(s =>
        {
            s.IsActive = _timerService.IsActive;
            s.IsRunning = _timerService.IsRunning;
            s.ElapsedTime = _timerService.ElapsedTime;
            s.IsInitialized = true;
        });

        System.Diagnostics.Debug.WriteLine($"✅ State initialized - IsActive: {State.IsActive}, IsRunning: {State.IsRunning}, IsInitialized: {State.IsInitialized}");
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
        System.Diagnostics.Debug.WriteLine($"🎯 Render() CALLED - IsInitialized: {State.IsInitialized}, IsActive: {State.IsActive}, IsRunning: {State.IsRunning}, Elapsed: {State.ElapsedTime}");

        // Show placeholder until initialized or when not active
        if (!State.IsInitialized || !State.IsActive)
        {
            System.Diagnostics.Debug.WriteLine("⏱️ Returning gray placeholder Label");
            return Label("⏱️ --:--")
                .FontSize(16)
                .FontAttributes(MauiControls.FontAttributes.Bold)
                .TextColor(Colors.Gray);
        }

        var minutes = (int)State.ElapsedTime.TotalMinutes;
        var seconds = State.ElapsedTime.Seconds;
        var timeText = $"⏱️ {minutes:00}:{seconds:00}";

        System.Diagnostics.Debug.WriteLine($"⏱️ Returning active timer Label: {timeText}");

        return Label(timeText)
            .FontSize(16)
            .FontAttributes(MauiControls.FontAttributes.Bold)
            .TextColor(MyTheme.PrimaryText);
    }
}
