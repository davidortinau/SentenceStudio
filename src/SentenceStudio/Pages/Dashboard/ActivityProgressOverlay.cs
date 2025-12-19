using MauiReactor.Shapes;

namespace SentenceStudio.Pages.Dashboard;

/// <summary>
/// Displays in-activity progress for today's plan items.
/// Shows remaining time/rounds and completion percentage.
/// Pedagogical design:
/// - Progress visibility: Learners know how much work remains
/// - Motivation: Seeing progress toward completion encourages persistence
/// - Pacing feedback: Time-based guidance for session management
/// </summary>
partial class ActivityProgressOverlay : MauiReactor.Component
{
    [Prop]
    string? _activityTitle;

    [Prop]
    int _targetMinutes;

    [Prop]
    int _elapsedMinutes;

    [Prop]
    int? _roundsCompleted;

    [Prop]
    int? _targetRounds;

    [Prop]
    Action? _onComplete;

    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        var progress = CalculateProgress();
        var isComplete = progress >= 100;

        return Border(
            VStack(spacing: MyTheme.MicroSpacing,
                // Activity title
                Label(_activityTitle ?? _localize["ActivityProgressTitle"])
                    .TextColor(MyTheme.PrimaryText)
                    .FontSize(14)
                    .FontAttributes(MauiControls.FontAttributes.Bold),

                // Progress bar
                Grid(
                    // Background
                    Border()
                        .Background(MyTheme.ItemBackground)
                        .HeightRequest(6)
                        .StrokeThickness(0)
                        .StrokeShape(new RoundRectangle().CornerRadius(3)),

                    // Progress fill
                    Border()
                        .Background(isComplete ? MyTheme.Success : MyTheme.ProgressBarFill)
                        .HeightRequest(6)
                        .StrokeThickness(0)
                        .StrokeShape(new RoundRectangle().CornerRadius(3))
                        .HStart()
                        .WidthRequest(Math.Max(6, progress))
                ),

                // Stats
                HStack(spacing: MyTheme.ComponentSpacing,
                    // Time or rounds
                    _targetRounds.HasValue && _roundsCompleted.HasValue
                        ? Label($"{_roundsCompleted}/{_targetRounds} {_localize["RoundsLabel"]}")
                            .TextColor(MyTheme.SecondaryText)
                            .FontSize(12)
                        : Label($"{_elapsedMinutes}/{_targetMinutes} {_localize["PlanMinutesLabel"]}")
                            .TextColor(MyTheme.SecondaryText)
                            .FontSize(12),

                    // Completion indicator
                    isComplete
                        ? HStack(spacing: MyTheme.MicroSpacing,
                            Label("✓")
                                .TextColor(MyTheme.Success)
                                .FontSize(14)
                                .FontAttributes(MauiControls.FontAttributes.Bold),
                            Label(_localize["ActivityCompleteLabel"])
                                .TextColor(MyTheme.Success)
                                .FontSize(12)
                                .FontAttributes(MauiControls.FontAttributes.Bold)
                        )
                        .HEnd()
                        : null
                )
            )
            .Padding(MyTheme.Size120)
        )
        .Background(MyTheme.CardBackground)
        .Stroke(MyTheme.CardBorder)
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.Size80))
        .Margin(MyTheme.Size160)
        .OnTapped(() =>
        {
            if (isComplete)
            {
                _onComplete?.Invoke();
            }
        });
    }

    int CalculateProgress()
    {
        if (_targetRounds.HasValue && _roundsCompleted.HasValue && _targetRounds.Value > 0)
        {
            return (int)((_roundsCompleted.Value / (double)_targetRounds.Value) * 100);
        }

        if (_targetMinutes > 0)
        {
            return Math.Min(100, (int)((_elapsedMinutes / (double)_targetMinutes) * 100));
        }

        return 0;
    }
}
