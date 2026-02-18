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

        var theme = BootstrapTheme.Current;

        return Border(
            VStack(
                Label("Current Skill Progress").H5(),
                VStack(
                    Label(_skill.Title)
                        .FontSize(18)
                        .FontAttributes(FontAttributes.Bold)
                        .HCenter(),

                    // Circular progress indicator (simulated with a large progress bar)
                    VStack(
                        Label($"{Math.Round(_skill.Proficiency * 100)}%")
                            .FontSize(32)
                            .FontAttributes(FontAttributes.Bold)
                            .TextColor(GetProficiencyColor(_skill.Proficiency))
                            .HCenter(),
                        ProgressBar()
                            .Progress(_skill.Proficiency)
                            .ProgressColor(GetProficiencyColor(_skill.Proficiency))
                            .ScaleY(3)
                            .Margin(24, 0)
                    ).Spacing(8),

                    // Delta indicator
                    HStack(
                        Label("7d change:")
                            .FontSize(12)
                            .Muted(),
                        Label($"{(_skill.Delta7d >= 0 ? "+" : "")}{Math.Round(_skill.Delta7d * 100, 1)}%")
                            .FontSize(12)
                            .FontAttributes(FontAttributes.Bold)
                            .TextColor(_skill.Delta7d >= 0 ? theme.Success : theme.Danger)
                    ).Spacing(4).HCenter(),

                    Label($"Last activity: {_skill.LastActivityUtc.ToString("MMM dd")}")
                        .Small()
                        .Muted()
                        .HCenter()
                        .Margin(0, 8, 0, 0)
                ).Spacing(8)
            ).Spacing(8).Padding(16)
        ).StrokeThickness(1).Stroke(theme.GetOutline());
    }

    private Color GetProficiencyColor(double proficiency)
    {
        var theme = BootstrapTheme.Current;
        return proficiency switch
        {
            >= 0.8 => theme.Success,
            >= 0.6 => theme.Warning,
            >= 0.4 => Color.FromArgb("#FF8C00"),
            _ => theme.Danger
        };
    }
}