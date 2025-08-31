using MauiReactor;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Pages.Dashboard;

public partial class VocabProgressCard : Component
{
    [Prop]
    private VocabProgressSummary _summary;

    [Prop]
    private bool _isVisible;

    public override VisualNode Render()
    {
        if (!_isVisible) return ContentView();

        var total = _summary.New + _summary.Learning + _summary.Review + _summary.Known;
        if (total == 0) total = 1; // Avoid division by zero

        return Border(
            VStack(
                Label("Vocabulary Progress").FontSize(16).FontAttributes(FontAttributes.Bold),
                Grid(
                    // Simple progress bars instead of charts for now
                    VStack(
                        HStack(
                            Label("Known").FontSize(12).WidthRequest(60),
                            ProgressBar()
                                .Progress(_summary.Known / (double)total)
                                .ProgressColor(Colors.Green)
                                .HorizontalOptions(LayoutOptions.FillAndExpand),
                            Label($"{_summary.Known}").FontSize(12).WidthRequest(30)
                        ).Spacing(8),
                        HStack(
                            Label("Review").FontSize(12).WidthRequest(60),
                            ProgressBar()
                                .Progress(_summary.Review / (double)total)
                                .ProgressColor(Colors.Orange)
                                .HorizontalOptions(LayoutOptions.FillAndExpand),
                            Label($"{_summary.Review}").FontSize(12).WidthRequest(30)
                        ).Spacing(8),
                        HStack(
                            Label("Learning").FontSize(12).WidthRequest(60),
                            ProgressBar()
                                .Progress(_summary.Learning / (double)total)
                                .ProgressColor(Colors.Blue)
                                .HorizontalOptions(LayoutOptions.FillAndExpand),
                            Label($"{_summary.Learning}").FontSize(12).WidthRequest(30)
                        ).Spacing(8),
                        HStack(
                            Label("New").FontSize(12).WidthRequest(60),
                            ProgressBar()
                                .Progress(_summary.New / (double)total)
                                .ProgressColor(Colors.Gray)
                                .HorizontalOptions(LayoutOptions.FillAndExpand),
                            Label($"{_summary.New}").FontSize(12).WidthRequest(30)
                        ).Spacing(8)
                    ).Spacing(4)
                ).Padding(0, 8),
                Label($"7d accuracy: {Math.Round(_summary.SuccessRate7d*100)}%")
                    .TextColor(Colors.Gray)
                    .FontSize(12)
                    .HorizontalOptions(LayoutOptions.Center)
            ).Spacing(8).Padding(12)
        ).StrokeThickness(1).Stroke(Colors.LightGray);
    }
}
