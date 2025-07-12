using MauiReactor.Parameters;
using ReactorCustomLayouts;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Controls;

namespace SentenceStudio.Pages.Dashboard;

class DashboardParameters
{
    public List<LearningResource> SelectedResources { get; set; } = new();
    public SkillProfile SelectedSkillProfile { get; set; }
}

class DashboardPageState
{
    public List<LearningResource> Resources { get; set; } = [];
    public List<SkillProfile> SkillProfiles { get; set; } = [];
    
    public List<LearningResource> SelectedResources { get; set; } = [];
    public int SelectedSkillProfileIndex { get; set; }
}

partial class DashboardPage : Component<DashboardPageState>
{
    [Inject] LearningResourceRepository _resourceRepository;
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
                                    new SfComboBox()
                                        .ItemsSource(State.Resources)
                                        .DisplayMemberPath("Title")
                                        .MultiSelectMode(Syncfusion.Maui.Toolkit.ComboBox.ComboBoxMultiSelectMode.Token)
                                        .SelectedItems(State.SelectedResources)
                                        .OnSelectionChanged((object sender, Syncfusion.Maui.Toolkit.ComboBox.ComboBoxSelectionChangedEventArgs e) => 
                                        {
                                            var selectedItems = e.AddedItems?.Cast<LearningResource>().ToList() ?? new List<LearningResource>();
                                            var removedItems = e.RemovedItems?.Cast<LearningResource>().ToList() ?? new List<LearningResource>();
                                            
                                            SetState(s => {
                                                // Remove items that were deselected
                                                foreach (var item in removedItems)
                                                {
                                                    s.SelectedResources.Remove(item);
                                                }
                                                // Add newly selected items
                                                foreach (var item in selectedItems)
                                                {
                                                    if (!s.SelectedResources.Contains(item))
                                                    {
                                                        s.SelectedResources.Add(item);
                                                    }
                                                }
                                            });
                                            
                                            _parameters.Set(p => p.SelectedResources = State.SelectedResources.ToList());
                                        })
                                }
                                .Hint("Learning Resources"),
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
                            new ActivityBorder().LabelText($"{_localize["VocabularyMatchingTitle"]}").Route(nameof(VocabularyMatchingPage)),
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
        var resources = await _resourceRepository.GetAllResourcesAsync();
        var skills = await _skillService.ListAsync();

        _parameters.Set(p =>{
            p.SelectedResources = resources.Take(1).ToList(); // Default to first resource
            p.SelectedSkillProfile = skills.FirstOrDefault();
        });

        SetState(s => 
        {
            s.Resources = resources;
            s.SkillProfiles = skills;
            s.SelectedResources = resources.Take(1).ToList(); // Default to first resource
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
                props.Resources = _parameters.Value.SelectedResources?.ToList() ?? new List<LearningResource>();
                props.Skill = _parameters.Value.SelectedSkillProfile;
            }
        )
    );
}

class ActivityProps
{
    public List<LearningResource> Resources { get; set; } = new();
    public SkillProfile Skill { get; set; }
    
    // Backward compatibility - returns first resource or null
    public LearningResource Resource => Resources?.FirstOrDefault();
}