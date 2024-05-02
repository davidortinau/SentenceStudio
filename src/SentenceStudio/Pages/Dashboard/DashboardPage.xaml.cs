using Microcharts;
using Sharpnado.Tasks;
using SkiaSharp;

namespace SentenceStudio.Pages.Dashboard;

public partial class DashboardPage : ContentPage
{
    private DashboardPageModel _model;

    public DashboardPage(DashboardPageModel model)
	{
		InitializeComponent();

		BindingContext = _model = model;
		TaskMonitor.Create(GetChart);
		
	}

	private async Task GetChart()
	{
        // var r = new Random(18);

		await Task.Delay(1000);

		// chartView.Chart = _model.WritingChart;
		

        // return new LineChart
        // {
        //     LabelOrientation = Orientation.Horizontal,
        //     ValueLabelOrientation = Orientation.Horizontal,
        //     LabelTextSize = 42,
        //     ValueLabelTextSize = 18,
        //     SerieLabelTextSize = 42,
        //     LegendOption = SeriesLegendOption.None,
        //     Series = new List<ChartSerie>()
        //             {
        //                 new ChartSerie()
        //                 {
        //                     Name = "UWP",
        //                     Color = SKColor.Parse("#2c3e50"),
        //                     Entries = GenerateSeriesEntry(r, 4),
        //                 },
        //                 new ChartSerie()
        //                 {
        //                     Name = "Android",
        //                     Color = SKColor.Parse("#77d065"),
        //                     Entries = GenerateSeriesEntry(r, 4),
        //                 },
        //                 new ChartSerie()
        //                 {
        //                     Name = "iOS",
        //                     Color = SKColor.Parse("#b455b6"),
        //                     Entries = GenerateSeriesEntry(r, 4),
        //                 },
        //             }
        // };
	}

    // private static IEnumerable<ChartEntry> GenerateSeriesEntry(Random r, int labelNumber = 3, bool withLabel = true, bool withNulls = false)
    // {
    //     List<ChartEntry> entries = new List<ChartEntry>();

    //     int label = 2020 - ((labelNumber - 1) * 5);
    //     int? value = r.Next(0, 700);
    //     do
    //     {
    //         if (withNulls && (value.Value % 10) == 0) value = null;
    //         entries.Add(new ChartEntry(value) { ValueLabel = value.ToString(), Label = withLabel ? label.ToString() : null });
    //         value = r.Next(0, 700);
    //         label += 5;
    //     }
    //     while (label <= 2020);

    //     return entries;
    // }

	
}

