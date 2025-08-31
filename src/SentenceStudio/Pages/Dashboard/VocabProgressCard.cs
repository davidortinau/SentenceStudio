using MauiReactor;
using SentenceStudio.Services.Progress;
using Syncfusion.Maui.Charts;

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
        if (total == 0) return ContentView(); // No data to display

        // Create data for donut chart
        var chartData = new List<VocabChartData>
        {
            new VocabChartData("Known", _summary.Known, Colors.Green),
            new VocabChartData("Review", _summary.Review, Colors.Orange),
            new VocabChartData("Learning", _summary.Learning, Colors.Blue),
            new VocabChartData("New", _summary.New, Colors.Gray)
        }.Where(x => x.Value > 0).ToList(); // Only show segments with data

        return Border(
            VStack(
                Label("Vocabulary Progress").FontSize(16).FontAttributes(FontAttributes.Bold),

                // Donut chart using MauiReactor ContentView
                new MauiReactor.ContentView<Microsoft.Maui.Controls.ContentView>((contentView) =>
                {
                    contentView.Content = CreateDonutChart(chartData, total);
                })
                .HeightRequest(200),

                Label($"7d accuracy: {Math.Round(_summary.SuccessRate7d * 100)}%")
                    .TextColor(Colors.Gray)
                    .FontSize(12)
                    .HorizontalOptions(LayoutOptions.Center)
            ).Spacing(8).Padding(12)
        ).StrokeThickness(1).Stroke(Colors.LightGray);
    }

    private Microsoft.Maui.Controls.View CreateDonutChart(List<VocabChartData> chartData, int total)
    {
        var chart = new Syncfusion.Maui.Charts.SfCircularChart();
        chart.HeightRequest = 200;

        // Enable the built-in legend
        chart.Legend = new Syncfusion.Maui.Charts.ChartLegend()
        {
            IsVisible = true
        };

        var series = new Syncfusion.Maui.Charts.DoughnutSeries()
        {
            ItemsSource = chartData,
            XBindingPath = nameof(VocabChartData.Category),
            YBindingPath = nameof(VocabChartData.Value),
            PaletteBrushes = chartData.Select(x => new SolidColorBrush(x.Color)).Cast<Microsoft.Maui.Controls.Brush>().ToList(),
            ShowDataLabels = true,
            InnerRadius = 0.7
        };

        // Configure data labels to show outside the chart
        series.DataLabelSettings = new Syncfusion.Maui.Charts.CircularDataLabelSettings()
        {
            LabelPosition = Syncfusion.Maui.Charts.ChartDataLabelPosition.Outside
        };

        // Use the built-in CenterView property on the DoughnutSeries
        series.CenterView = new Microsoft.Maui.Controls.StackLayout
        {
            Orientation = Microsoft.Maui.Controls.StackOrientation.Vertical,
            HorizontalOptions = Microsoft.Maui.Controls.LayoutOptions.Center,
            VerticalOptions = Microsoft.Maui.Controls.LayoutOptions.Center,
            Children =
            {
                new Microsoft.Maui.Controls.Label
                {
                    Text = total.ToString(),
                    FontSize = 24,
                    FontAttributes = Microsoft.Maui.Controls.FontAttributes.Bold,
                    HorizontalOptions = Microsoft.Maui.Controls.LayoutOptions.Center,
                    TextColor = Microsoft.Maui.Graphics.Colors.Black
                },
                new Microsoft.Maui.Controls.Label
                {
                    Text = "total words",
                    FontSize = 12,
                    TextColor = Microsoft.Maui.Graphics.Colors.Gray,
                    HorizontalOptions = Microsoft.Maui.Controls.LayoutOptions.Center
                }
            }
        };

        chart.Series.Add(series);
        return chart;
    }
}

// Data model for chart
public class VocabChartData
{
    public string Category { get; set; }
    public int Value { get; set; }
    public Color Color { get; set; }

    public VocabChartData(string category, int value, Color color)
    {
        Category = category;
        Value = value;
        Color = color;
    }
}
