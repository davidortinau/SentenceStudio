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

        var theme = BootstrapTheme.Current;

        return Border(
            VStack(
                Label("Recent Resources").H5(),
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
                                ).Spacing(8),
                                ProgressBar()
                                    .Progress(resource.Proficiency)
                                    .ProgressColor(GetProficiencyColor(resource.Proficiency))
                                    .Margin(0, 4, 0, 0),
                                HStack(
                                    Label($"{resource.Attempts} attempts")
                                        .Small()
                                        .Muted(),
                                    Label($"{Math.Round(resource.CorrectRate * 100)}% correct")
                                        .Small()
                                        .Muted(),
                                    Label($"{resource.Minutes} min")
                                        .Small()
                                        .Muted()
                                        .HEnd()
                                ).Spacing(8)
                            ).Spacing(4).Padding(8)
                        ).StrokeThickness(0.5).Stroke(theme.GetOutline()).Margin(0, 4)
                    ).ToArray()
                ).Spacing(4)
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