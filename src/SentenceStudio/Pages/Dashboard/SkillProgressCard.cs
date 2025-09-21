using MauiReactor;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Pages.Dashboard;

public partial class SkillProgressCard : Component
{
    [Prop]
    private SkillProgress? _skill;

    [Prop]
    private bool _isVisible;

    public override VisualNode Render()
    {
        if (!_isVisible || _skill == null) return ContentView();

        return Border(
            VStack(
                Label("Current Skill Progress").FontSize(16).FontAttributes(FontAttributes.Bold),
                VStack(
                    Label(_skill.Title)
                        .FontSize(18)
                        .FontAttributes(FontAttributes.Bold)
                        .HorizontalOptions(LayoutOptions.Center),

                    // Circular progress indicator (simulated with a large progress bar)
                    VStack(
                        Label($"{Math.Round(_skill.Proficiency * 100)}%")
                            .FontSize(32)
                            .FontAttributes(FontAttributes.Bold)
                            .TextColor(GetProficiencyColor(_skill.Proficiency))
                            .HorizontalOptions(LayoutOptions.Center),
                        ProgressBar()
                            .Progress(_skill.Proficiency)
                            .ProgressColor(GetProficiencyColor(_skill.Proficiency))
                            .ScaleY(3)
                            .Margin(20, 0)
                    ).Spacing(8),

                    // Delta indicator
                    HStack(
                        Label("7d change:")
                            .FontSize(12)
                            .TextColor(Colors.Gray),
                        Label($"{(_skill.Delta7d >= 0 ? "+" : "")}{Math.Round(_skill.Delta7d * 100, 1)}%")
                            .FontSize(12)
                            .FontAttributes(FontAttributes.Bold)
                            .TextColor(_skill.Delta7d >= 0 ? Colors.Green : Colors.Red)
                    ).Spacing(4).HorizontalOptions(LayoutOptions.Center),

                    Label($"Last activity: {_skill.LastActivityUtc.ToString("MMM dd")}")
                        .FontSize(10)
                        .TextColor(Colors.Gray)
                        .HorizontalOptions(LayoutOptions.Center)
                        .Margin(0, 8, 0, 0)
                ).Spacing(12)
            ).Spacing(8).Padding(16)
        ).StrokeThickness(1).Stroke(Colors.LightGray);
    }

    private Color GetProficiencyColor(double proficiency)
    {
        return proficiency switch
        {
            >= 0.8 => Colors.Green,
            >= 0.6 => Colors.Orange,
            >= 0.4 => Colors.DarkOrange,
            _ => Colors.Red
        };
    }
}