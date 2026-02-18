using SentenceStudio.Services.Progress;
using SentenceStudio.Pages.VocabularyProgress;

namespace SentenceStudio.Pages.Dashboard;

public partial class VocabProgressCard : Component
{
    [Prop]
    private VocabProgressSummary _summary;

    [Prop]
    private bool _isVisible;

    [Prop]
    private Action<VocabularyFilterType>? _onSegmentTapped;

    private List<VocabChartData> _chartData = new(); // stored only to map selection indexes

    public override VisualNode Render()
    {
        if (!_isVisible) return ContentView();

        var theme = BootstrapTheme.Current;
        var total = _summary.New + _summary.Learning + _summary.Review + _summary.Known;
        if (total == 0) return ContentView(); // No data to display

        // Create data for donut chart
        var chartData = new List<VocabChartData>
        {
            new VocabChartData("Known", _summary.Known, theme.Success),
            new VocabChartData("Review", _summary.Review, theme.Warning),
            new VocabChartData("Learning", _summary.Learning, theme.Info),
            new VocabChartData("New", _summary.New, theme.GetOutline())
        }.Where(x => x.Value > 0).ToList(); // Only show segments with data

        _chartData = chartData; // keep for selection mapping

        return
            VStack(
                Label("Vocabulary Progress").H5(),

                BuildDonutChart(chartData, total),

                Label($"7d accuracy: {Math.Round(_summary.SuccessRate7d * 100)}%")
                    .Muted()
                    .FontSize(12)
                    .HCenter()
            ).Spacing(8).Padding(16);
    }

    private VisualNode BuildDonutChart(List<VocabChartData> chartData, int total)
    {
        return new MauiReactor.ContentView<Microsoft.Maui.Controls.ContentView>(cv =>
        {
            var chart = new Syncfusion.Maui.Charts.SfCircularChart
            {
                Legend = new Syncfusion.Maui.Charts.ChartLegend { IsVisible = true },
                HeightRequest = 200
            };

            var series = new Syncfusion.Maui.Charts.DoughnutSeries
            {
                ItemsSource = chartData,
                XBindingPath = nameof(VocabChartData.Category),
                YBindingPath = nameof(VocabChartData.Value),
                PaletteBrushes = chartData.Select(x => new SolidColorBrush(x.Color)).Cast<Microsoft.Maui.Controls.Brush>().ToList(),
                ShowDataLabels = true,
                InnerRadius = 0.7,
                DataLabelSettings = new Syncfusion.Maui.Charts.CircularDataLabelSettings
                {
                    LabelPosition = Syncfusion.Maui.Charts.ChartDataLabelPosition.Outside
                },
                CenterView = new Microsoft.Maui.Controls.VerticalStackLayout
                {
                    Spacing = 0,
                    HorizontalOptions = Microsoft.Maui.Controls.LayoutOptions.Center,
                    VerticalOptions = Microsoft.Maui.Controls.LayoutOptions.Center,
                    Children =
                    {
                        new Microsoft.Maui.Controls.Label
                        {
                            Text = total.ToString(),
                            FontSize = 24,
                            FontAttributes = Microsoft.Maui.Controls.FontAttributes.Bold,
                            HorizontalOptions = Microsoft.Maui.Controls.LayoutOptions.Center
                        },
                        new Microsoft.Maui.Controls.Label
                        {
                            Text = "total words",
                            FontSize = 12,
                            TextColor = BootstrapTheme.Current.Muted,
                            HorizontalOptions = Microsoft.Maui.Controls.LayoutOptions.Center
                        }
                    }
                }
            };

            if (_onSegmentTapped != null)
            {
                var sel = new Syncfusion.Maui.Charts.DataPointSelectionBehavior();
                sel.SelectionChanged += (s, e) =>
                {
                    if (_onSegmentTapped == null || !_isVisible) return;
                    var prop = e.GetType().GetProperty("NewIndexes");
                    if (prop?.GetValue(e) is List<int> list && list.Count > 0)
                    {
                        var idx = list[0];
                        if (idx >= 0 && idx < _chartData.Count)
                        {
                            var filter = MapCategoryToFilter(_chartData[idx].Category);
                            if (filter.HasValue)
                                _onSegmentTapped(filter.Value);
                        }
                    }
                };
                series.SelectionBehavior = sel;
            }

            chart.Series.Add(series);
            cv.Content = chart;
        }).HeightRequest(200);
    }

    private static VocabularyFilterType? MapCategoryToFilter(string category)
    {
        return category switch
        {
            "Known" => VocabularyFilterType.Known,
            "Learning" => VocabularyFilterType.Learning,
            "Review" => VocabularyFilterType.Learning, // Map Review to Learning filter
            "New" => VocabularyFilterType.Unknown,
            _ => null
        };
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
