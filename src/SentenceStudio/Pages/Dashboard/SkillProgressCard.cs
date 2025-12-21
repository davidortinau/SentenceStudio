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
                            .Margin(MyTheme.SectionSpacing, 0)
                    ).Spacing(MyTheme.ComponentSpacing),

                    // Delta indicator
                    HStack(
                        Label("7d change:")
                            .FontSize(12)
                            .TextColor(MyTheme.SecondaryText),
                        Label($"{(_skill.Delta7d >= 0 ? "+" : "")}{Math.Round(_skill.Delta7d * 100, 1)}%")
                            .FontSize(12)
                            .FontAttributes(FontAttributes.Bold)
                            .TextColor(_skill.Delta7d >= 0 ? MyTheme.Success : MyTheme.Error)
                    ).Spacing(MyTheme.MicroSpacing).HCenter(),

                    Label($"Last activity: {_skill.LastActivityUtc.ToString("MMM dd")}")
                        .FontSize(10)
                        .TextColor(MyTheme.SecondaryText)
                        .HCenter()
                        .Margin(0, MyTheme.ComponentSpacing, 0, 0)
                ).Spacing(MyTheme.CardMargin)
            ).Spacing(MyTheme.ComponentSpacing).Padding(MyTheme.LayoutSpacing)
        ).StrokeThickness(1).Stroke(MyTheme.ItemBorder);
    }

    private Color GetProficiencyColor(double proficiency)
    {
        return proficiency switch
        {
            >= 0.8 => MyTheme.ProficiencyHigh,
            >= 0.6 => MyTheme.ProficiencyMedium,
            >= 0.4 => MyTheme.ProficiencyLow,
            _ => MyTheme.ProficiencyVeryLow
        };
    }
}