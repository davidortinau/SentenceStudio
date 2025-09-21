using MauiReactor;
using MauiReactor.Parameters;
using ReactorCustomLayouts;
using MauiReactor.Shapes;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Storage;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Pages.VocabularyQuiz;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Reading;
using SentenceStudio.Services.Progress;
using SentenceStudio; // LocalizationManager

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
    public int SelectedSkillProfileIndex { get; set; } = -1;
    public int SelectedResourceIndex { get; set; } = -1;

    public DisplayOrientation Orientation { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Orientation;
    public double Width { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Width;
    public double Height { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Height;
    public double Density { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Density;

    public VocabProgressSummary? VocabSummary { get; set; }
    public List<ResourceProgress> ResourceProgress { get; set; } = [];
    public SkillProgress? SelectedSkillProgress { get; set; }
    public List<PracticeHeatPoint> PracticeHeat { get; set; } = [];
}

partial class DashboardPage : Component<DashboardPageState>
{
    private const string PREF_SELECTED_RESOURCE_IDS = "SelectedResourceIds";
    private const string PREF_SELECTED_SKILL_PROFILE_ID = "SelectedSkillProfileId";

    [Inject] LearningResourceRepository _resourceRepository;
    [Inject] SkillProfileRepository _skillService;
    [Inject] IProgressService _progressService;

    [Param] IParameter<DashboardParameters> _parameters;

    LocalizationManager _localize => LocalizationManager.Instance;
    private int _progressFetchVersion = 0;

    protected override void OnMounted()
    {
        var info = DeviceDisplay.Current.MainDisplayInfo;
        SetState(s =>
        {
            s.Orientation = info.Orientation;
            s.Width = info.Width;
            s.Height = info.Height;
            s.Density = info.Density;
        });
        DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
        base.OnWillUnmount();
    }

    private void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
    {
        // var info = e.DisplayInfo;
        var info = DeviceDisplay.Current.MainDisplayInfo;

        // SetState triggers a rerender in MauiReactor
        SetState(s =>
        {
            s.Orientation = info.Orientation;
            s.Width = info.Width;
            s.Height = info.Height;
            s.Density = info.Density;
        });
    }

    public override VisualNode Render()
    {
        SafeAreaEdges safeEdges = DeviceDisplay.Current.MainDisplayInfo.Rotation switch
        {
            DisplayRotation.Rotation0 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation90 => new(SafeAreaRegions.All, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation180 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation270 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.All, SafeAreaRegions.None),
            _ => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None)
        };

        return ContentPage($"{_localize["Dashboard"]}",
            Grid(
                VScrollView(
                    VStack(
                        DeviceInfo.Idiom == DeviceIdiom.Phone || (DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density) < 600 ?
                            VStack(spacing: 15,
                                Border(
                                    new SfTextInputLayout{

                                        new SfComboBox()
                                            .MinimumHeightRequest(44)
                                            .BackgroundColor(Colors.Transparent)
                                            .TextColor(MyTheme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground)
                                            .TokenItemStyle(MyTheme.ChipStyle)
                                            .ItemPadding(MyTheme.LayoutPadding)
                                            .PlaceholderText("Select resource(s)")
                                            .DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.Resources)
                                            .DisplayMemberPath("Title")
                                            .SelectedItems(State.SelectedResources?.Cast<object>().ToList() ?? new List<object>())
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Multiple)
                                            .OnSelectionChanged(OnResourcesSelectionChanged)
                                    }.Hint("Learning Resource(s)")
                                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Outlined)
                                ).Padding(MyTheme.Size160, MyTheme.Size80),
                                Border(
                                    VStack(
                                        Label("Skill(s)"),
                                        new SfComboBox()
                                            .HeightRequest(44)
                                            .BackgroundColor(Colors.Transparent)
                                            .TextColor(MyTheme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground)
                                            .ItemPadding(MyTheme.LayoutPadding)
                                            .PlaceholderText("Select skill(s)")
                                            .DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.SkillProfiles)
                                            .DisplayMemberPath("Title")
                                            .SelectedIndex(State.SkillProfiles?.Count > 0 && State.SelectedSkillProfileIndex >= 0 && State.SelectedSkillProfileIndex < State.SkillProfiles.Count ? State.SelectedSkillProfileIndex : -1)
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Single)
                                            .OnSelectionChanged(OnSkillSelectionChanged)
                                    ).Spacing(MyTheme.LayoutSpacing)
                                ).Padding(MyTheme.Size160, MyTheme.Size80)
                            ) : // Desktop
                            Grid(
                                Border(
                                    VStack(
                                        Label("Resource"),
                                        new SfComboBox()
                                            // .HeightRequest(60)
                                            .BackgroundColor(Colors.Transparent)
                                            .ShowBorder(false)
                                            // .MultiSelectionDisplayMode(Syncfusion.Maui.Inputs.ComboBoxMultiSelectionDisplayMode.Token)
                                            .TextColor(MyTheme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground)
                                            .TokenItemStyle(MyTheme.ChipStyle)
                                            .TokensWrapMode(Syncfusion.Maui.Inputs.ComboBoxTokensWrapMode.Wrap)
                                            .EnableAutoSize(true)
                                            // .ItemPadding(MyTheme.LayoutPadding)
                                            .PlaceholderText("Select resource(s)")
                                            // .DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.Resources)
                                            .DisplayMemberPath("Title")
                                            .SelectedItems(State.SelectedResources?.Cast<object>().ToList() ?? new List<object>())
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Multiple)
                                            .OnSelectionChanged(OnResourcesSelectionChanged)
                                    ).Spacing(MyTheme.LayoutSpacing)
                                ).Padding(MyTheme.Size160, MyTheme.Size80),
                                Border(
                                    VStack(
                                        Label("Resource"),
                                        new SfComboBox()
                                            .BackgroundColor(Colors.Transparent)
                                            .ShowBorder(false)
                                            .PlaceholderText("Select skill(s)")
                                            //.DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.SkillProfiles)
                                            .DisplayMemberPath("Title")
                                            .SelectedIndex(State.SkillProfiles?.Count > 0 && State.SelectedSkillProfileIndex >= 0 && State.SelectedSkillProfileIndex < State.SkillProfiles.Count ? State.SelectedSkillProfileIndex : -1)
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Single)
                                            .OnSelectionChanged(OnSkillSelectionChanged)
                                    ).Spacing(MyTheme.LayoutSpacing)
                                ).Padding(MyTheme.Size160, MyTheme.Size80).GridColumn(1)
                            ).Columns("*,*").ColumnSpacing(15),

                        // Progress Section
                        Label("Vocab Progress")
                            .ThemeKey(MyTheme.H1).HStart().Margin(0, 20, 0, 10),
                        FlexLayout(
                            (State.VocabSummary != null
                                ? Border(
                                    new VocabProgressCard()
                                        .Summary(State.VocabSummary)
                                        .IsVisible(true)
                                        .OnSegmentTapped(NavigateToVocabularyProgress)
                                )
                                .StrokeThickness(0)
                                .Set(Microsoft.Maui.Controls.FlexLayout.GrowProperty, 1f)
                                .Set(Microsoft.Maui.Controls.FlexLayout.BasisProperty, new FlexBasis(0, true))
                                .Set(Microsoft.Maui.Controls.FlexLayout.AlignSelfProperty, FlexAlignSelf.Stretch)
                                .Set(View.MinimumWidthRequestProperty, 340d)
                                .Margin(0, 0, 12, 12)
                                : ContentView().HeightRequest(0)
                            ),
                            (State.PracticeHeat?.Any() == true
                                ? Border(
                                    new PracticeStreakCard()
                                        .HeatData(State.PracticeHeat)
                                        .IsVisible(true)
                                )
                                .StrokeThickness(0)
                                .Set(Microsoft.Maui.Controls.FlexLayout.GrowProperty, 1f)
                                .Set(Microsoft.Maui.Controls.FlexLayout.BasisProperty, new FlexBasis(0, true))
                                .Set(Microsoft.Maui.Controls.FlexLayout.AlignSelfProperty, FlexAlignSelf.Stretch)
                                .Set(View.MinimumWidthRequestProperty, 340d)
                                .Margin(0, 0, 12, 12)
                                : ContentView().HeightRequest(0)
                            )
                        )
                        // Always row + wrap; wrapping handles narrow widths. If you prefer vertical stacking earlier than wrap, change threshold below.
                        .Direction((State.Width / State.Density) > 600 ? FlexDirection.Row : FlexDirection.Column)
                        .Wrap(FlexWrap.Wrap)
                        .AlignItems(FlexAlignItems.Stretch)
                        .JustifyContent(FlexJustify.Start)
                        .Padding(0),

                        // Activities
                        Label().ThemeKey(MyTheme.Title1).HStart().Text($"{_localize["Activities"]}"),
                        new HWrap(){
                            new ActivityBorder().LabelText($"{_localize["Warmup"]}").Route("warmup"),
                            new ActivityBorder().LabelText($"{_localize["DescribeAScene"]}").Route(nameof(DescribeAScenePage)),
                            new ActivityBorder().LabelText($"{_localize["Translate"]}").Route(nameof(TranslationPage)),
                            new ActivityBorder().LabelText($"{_localize["Write"]}").Route(nameof(WritingPage)),
                            new ActivityBorder().LabelText($"{_localize["Clozures"]}").Route(nameof(ClozurePage)),
                            new ActivityBorder().LabelText($"{_localize["Reading"]}").Route("reading"),
                            new ActivityBorder().LabelText($"{_localize["VocabularyQuiz"]}").Route(nameof(VocabularyQuizPage)),
                            new ActivityBorder().LabelText($"{_localize["VocabularyMatchingTitle"]}").Route(nameof(VocabularyMatchingPage)),
                            new ActivityBorder().LabelText($"{_localize["Shadowing"]}").Route("shadowing"),
                            new ActivityBorder().LabelText($"{_localize["HowDoYouSay"]}").Route("howdoyousay")
                        }.Spacing(DeviceInfo.Idiom == DeviceIdiom.Phone ? 8 : 20)
                    )
                    .Padding(MyTheme.Size160)
                    .Spacing(MyTheme.Size240)
                )
                .Set(Layout.SafeAreaEdgesProperty, safeEdges)
            )
            .Set(Layout.SafeAreaEdgesProperty, safeEdges)
        )
        .Set(Layout.SafeAreaEdgesProperty, safeEdges)
        .OnAppearing(LoadOrRefreshDataAsync);
    }

    // (Removed duplicate class & render implementation above during consolidation)

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

        // Eagerly refresh progress (awaited) so returning to dashboard shows up-to-date data.
        await RefreshProgressDataAsync(selectedSkill?.Id);

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

    private async Task RefreshProgressDataAsync(int? skillId)
    {
        int myVersion = Interlocked.Increment(ref _progressFetchVersion);
        try
        {
            var vocabFromUtc = DateTime.UtcNow.AddDays(-30);
            var heatFromUtc = DateTime.UtcNow.AddDays(-364);
            var heatToUtc = DateTime.UtcNow;

            var vocabTask = _progressService.GetVocabSummaryAsync(vocabFromUtc);
            var resourceTask = _progressService.GetRecentResourceProgressAsync(vocabFromUtc, 3);
            var heatTask = _progressService.GetPracticeHeatAsync(heatFromUtc, heatToUtc); // full year for heat map
            Task<SentenceStudio.Services.Progress.SkillProgress?> skillTask = Task.FromResult<SentenceStudio.Services.Progress.SkillProgress?>(null);
            if (skillId.HasValue)
            {
                skillTask = _progressService.GetSkillProgressAsync(skillId.Value);
            }

            await Task.WhenAll(vocabTask, resourceTask, heatTask, skillTask);

            // If a newer request started meanwhile, abandon these results
            if (myVersion != _progressFetchVersion) return;

            SetState(st =>
            {
                st.VocabSummary = vocabTask.Result;
                st.ResourceProgress = resourceTask.Result;
                st.SelectedSkillProgress = skillTask.Result;
                st.PracticeHeat = heatTask.Result.ToList();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Progress data load error: {ex.Message}");
        }
    }

    // Selection handlers that are resilient to chip removals (e.AddedItems can be null)
    private void OnResourcesSelectionChanged(object sender, Syncfusion.Maui.Inputs.SelectionChangedEventArgs e)
    {
        try
        {
            var combo = sender as Syncfusion.Maui.Inputs.SfComboBox;
            var selected = combo?.SelectedItems?.OfType<LearningResource>().ToList() ?? new List<LearningResource>();

            SetState(s =>
            {
                s.SelectedResources = selected;
                s.SelectedResourceIndex = selected.Any() ? State.Resources.IndexOf(selected.First()) : -1;
            });

            _parameters.Set(p => p.SelectedResources = selected);
            SaveUserSelectionsToPreferences();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnResourcesSelectionChanged error: {ex.Message}");
        }
    }

    private void OnSkillSelectionChanged(object sender, Syncfusion.Maui.Inputs.SelectionChangedEventArgs e)
    {
        try
        {
            var combo = sender as Syncfusion.Maui.Inputs.SfComboBox;
            // Single selection mode: use SelectedItem
            var selectedProfile = combo?.SelectedItem as SkillProfile;
            var index = selectedProfile != null ? State.SkillProfiles.IndexOf(selectedProfile) : -1;

            SetState(s => s.SelectedSkillProfileIndex = index);
            _parameters.Set(p => p.SelectedSkillProfile = selectedProfile);
            SaveUserSelectionsToPreferences();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnSkillSelectionChanged error: {ex.Message}");
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

    private void NavigateToVocabularyProgress(VocabularyFilterType filterType)
    {
        // Invoke on main thread (selection event should already be on UI thread, this is extra safety)
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = MauiControls.Shell.Current.GoToAsync<VocabularyProgressProps>(
                    nameof(VocabularyLearningProgressPage),
                    props =>
                    {
                        props.InitialFilter = filterType;
                        props.Title = $"Vocabulary Progress - {filterType}";
                    });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            }
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
            .WidthRequest(DeviceInfo.Idiom == DeviceIdiom.Phone ? 140 : 200)
            .HeightRequest(DeviceInfo.Idiom == DeviceIdiom.Phone ? 60 : 80)
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