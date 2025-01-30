
using CustomLayouts;
using MauiReactor.Parameters;
using ReactorCustomLayouts;

namespace SentenceStudio.Pages.Dashboard;

class DashboardParameters
{
    public VocabularyList SelectedVocabList { get; set; }
    public SkillProfile SelectedSkillProfile { get; set; }
}

class DashboardPageState
{
    public List<VocabularyList> VocabLists { get; set; } = [];
    public List<SkillProfile> SkillProfiles { get; set; } = [];
    
    public int SelectedVocabListIndex { get; set; }
    public int SelectedSkillProfileIndex { get; set; }
}

partial class DashboardPage : Component<DashboardPageState>
{
    [Inject] VocabularyService _vocabService;
    [Inject] SkillProfileRepository _skillService;

    [Param] IParameter<DashboardParameters> _parameters;

    LocalizationManager _localize => LocalizationManager.Instance;

    
    // ContentView scatterView;
    public override VisualNode Render()
	{
        return ContentPage($"{_localize["Dashboard"]}",

            Grid(                
                VScrollView(
                    VStack(
                        ContentView()
                            .Height(600)
                            .Width(800)
                            .IsVisible(false),
                        Grid(
                            new SfTextInputLayout
                                {
                                    Picker()
                                        .ItemsSource(State.VocabLists.Select(_ => _.Name).ToList())
                                        .SelectedIndex(State.SelectedVocabListIndex)
                                        .OnSelectedIndexChanged(index => 
                                        {
                                            State.SelectedVocabListIndex = index;
                                            _parameters.Set(p => p.SelectedVocabList = State.VocabLists[index]);
                                        })
                                }
                                .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                                .ContainerBackground(Colors.White)
                                .Hint("Vocabulary"),
                            new SfTextInputLayout
                                {
                                    Picker()
                                        .ItemsSource(State.SkillProfiles?.Select(p => p.Title).ToList())
                                        .SelectedIndex(State.SelectedSkillProfileIndex)
                                        .OnSelectedIndexChanged(index => 
                                        {
                                            State.SelectedSkillProfileIndex = index;
                                            _parameters.Set(p => p.SelectedSkillProfile = State.SkillProfiles[index]);
                                        })
                                }
                                .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                                .ContainerBackground(Colors.White)
                                .Hint("Skills")
                                .GridColumn(1)
                        ).Columns("*,*").ColumnSpacing(15),
                        
                        Label().Style((Style)Application.Current.Resources["Title1"]).HStart().Text($"{_localize["Activities"]}"),
                        new HWrap(){
                            new ActivityBorder()
                                .LabelText($"{_localize["Warmup"]}")
                                .Route("warmup"),
                            new ActivityBorder().LabelText($"{_localize["Storyteller"]}"),
                            new ActivityBorder().LabelText($"{_localize["Translate"]}"),
                            new ActivityBorder().LabelText($"{_localize["Write"]}"),
                            new ActivityBorder().LabelText($"{_localize["Clozures"]}"),
                            new ActivityBorder().LabelText($"{_localize["HowDoYouSay"]}")                                
                        }.Spacing(20)
                                
                                // new HorizontalWrapLayout
                                // {
                                //     Spacing = (Double)Application.Current.Resources["size320"],

                                //     Children =
                                //     {
                                //         CreateActivityBorder("Warmup", "WarmupCommand"),
                                //         CreateActivityBorder("Storyteller", "NavigateCommand", "storyteller"),
                                //         CreateActivityBorder("Translate", "NavigateCommand", "translation"),
                                //         CreateActivityBorder("Write", "NavigateCommand", "writingLesson"),
                                //         CreateActivityBorder("DescribeAScene", "DescribeASceneCommand"),
                                //         CreateActivityBorder("Clozures", "NavigateCommand", "clozures"),
                                //         CreateActivityBorder("HowDoYouSay", "NavigateCommand", "howDoYouSay")
                                //     }
                                // }
                            
                    )// vstack
                    .Padding((Double)Application.Current.Resources["size160"])
                    .Spacing((Double)Application.Current.Resources["size240"])
                )// vscrollview
            )// grid
                
        ).OnAppearing(LoadOrRefreshDataAsync);// contentpage
    }

    private async Task LoadOrRefreshDataAsync()
    {
        var vocabLists = await _vocabService.GetListsAsync();
        var skills = await _skillService.ListAsync();

        // var listIndex = State.VocabLists.FirstOrDefault(p => p.ID == Props.Task.ProjectID);
        // var profileIndex = State.SkillProfiles.IndexOf(State.SelectedSkillProfile);

        _parameters.Set(p =>{
            p.SelectedVocabList = vocabLists.FirstOrDefault();
            p.SelectedSkillProfile = skills.FirstOrDefault();
        });

        SetState(s => 
        {
            s.VocabLists = vocabLists;
            s.SkillProfiles = skills;
            // s.SelectedVocabListIndex = listIndex;
            // s.SelectedSkillProfileIndex = profileIndex;
        });
    }

    private void NavToWarmup(object sender, EventArgs e)
    {
        // Microsoft.Maui.Controls.Shell.Current.GoToAsync<ActivityProps>(
        //                 nameof(WarmupPage),
        //                 props =>
        //                 {
        //                     props.Vocabulary = State.SelectedVocabList;
        //                     props.Skill = State.SelectedSkillProfile;
        //                 });
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

public partial class ActivityBorder : MauiReactor.Component
    {
        [Prop]
        string _labelText;

        [Prop]
        string _route;

        [Param] IParameter<DashboardParameters> _parameters;


        public override VisualNode Render()
        => Border(
            Grid(
                Label()
                            .VerticalOptions(LayoutOptions.Center)
                            .HorizontalOptions(LayoutOptions.Center)
                            .Text($"{_labelText}")

                    )   
                    .WidthRequest(300)
                    .HeightRequest(120)
            )
            .StrokeShape( Rectangle())
            .StrokeThickness(1)
            .HorizontalOptions(LayoutOptions.Start)
            .OnTapped(async ()=>
            await MauiControls.Shell.Current.GoToAsync<ActivityProps>(
                _route, 
                props => {
                    props.Vocabulary = _parameters.Value.SelectedVocabList;
                    props.Skill = _parameters.Value.SelectedSkillProfile;
                    }
                    )
                    );
    }

class ActivityProps
{
    public VocabularyList Vocabulary { get; set; }
    
    public SkillProfile Skill { get; set; }
}