using MauiReactor.Parameters;
using ReactorCustomLayouts;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Pages.VocabularyQuiz;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Controls;
using MauiReactor.Shapes;

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
    public int SelectedSkillProfileIndex { get; set; } = -1; // Initialize to -1 (no selection)
    public int SelectedResourceIndex { get; set; } = -1; // Initialize to -1 (no selection)
}

partial class DashboardPage : Component<DashboardPageState>
{
    [Inject] LearningResourceRepository _resourceRepository;
    [Inject] SkillProfileRepository _skillService;

    [Param] IParameter<DashboardParameters> _parameters;

    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
	{
        Console.WriteLine(">> DashboardPage Render <<");
        return ContentPage($"{_localize["Dashboard"]}",

            Grid(
                VScrollView(
                    VStack(
                        ContentView()
                            .Height(600)
                            .Width(800)
                            .IsVisible(false),
                        // Responsive layout: horizontal on wide screens, vertical on narrow screens
                        DeviceInfo.Idiom == DeviceIdiom.Phone || DeviceDisplay.MainDisplayInfo.Width < 600 ?
                            // Vertical layout for narrow screens
                            VStack(spacing: 15,
                                Border
                                    (
                                        VStack(
                                            Label()
                                                .Text("Learning Resource(s)"),
                                            new SfComboBox()
                                                .BackgroundColor(Colors.Transparent)
                                                .PlaceholderText("Select resource(s)")
                                                .DropDownBackground(ApplicationTheme.IsLightTheme ? ApplicationTheme.LightSecondaryBackground : ApplicationTheme.DarkSecondaryBackground)
                                                .ItemsSource(State.Resources)
                                                .DisplayMemberPath("Title")
                                                .SelectedIndex(State.Resources?.Count > 0 && State.SelectedResourceIndex >= 0 && State.SelectedResourceIndex < State.Resources.Count ? State.SelectedResourceIndex : -1)
                                                .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Multiple)
                                                .OnSelectionChanged((sender, e) =>
                                                {
                                                    if (e.AddedItems?.Cast<LearningResource>().ToList() is var selectedResources && selectedResources.Any())
                                                    {
                                                        SetState(s =>
                                                        {
                                                            s.SelectedResources = selectedResources;
                                                            s.SelectedResourceIndex = State.Resources.IndexOf(selectedResources.FirstOrDefault());
                                                        });
                                                        _parameters.Set(p => p.SelectedResources = selectedResources.ToList());
                                                    }
                                                })
                                        ).Spacing(ApplicationTheme.LayoutSpacing)
                                    ).Padding(ApplicationTheme.Size160, ApplicationTheme.Size80),
                                Border
                                    (
                                        VStack(
                                            Label()
                                                .Text("Skill(s)"),
                                            new SfComboBox()
                                                .BackgroundColor(Colors.Transparent)
                                                .PlaceholderText("Select skill(s)")
                                                .DropDownBackground(ApplicationTheme.IsLightTheme ? ApplicationTheme.LightSecondaryBackground : ApplicationTheme.DarkSecondaryBackground)
                                                .ItemsSource(State.SkillProfiles)
                                                .DisplayMemberPath("Title")
                                                .SelectedIndex(State.SkillProfiles?.Count > 0 && State.SelectedSkillProfileIndex >= 0 && State.SelectedSkillProfileIndex < State.SkillProfiles.Count ? State.SelectedSkillProfileIndex : -1)
                                                .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Single)
                                                .OnSelectionChanged((sender, e) =>
                                                {
                                                    if (e.AddedItems?.FirstOrDefault() is SkillProfile selectedProfile)
                                                    {
                                                        var index = State.SkillProfiles.IndexOf(selectedProfile);
                                                        SetState(s => s.SelectedSkillProfileIndex = index);
                                                        _parameters.Set(p => p.SelectedSkillProfile = selectedProfile);
                                                    }
                                                })
                                        ).Spacing(ApplicationTheme.LayoutSpacing)
                                    ).Padding(ApplicationTheme.Size160, ApplicationTheme.Size80)
                            ) :
                            // Horizontal layout for wide screens
                            Grid(
                                Border
                                    (
                                        VStack(
                                            Label()
                                                .Text("Learning Resource(s)"),
                                            new SfComboBox()
                                                .BackgroundColor(Colors.Transparent)
                                                .PlaceholderText("Select resource(s)")
                                                .DropDownBackground(ApplicationTheme.IsLightTheme ? ApplicationTheme.LightSecondaryBackground : ApplicationTheme.DarkSecondaryBackground)
                                                .ItemsSource(State.Resources)
                                                .DisplayMemberPath("Title")
                                                .SelectedIndex(State.Resources?.Count > 0 && State.SelectedResourceIndex >= 0 && State.SelectedResourceIndex < State.Resources.Count ? State.SelectedResourceIndex : -1)
                                                .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Multiple)
                                                .OnSelectionChanged((sender, e) =>
                                                {
                                                    if (e.AddedItems?.Cast<LearningResource>().ToList() is var selectedResources && selectedResources.Any())
                                                    {
                                                        SetState(s =>
                                                        {
                                                            s.SelectedResources = selectedResources;
                                                            s.SelectedResourceIndex = State.Resources.IndexOf(selectedResources.FirstOrDefault());
                                                        });
                                                        _parameters.Set(p => p.SelectedResources = selectedResources.ToList());
                                                    }
                                                })
                                        ).Spacing(ApplicationTheme.LayoutSpacing)
                                    ).Padding(ApplicationTheme.Size160, ApplicationTheme.Size80),
                                Border
                                    (
                                        VStack(
                                            Label()
                                                .Text("Skill(s)"),
                                            new SfComboBox()
                                                .BackgroundColor(Colors.Transparent)
                                                .PlaceholderText("Select skill(s)")
                                                .DropDownBackground(ApplicationTheme.IsLightTheme ? ApplicationTheme.LightSecondaryBackground : ApplicationTheme.DarkSecondaryBackground)
                                                .ItemsSource(State.SkillProfiles)
                                                .DisplayMemberPath("Title")
                                                .SelectedIndex(State.SkillProfiles?.Count > 0 && State.SelectedSkillProfileIndex >= 0 && State.SelectedSkillProfileIndex < State.SkillProfiles.Count ? State.SelectedSkillProfileIndex : -1)
                                                .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Single)
                                                .OnSelectionChanged((sender, e) =>
                                                {
                                                    if (e.AddedItems?.FirstOrDefault() is SkillProfile selectedProfile)
                                                    {
                                                        var index = State.SkillProfiles.IndexOf(selectedProfile);
                                                        SetState(s => s.SelectedSkillProfileIndex = index);
                                                        _parameters.Set(p => p.SelectedSkillProfile = selectedProfile);
                                                    }
                                                })
                                        ).Spacing(ApplicationTheme.LayoutSpacing)
                                    )
                                    .Padding(ApplicationTheme.Size160, ApplicationTheme.Size80)
                                    .GridColumn(1)
                            ).Columns("*,*").ColumnSpacing(15),

                        Label()
                            .ThemeKey(ApplicationTheme.Title1).HStart().Text($"{_localize["Activities"]}"),
                        new HWrap(){
                            new ActivityBorder()
                                .LabelText($"{_localize["Warmup"]}")
                                .Route("warmup"),
                            new ActivityBorder().LabelText($"{_localize["DescribeAScene"]}").Route(nameof(DescribeAScenePage)),
                            new ActivityBorder().LabelText($"{_localize["Translate"]}").Route(nameof(TranslationPage)),
                            new ActivityBorder().LabelText($"{_localize["Write"]}").Route(nameof(WritingPage)),
                            new ActivityBorder().LabelText($"{_localize["Clozures"]}").Route(nameof(ClozurePage)),
                            new ActivityBorder().LabelText($"{_localize["VocabularyQuiz"]}").Route(nameof(VocabularyQuizPage)),
                            new ActivityBorder().LabelText($"{_localize["VocabularyMatchingTitle"]}").Route(nameof(VocabularyMatchingPage)),
                            new ActivityBorder().LabelText($"{_localize["Shadowing"]}").Route("shadowing"),
                            new ActivityBorder().LabelText($"{_localize["HowDoYouSay"]}").Route("howdoyousay")
                        }.Spacing(20)
                    )// vstack
                    .Padding(ApplicationTheme.Size160)
                    .Spacing(ApplicationTheme.Size240)
                )// vscrollview
            )// grid

        ).OnAppearing(LoadOrRefreshDataAsync);// contentpage
    }

    async Task LoadOrRefreshDataAsync()
    {
        Console.WriteLine(">> DashboardPage OnAppearing <<");
        var resources = await _resourceRepository.GetAllResourcesAsync();
        var skills = await _skillService.ListAsync();

        // Only set defaults if no previous selections exist
        var existingSelectedResources = _parameters.Value?.SelectedResources;
        var existingSelectedSkill = _parameters.Value?.SelectedSkillProfile;

        _parameters.Set(p =>{
            // Preserve existing selections, or set defaults if none exist
            p.SelectedResources = existingSelectedResources?.Any() == true ? existingSelectedResources : resources.Take(1).ToList();
            p.SelectedSkillProfile = existingSelectedSkill ?? skills.FirstOrDefault();
        });

        // Calculate indices for the selected items
        var selectedResourceIndex = -1;
        var selectedSkillIndex = -1;
        
        if (_parameters.Value.SelectedResources?.Any() == true)
        {
            var firstSelected = _parameters.Value.SelectedResources.First();
            for (int i = 0; i < resources.Count; i++)
            {
                if (resources[i].Id == firstSelected.Id)
                {
                    selectedResourceIndex = i;
                    break;
                }
            }
        }
        
        if (_parameters.Value.SelectedSkillProfile != null)
        {
            for (int i = 0; i < skills.Count; i++)
            {
                if (skills[i].Id == _parameters.Value.SelectedSkillProfile.Id)
                {
                    selectedSkillIndex = i;
                    break;
                }
            }
        }

        SetState(s => 
        {
            s.Resources = resources;
            s.SkillProfiles = skills;
            s.SelectedResources = _parameters.Value.SelectedResources ?? new List<LearningResource>();
            s.SelectedSkillProfileIndex = selectedSkillIndex >= 0 ? selectedSkillIndex : (skills.Any() ? 0 : -1);
            s.SelectedResourceIndex = selectedResourceIndex >= 0 ? selectedResourceIndex : (resources.Any() ? 0 : -1);
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
        {
            // 🏴‍☠️ Validate that we have the required selections before navigating
            if (_parameters.Value.SelectedResources?.Any() != true)
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Ahoy!", 
                    "Ye need to select at least one learning resource before startin' this activity, matey!", 
                    "Aye, Captain!");
                return;
            }
            
            if (_parameters.Value.SelectedSkillProfile == null)
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Avast!", 
                    "Choose yer skill profile first, ye scallywag!", 
                    "Aye, Captain!");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ ActivityBorder: Navigating to {_route} with {_parameters.Value.SelectedResources.Count} resources and skill '{_parameters.Value.SelectedSkillProfile.Title}'");
            
            await MauiControls.Shell.Current.GoToAsync<ActivityProps>(
                _route,
                props =>
                {
                    props.Resources = _parameters.Value.SelectedResources?.ToList() ?? new List<LearningResource>();
                    props.Skill = _parameters.Value.SelectedSkillProfile;
                }
            );
        });
}

class ActivityProps
{
    public List<LearningResource> Resources { get; set; } = new();
    public SkillProfile Skill { get; set; }
    
    // Backward compatibility - returns first resource or null
    public LearningResource Resource => Resources?.FirstOrDefault();
}