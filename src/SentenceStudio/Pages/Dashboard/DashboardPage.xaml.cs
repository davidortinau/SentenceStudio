
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Maui.Skia;
using OxyPlot.Series;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Dashboard;

public partial class DashboardPage : ContentPage
{
    private DashboardPageModel _model;

    public DashboardPage(DashboardPageModel model)
	{
		InitializeComponent();

		BindingContext = _model = model;
				
	}

override protected void OnAppearing()
{
	base.OnAppearing();
	TaskMonitor.Create(SetupScatterChart);
}
	

    private async Task SetupScatterChart()
    {
        // Create a PlotModel
            var plotModel = new PlotModel { Title = "Trend of Accuracy Over Time" };

            // Create and configure the scatter series
            var scatterSeries = new ScatterSeries { MarkerType = MarkerType.Circle };

			var activities = await _model.GetWritingActivity();

            // Add data points to the series
			var dataPoints = new List<CustomDataPoint>();
			var groupedActivities = activities.GroupBy(a => new { a.CreatedAt.Date, a.Accuracy });

try{
			foreach (var group in groupedActivities)
			{
				var count = group.Count();
				dataPoints.Add(new CustomDataPoint(group.Key.Date.ToString("MM-dd"), group.Key.Accuracy, count));
			}
}catch(Exception ex){
	Debug.WriteLine(ex.Message);
}

            // Add points to the scatter series
            foreach (var dataPoint in dataPoints)
            {
                scatterSeries.Points.Add(new ScatterPoint(DateTimeAxis.ToDouble(DateTime.ParseExact(dataPoint.XValue, "MM-dd", null)), 
                                                          dataPoint.YValue, 
                                                          dataPoint.Size * 5));
            }

            // Configure the axes
            plotModel.Axes.Add(new DateTimeAxis { 
				Position = AxisPosition.Bottom, 
				StringFormat = "MM-dd", 
				Title = "Date (MM-DD)", 
				MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot
			});
            plotModel.Axes.Add(new LinearAxis { 
				Position = AxisPosition.Left, 
				Title = "Accuracy Scores",
				MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
				Minimum = 0,   // Set the minimum value of Y-axis
                Maximum = 100  // Set the maximum value of Y-axis 
			});

            // Add the scatter series to the PlotModel
            plotModel.Series.Add(scatterSeries);

            // Create a PlotView and set its model
            var plotView = new PlotView
            {
                Model = plotModel,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill
            };

			this.Dispatcher.Dispatch(() => ScatterView.Content = plotView);
			
    }
	public class CustomDataPoint
    {
        public string XValue { get; set; }
        public double YValue { get; set; }
        public double Size { get; set; }

        public CustomDataPoint(string xValue, double yValue, double size)
        {
            XValue = xValue;
            YValue = yValue;
            Size = size;
        }
    }
}

