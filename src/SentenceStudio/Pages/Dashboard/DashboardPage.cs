﻿using MauiReactor.Parameters;
using ReactorCustomLayouts;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Pages.VocabularyQuiz;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Controls;
using SentenceStudio.Pages.Reading;
using MauiReactor.Shapes;
using Microsoft.Maui.Storage;

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
    // Preference keys for storing user selections
    private const string PREF_SELECTED_RESOURCE_IDS = "SelectedResourceIds";
    private const string PREF_SELECTED_SKILL_PROFILE_ID = "SelectedSkillProfileId";

    [Inject] LearningResourceRepository _resourceRepository;
    [Inject] SkillProfileRepository _skillService;

    [Param] IParameter<DashboardParameters> _parameters;

    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
	{
        //Console.Writeline(">> DashboardPage Render <<");
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
                                                .HeightRequest(44)
                                                .BackgroundColor(Colors.Transparent)
                                                .TextColor(ApplicationTheme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground) // doesn't help bc mult selection chips
                                                .TokenItemStyle(ApplicationTheme.ChipStyle)
                                                .ItemPadding(ApplicationTheme.LayoutPadding)
                                                .PlaceholderText("Select resource(s)")
                                                .DropDownBackground(ApplicationTheme.IsLightTheme ? ApplicationTheme.LightSecondaryBackground : ApplicationTheme.DarkSecondaryBackground)
                                                .ItemsSource(State.Resources)
                                                .DisplayMemberPath("Title")
                                                .SelectedItems(State.SelectedResources?.Cast<object>().ToList() ?? new List<object>())
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
                                                        SaveUserSelectionsToPreferences();
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
                                                .HeightRequest(44)
                                                .BackgroundColor(Colors.Transparent)
                                                .TextColor(ApplicationTheme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground)
                                                .ItemPadding(ApplicationTheme.LayoutPadding)
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
                                                        SaveUserSelectionsToPreferences();
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
                                                .SelectedItems(State.SelectedResources?.Cast<object>().ToList() ?? new List<object>())
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
                                                        SaveUserSelectionsToPreferences();
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
                                                        SaveUserSelectionsToPreferences();
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
                            new ActivityBorder().LabelText($"📖 Reading").Route("reading"),
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
        //Console.Writeline(">> DashboardPage OnAppearing <<");
        var resources = await _resourceRepository.GetAllResourcesAsync();
        var skills = await _skillService.ListAsync();

        // Check if we have existing parameter values (from navigation) or load from preferences
        var existingSelectedResources = _parameters.Value?.SelectedResources;
        var existingSelectedSkill = _parameters.Value?.SelectedSkillProfile;

        List<LearningResource> selectedResources;
        SkillProfile selectedSkill;

        if (existingSelectedResources?.Any() == true && existingSelectedSkill != null)
        {
            // Use existing parameter values (e.g., from navigation)
            selectedResources = existingSelectedResources;
            selectedSkill = existingSelectedSkill;
            System.Diagnostics.Debug.WriteLine("🏴‍☠️ Using existing parameter values");
        }
        else
        {
            // Load from preferences or use defaults
            (selectedResources, selectedSkill) = await LoadUserSelectionsFromPreferences(resources, skills);
        }

        // Set the parameter values
        _parameters.Set(p =>
        {
            p.SelectedResources = selectedResources;
            p.SelectedSkillProfile = selectedSkill;
        });

        // Calculate indices for the selected items
        var selectedResourceIndex = -1;
        var selectedSkillIndex = -1;
        
        if (selectedResources?.Any() == true)
        {
            var firstSelected = selectedResources.First();
            for (int i = 0; i < resources.Count; i++)
            {
                if (resources[i].Id == firstSelected.Id)
                {
                    selectedResourceIndex = i;
                    break;
                }
            }
        }
        
        if (selectedSkill != null)
        {
            for (int i = 0; i < skills.Count; i++)
            {
                if (skills[i].Id == selectedSkill.Id)
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
            s.SelectedResources = selectedResources ?? new List<LearningResource>();
            s.SelectedSkillProfileIndex = selectedSkillIndex >= 0 ? selectedSkillIndex : (skills.Any() ? 0 : -1);
            s.SelectedResourceIndex = selectedResourceIndex >= 0 ? selectedResourceIndex : (resources.Any() ? 0 : -1);
        });

        // Debug logging to verify state
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ State set - Selected Resources Count: {State.SelectedResources.Count}");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ State set - Selected Resource Index: {State.SelectedResourceIndex}");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ State set - Selected Skill Index: {State.SelectedSkillProfileIndex}");
        if (State.SelectedResources.Any())
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Selected resource titles: {string.Join(", ", State.SelectedResources.Select(r => r.Title))}");
        }
    }

    /// <summary>
    /// Save the user's current selections to preferences for persistence across app sessions
    /// </summary>
    private void SaveUserSelectionsToPreferences()
    {
        try
        {
            // Save selected resource IDs as a comma-separated string
            if (_parameters.Value?.SelectedResources?.Any() == true)
            {
                var resourceIds = string.Join(",", _parameters.Value.SelectedResources.Select(r => r.Id.ToString()));
                Preferences.Default.Set(PREF_SELECTED_RESOURCE_IDS, resourceIds);
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Saved selected resource IDs to preferences: {resourceIds}");
            }
            else
            {
                Preferences.Default.Remove(PREF_SELECTED_RESOURCE_IDS);
            }

            // Save selected skill profile ID
            if (_parameters.Value?.SelectedSkillProfile != null)
            {
                Preferences.Default.Set(PREF_SELECTED_SKILL_PROFILE_ID, _parameters.Value.SelectedSkillProfile.Id);
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Saved selected skill profile ID to preferences: {_parameters.Value.SelectedSkillProfile.Id}");
            }
            else
            {
                Preferences.Default.Remove(PREF_SELECTED_SKILL_PROFILE_ID);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Error saving preferences: {ex.Message}");
        }
    }

    /// <summary>
    /// Load the user's saved selections from preferences
    /// </summary>
    private async Task<(List<LearningResource> selectedResources, SkillProfile selectedSkill)> LoadUserSelectionsFromPreferences(
        List<LearningResource> availableResources, 
        List<SkillProfile> availableSkills)
    {
        var selectedResources = new List<LearningResource>();
        SkillProfile selectedSkill = null;

        try
        {
            // Load selected resource IDs
            var savedResourceIds = Preferences.Default.Get(PREF_SELECTED_RESOURCE_IDS, string.Empty);
            if (!string.IsNullOrEmpty(savedResourceIds))
            {
                var resourceIds = savedResourceIds.Split(',')
                    .Where(s => int.TryParse(s.Trim(), out _))
                    .Select(s => int.Parse(s.Trim()))
                    .ToList();

                selectedResources = availableResources
                    .Where(r => resourceIds.Contains(r.Id))
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Loaded {selectedResources.Count} selected resources from preferences");
            }

            // Load selected skill profile ID
            var savedSkillId = Preferences.Default.Get(PREF_SELECTED_SKILL_PROFILE_ID, -1);
            if (savedSkillId >= 0)
            {
                selectedSkill = availableSkills.FirstOrDefault(s => s.Id == savedSkillId);
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Loaded selected skill profile from preferences: {selectedSkill?.Title ?? "Not found"}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Error loading preferences: {ex.Message}");
        }

        // Fallback to defaults if no valid saved selections
        if (!selectedResources.Any())
        {
            selectedResources = availableResources.Take(1).ToList();
            System.Diagnostics.Debug.WriteLine("🏴‍☠️ No saved resources found, using default (first resource)");
        }

        if (selectedSkill == null)
        {
            selectedSkill = availableSkills.FirstOrDefault();
            System.Diagnostics.Debug.WriteLine("🏴‍☠️ No saved skill profile found, using default (first skill)");
        }

        return (selectedResources, selectedSkill);
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