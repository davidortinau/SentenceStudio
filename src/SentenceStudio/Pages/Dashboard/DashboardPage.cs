using MauiReactor.Parameters;
using ReactorCustomLayouts;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.Writing;

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
                                .Hint("Skills")
                                .GridColumn(1)
                        ).Columns("*,*").ColumnSpacing(15),
                        
                        Label().Style((Style)Application.Current.Resources["Title1"]).HStart().Text($"{_localize["Activities"]}"),
                        new HWrap(){
                            new ActivityBorder()
                                .LabelText($"{_localize["Warmup"]}")
                                .Route("warmup"),
                            new ActivityBorder().LabelText($"{_localize["DescribeAScene"]}").Route(nameof(DescribeAScenePage)),
                            new ActivityBorder().LabelText($"{_localize["Translate"]}").Route(nameof(TranslationPage)),
                            new ActivityBorder().LabelText($"{_localize["Write"]}").Route(nameof(WritingPage)),
                            new ActivityBorder().LabelText($"{_localize["Clozures"]}").Route(nameof(ClozurePage)),
                            new ActivityBorder().LabelText($"{_localize["Shadowing"]}").Route("shadowing"),
                            new ActivityBorder().LabelText($"{_localize["HowDoYouSay"]}").Route("howdoyousay")                                
                        }.Spacing(20)
                    )// vstack
                    .Padding((Double)Application.Current.Resources["size160"])
                    .Spacing(ApplicationTheme.Size240)
                )// vscrollview
            )// grid
                
        ).OnAppearing(LoadOrRefreshDataAsync);// contentpage
    }

    async Task LoadOrRefreshDataAsync()
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
}

public partial class ActivityBorder : MauiReactor.Component
{
    [Prop]
    string _labelText;

    [Prop]
    string _route;

    [Param] IParameter<DashboardParameters> _parameters;


    public override VisualNode Render() =>
        Border(
            Grid(
                Label()
                            .VerticalOptions(LayoutOptions.Center)
                            .HorizontalOptions(LayoutOptions.Center)
                            .Text($"{_labelText}")

                    )
                    .WidthRequest(300)
                    .HeightRequest(120)
            )
            .StrokeShape(Rectangle())
            .StrokeThickness(1)
            .HorizontalOptions(LayoutOptions.Start)
            .OnTapped(async () =>
            await MauiControls.Shell.Current.GoToAsync<ActivityProps>(
                _route,
                props =>
                {
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