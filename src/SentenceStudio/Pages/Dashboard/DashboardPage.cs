
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Maui.Skia;
using OxyPlot.Series;
using Sharpnado.Tasks;
using CustomLayouts;

namespace SentenceStudio.Pages.Dashboard;

public class DashboardPage : ContentPage
{
    private DashboardPageModel _model;

    public DashboardPage(DashboardPageModel model)
	{
		BindingContext = _model = model;

        Build();
				
	}

    // ContentView scatterView;
    public void Build()
    {

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Star }
            },

            Children =
            {
                new ScrollView
                {
                    Content = new VerticalStackLayout
                    {
                        Padding = (Double)Application.Current.Resources["size160"],
                        Spacing = (Double)Application.Current.Resources["size240"],

                        Children =
                        {
                            new ContentView
                            {
                                HeightRequest = 600,
                                WidthRequest = 800,
                                IsVisible = false
                            },

                            new FormField
                            {
                                ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                                Content = new Picker()
                                    .Bind(Picker.SelectedItemProperty, "VocabList", BindingMode.TwoWay)
                                    .Bind(Picker.ItemsSourceProperty, "VocabLists")
                            }.Bind(FormField.FieldLabelProperty, "Localize[DefaultVocabulary]"),

                            new FormField
                            {
                                ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                                Content = new Picker()
                                    .Bind(Picker.SelectedItemProperty, "SkillProfile", BindingMode.TwoWay)
                                    .Bind(Picker.ItemsSourceProperty, "SkillProfiles")
                            }.Bind(FormField.FieldLabelProperty, "Localize[DefaultSkill]"),

                            new Label
                            {
                                Style = (Style)Application.Current.Resources["Title1"],
                                HorizontalOptions = LayoutOptions.Start
                            }.Bind(Label.TextProperty, "Localize[Activities]"),

                            new HorizontalWrapLayout
                            {
                                Spacing = (Double)Application.Current.Resources["size320"],

                                Children =
                                {
                                    CreateActivityBorder("Warmup", "WarmupCommand"),
                                    CreateActivityBorder("Storyteller", "NavigateCommand", "storyteller"),
                                    CreateActivityBorder("Translate", "DefaultTranslateCommand"),
                                    CreateActivityBorder("Write", "DefaultWriteCommand"),
                                    CreateActivityBorder("DescribeAScene", "DescribeASceneCommand"),
                                    CreateActivityBorder("Clozures", "NavigateCommand", "clozures"),
                                    CreateActivityBorder("HowDoYouSay", "NavigateCommand", "howDoYouSay")
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private Border CreateActivityBorder(string labelText, string commandName, string commandParameter = null)
    {
        var border = new Border
        {
            StrokeShape = new Rectangle(),
            StrokeThickness = 1,
            HorizontalOptions = LayoutOptions.Start,
            Content = new Grid
            {
                WidthRequest = 300,
                HeightRequest = 120,
                Children =
                {
                    new Label
                    {
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalOptions = LayoutOptions.Center
                    }.Bind(Label.TextProperty, $"Localize[{labelText}]")
                }
            }
        };

        var tapGestureRecognizer = new TapGestureRecognizer();
        tapGestureRecognizer.SetBinding(TapGestureRecognizer.CommandProperty, commandName);
        if (commandParameter != null)
        {
            tapGestureRecognizer.CommandParameter = commandParameter;
        }

        ((Grid)border.Content).GestureRecognizers.Add(tapGestureRecognizer);

        return border;
    }
	

//     private async Task SetupScatterChart()
//     {
//         // Create a PlotModel
//             var plotModel = new PlotModel { Title = "Trend of Accuracy Over Time" };

//             // Create and configure the scatter series
//             var scatterSeries = new ScatterSeries { MarkerType = MarkerType.Circle };

// 			var activities = await _model.GetWritingActivity();

//             // Add data points to the series
// 			var dataPoints = new List<CustomDataPoint>();
// 			var groupedActivities = activities.GroupBy(a => new { a.CreatedAt.Date, a.Accuracy });

// try{
// 			foreach (var group in groupedActivities)
// 			{
// 				var count = group.Count();
// 				dataPoints.Add(new CustomDataPoint(group.Key.Date.ToString("MM-dd"), group.Key.Accuracy, count));
// 			}
// }catch(Exception ex){
// 	Debug.WriteLine(ex.Message);
// }

//             // Add points to the scatter series
//             foreach (var dataPoint in dataPoints)
//             {
//                 scatterSeries.Points.Add(new ScatterPoint(DateTimeAxis.ToDouble(DateTime.ParseExact(dataPoint.XValue, "MM-dd", null)), 
//                                                           dataPoint.YValue, 
//                                                           dataPoint.Size * 5));
//             }

//             // Configure the axes
//             plotModel.Axes.Add(new DateTimeAxis { 
// 				Position = AxisPosition.Bottom, 
// 				StringFormat = "MM-dd", 
// 				Title = "Date (MM-DD)", 
// 				MajorGridlineStyle = LineStyle.Solid,
//                 MinorGridlineStyle = LineStyle.Dot
// 			});
//             plotModel.Axes.Add(new LinearAxis { 
// 				Position = AxisPosition.Left, 
// 				Title = "Accuracy Scores",
// 				MajorGridlineStyle = LineStyle.Solid,
//                 MinorGridlineStyle = LineStyle.Dot,
// 				Minimum = 0,   // Set the minimum value of Y-axis
//                 Maximum = 100  // Set the maximum value of Y-axis 
// 			});

//             // Add the scatter series to the PlotModel
//             plotModel.Series.Add(scatterSeries);

//             // Create a PlotView and set its model
//             var plotView = new PlotView
//             {
//                 Model = plotModel,
//                 VerticalOptions = LayoutOptions.Fill,
//                 HorizontalOptions = LayoutOptions.Fill
//             };

// 			this.Dispatcher.Dispatch(() => ScatterView.Content = plotView);
			
//     }
// 	public class CustomDataPoint
//     {
//         public string XValue { get; set; }
//         public double YValue { get; set; }
//         public double Size { get; set; }

//         public CustomDataPoint(string xValue, double yValue, double size)
//         {
//             XValue = xValue;
//             YValue = yValue;
//             Size = size;
//         }
//     }
}

