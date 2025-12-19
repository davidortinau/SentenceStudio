using MauiReactor;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Pages.Dashboard;

public partial class ResourceProgressCard : Component
{
    [Prop]
    private List<ResourceProgress> _resources;

    [Prop]
    private bool _isVisible;

    public override VisualNode Render()
    {
        if (!_isVisible || _resources?.Any() != true) return ContentView();

        return Border(
            VStack(
                Label("Recent Resources").FontSize(16).FontAttributes(FontAttributes.Bold),
                VStack(
                    _resources.Take(3).Select(resource =>
                        Border(
                            VStack(
                                HStack(
                                    Label(resource.Title)
                                        .FontSize(14)
                                        .FontAttributes(FontAttributes.Bold)
                                        .HFill(),
                                    Label($"{Math.Round(resource.Proficiency * 100)}%")
                                        .FontSize(12)
                                        .TextColor(GetProficiencyColor(resource.Proficiency))
                                ).Spacing(MyTheme.ComponentSpacing),
                                ProgressBar()
                                    .Progress(resource.Proficiency)
                                    .ProgressColor(GetProficiencyColor(resource.Proficiency))
                                    .Margin(0, MyTheme.MicroSpacing, 0, 0),
                                HStack(
                                    Label($"{resource.Attempts} attempts")
                                        .FontSize(10)
                                        .TextColor(Colors.Gray),
                                    Label($"{Math.Round(resource.CorrectRate * 100)}% correct")
                                        .FontSize(10)
                                        .TextColor(Colors.Gray),
                                    Label($"{resource.Minutes} min")
                                        .FontSize(10)
                                        .TextColor(Colors.Gray)
                                        .HEnd()
                                ).Spacing(MyTheme.ComponentSpacing)
                            ).Spacing(MyTheme.MicroSpacing).Padding(MyTheme.ComponentSpacing)
                        ).StrokeThickness(0.5).Stroke(Colors.LightGray).Margin(0, MyTheme.MicroSpacing)
                    ).ToArray()
                ).Spacing(MyTheme.MicroSpacing)
            ).Spacing(MyTheme.ComponentSpacing).Padding(MyTheme.CardPadding)
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