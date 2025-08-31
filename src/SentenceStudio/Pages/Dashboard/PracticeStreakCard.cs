using MauiReactor;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Pages.Dashboard;

public partial class PracticeStreakCard : Component
{
    [Prop]
    private List<PracticeHeatPoint> _heatData;

    [Prop]
    private bool _isVisible;

    public override VisualNode Render()
    {
        if (!_isVisible || _heatData?.Any() != true) return ContentView();

        var recent14Days = _heatData.TakeLast(14).ToList();
        var currentStreak = CalculateCurrentStreak(recent14Days);
        var maxStreak = CalculateMaxStreak(_heatData);

        return Border(
            VStack(
                Label("Practice Streak").FontSize(16).FontAttributes(FontAttributes.Bold),
                
                // Streak stats
                HStack(
                    VStack(
                        Label($"{currentStreak}")
                            .FontSize(28)
                            .FontAttributes(FontAttributes.Bold)
                            .TextColor(currentStreak > 0 ? Colors.Green : Colors.Gray)
                            .HorizontalOptions(LayoutOptions.Center),
                        Label("Current Streak")
                            .FontSize(10)
                            .TextColor(Colors.Gray)
                            .HorizontalOptions(LayoutOptions.Center)
                    ).Spacing(2),
                    
                    VStack(
                        Label($"{maxStreak}")
                            .FontSize(20)
                            .FontAttributes(FontAttributes.Bold)
                            .TextColor(Colors.Orange)
                            .HorizontalOptions(LayoutOptions.Center),
                        Label("Best Streak")
                            .FontSize(10)
                            .TextColor(Colors.Gray)
                            .HorizontalOptions(LayoutOptions.Center)
                    ).Spacing(2)
                ).Spacing(40).HorizontalOptions(LayoutOptions.Center),

                // Visual representation of last 14 days
                Label("Last 14 days").FontSize(12).TextColor(Colors.Gray).HorizontalOptions(LayoutOptions.Center),
                HStack(
                    recent14Days.Select(day =>
                        Border(
                            ContentView()
                                .WidthRequest(16)
                                .HeightRequest(16)
                        )
                        .BackgroundColor(GetHeatColor(day.Count))
                        .StrokeThickness(0)
                        .Margin(1)
                    ).ToArray()
                ).HorizontalOptions(LayoutOptions.Center).Spacing(2)
            ).Spacing(12).Padding(16)
        ).StrokeThickness(1).Stroke(Colors.LightGray);
    }

    private int CalculateCurrentStreak(List<PracticeHeatPoint> recent14Days)
    {
        int streak = 0;
        for (int i = recent14Days.Count - 1; i >= 0; i--)
        {
            if (recent14Days[i].Count > 0)
                streak++;
            else
                break;
        }
        return streak;
    }

    private int CalculateMaxStreak(List<PracticeHeatPoint> allData)
    {
        int maxStreak = 0;
        int currentStreak = 0;
        
        foreach (var day in allData)
        {
            if (day.Count > 0)
            {
                currentStreak++;
                maxStreak = Math.Max(maxStreak, currentStreak);
            }
            else
            {
                currentStreak = 0;
            }
        }
        
        return maxStreak;
    }

    private Color GetHeatColor(int count)
    {
        return count switch
        {
            0 => Color.FromRgba(200, 200, 200, 128),
            1 => Color.FromRgba(144, 238, 144, 180),
            2 => Color.FromRgba(50, 205, 50, 200),
            3 => Color.FromRgba(34, 139, 34, 220),
            _ => Color.FromRgba(0, 100, 0, 255)
        };
    }
}