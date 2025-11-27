using MauiReactor.Shapes;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Helpers;

namespace SentenceStudio.Pages.LearningResources;

class ListLearningResourcesState
{
    public List<LearningResource> Resources { get; set; } = [];
    public bool IsLoading { get; set; } = false;
    public string SearchText { get; set; } = string.Empty;
    public string FilterType { get; set; } = "All";
    public string FilterLanguage { get; set; } = "All";
    public bool IsMigrating { get; set; } = false;
    public int FilterTypeIndex { get; set; } = 0;
    public int FilterLanguageIndex { get; set; } = 0;
    public bool IsCreatingStarter { get; set; } = false;
}

class ResourceProps
{
    public int ResourceID { get; set; }
}

partial class ListLearningResourcesPage : Component<ListLearningResourcesState>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] UserProfileRepository _userProfileRepo;

    LocalizationManager _localize => LocalizationManager.Instance;

    List<string> _mediaTypes = new() { "All" };
    List<string> _languages = new() { "All" };




    public override VisualNode Render()
    {
        return ContentPage($"{_localize["LearningResources"]}",
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Search")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = FluentUI.search_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(() => SetState(s => s.SearchText = "")), // Clear search and focus the search field
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Add")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = FluentUI.add_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(AddResource),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Progress")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = FluentUI.chart_multiple_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(ViewVocabularyProgress),

                State.IsLoading ?
                    VStack(
                        ActivityIndicator().IsRunning(true).Center()
                    ).VCenter().HCenter() :
                    Grid(rows: "*,Auto", columns: "*",
                            CollectionView()
                                .Margin(MyTheme.LayoutPadding)
                                .GridRow(0)
                                .SelectionMode(SelectionMode.None)
                                .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty,
                                    GridLayoutHelper.CalculateResponsiveLayout(
                                        desiredItemWidth: 350,
                                        orientation: ItemsLayoutOrientation.Vertical,
                                        maxColumns: 3))
                                .ItemsSource(State.Resources, RenderResourceItem)
                                .EmptyView(
                                    VStack(
                                        Label($"{_localize["NoResourcesFound"]}")
                                            .VCenter().HCenter(),

                                        State.IsCreatingStarter ?
                                        VStack(
                                            ActivityIndicator()
                                                .IsRunning(true)
                                                .HCenter()
                                                .HeightRequest(30)
                                                .WidthRequest(30),
                                            Label("Creating starter vocabulary...")
                                                .HCenter()
                                                .FontSize(14)
                                                .TextColor(Colors.Gray)
                                        )
                                        .Spacing(MyTheme.ComponentSpacing) :
                                        VStack(
                                            Button("Add Your First Resource")
                                                .OnClicked(AddResource)
                                                .HCenter()
                                                .WidthRequest(200),

                                            Button("Create a Starter Resource")
                                                .OnClicked(CreateStarterResource)
                                                .HCenter()
                                                .WidthRequest(200)
                                                .BackgroundColor(MyTheme.HighlightDarkest)
                                        )
                                        .Spacing(MyTheme.ComponentSpacing)
                                    )
                                    .Spacing(MyTheme.LayoutSpacing)
                                    .VCenter()
                                ),// emptyview, end of CollectionView
                            RenderBottomBar()
                        ) // Grid
                        .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )// contentpage
        .OnAppearing(LoadResources);
    }

    VisualNode RenderResourceItem(LearningResource resource) =>
        Border(
            Grid(rows: "Auto, Auto", columns: "Auto, *, Auto",
                // Icon based on media type
                Image()
                .Source(MyTheme.GetIconForMediaType(resource.MediaType))
                    .VCenter()
                    .HCenter()
                    .GridColumn(0)
                    .GridRowSpan(2),

                // Title and info
                Label(resource.Title)
                    .FontSize(18)
                    .FontAttributes(FontAttributes.Bold)
                    .GridColumn(1)
                    .GridRow(0)
                    .LineBreakMode(LineBreakMode.TailTruncation),

                HStack(
                    // Smart resource indicator
                    resource.IsSmartResource
                        ? Label("⚡")
                            .FontSize(12)
                            .Margin(new Thickness(0, 0, MyTheme.MicroSpacing, 0))
                        : null,

                    Label(resource.MediaType)
                        .FontSize(12)
                        .TextColor(Colors.DarkGray),

                    Label("•")
                        .FontSize(12)
                        .TextColor(Colors.DarkGray)
                        .Margin(new Thickness(MyTheme.MicroSpacing, 0)),

                    Label(resource.Language)
                        .FontSize(12)
                        .TextColor(Colors.DarkGray),

                    // System-generated badge for smart resources
                    resource.IsSmartResource
                        ? HStack(
                            Label("•")
                                .FontSize(12)
                                .TextColor(Colors.DarkGray)
                                .Margin(new Thickness(MyTheme.MicroSpacing, 0)),
                            Label("Auto-updated")
                                .FontSize(12)
                                .TextColor(MyTheme.AccentText)
                                .FontAttributes(FontAttributes.Italic)
                        )
                        : null
                )
                .GridColumn(1)
                .GridRow(1)
                .Spacing(MyTheme.MicroSpacing),

                // Date
                VStack(
                    Label(resource.CreatedAt.ToString("d"))
                        .FontSize(12)
                        .TextColor(Colors.DarkGray)
                        .HEnd(),

                    Label("Details >")
                        .HEnd()
                )
                .GridColumn(2)
                .GridRowSpan(2)
                .VCenter()
            )
            .Padding(MyTheme.ComponentSpacing)
            .ColumnSpacing(MyTheme.SectionSpacing).RowSpacing(MyTheme.MicroSpacing)
            .OnTapped(() => ViewResource(resource.Id))
        )
        .Stroke(Colors.LightGray)
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Margin(new Thickness(MyTheme.ComponentSpacing, MyTheme.MicroSpacing));

    VisualNode RenderBottomBar() =>
        Grid(rows: "Auto", columns: "*,Auto,Auto",
            new SfTextInputLayout(
                Entry()
                    .Placeholder($"{_localize["Search"]}...")
                    .Text(State.SearchText)
                    .OnTextChanged(text =>
                    {
                        SetState(s => s.SearchText = text);
                        SearchResources();
                    })
                    .FontSize(13)
                    .VCenter()
            )
            .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Outlined)
            .OutlineCornerRadius(27)
            .ShowHint(false)
            .LeadingView(
                Image()
                    .Source(MyTheme.IconSearch)
                    .HeightRequest(MyTheme.IconSize)
                    .WidthRequest(MyTheme.IconSize)
            )
            .HeightRequest(54)
            .FocusedStrokeThickness(0)
            .UnfocusedStrokeThickness(0)
            .GridColumn(0)
            .VStart(),

            // Type filter icon
            ImageButton()
                .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconDictionary)
                .BackgroundColor(MyTheme.LightSecondaryBackground)
                .HeightRequest(36)
                .WidthRequest(36)
                .CornerRadius(18)
                .Padding(6)
                .OnClicked(ShowTypeFilterDialog)
                .GridColumn(1)
                .VStart(),

            // Language filter icon
            ImageButton()
                .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconGlobe)
                .BackgroundColor(MyTheme.LightSecondaryBackground)
                .HeightRequest(36)
                .WidthRequest(36)
                .CornerRadius(18)
                .Padding(6)
                .OnClicked(ShowLanguageFilterDialog)
                .GridColumn(2)
                .VStart()
        )
        .ColumnSpacing(MyTheme.ComponentSpacing)
        .Padding(new Thickness(MyTheme.LayoutSpacing, MyTheme.LayoutSpacing, MyTheme.LayoutSpacing, 0))
        .GridRow(1);

    async Task LoadResources()
    {
        SetState(s => s.IsLoading = true);

        _mediaTypes = new List<string> { "All" };
        _mediaTypes.AddRange(Constants.MediaTypes);

        _languages = new List<string> { "All" };
        _languages.AddRange(Constants.Languages);

        var resources = await _resourceRepo.GetAllResourcesAsync();

        SetState(s =>
        {
            s.Resources = resources;
            s.IsLoading = false;
        });
    }

    async Task SearchResources()
    {
        if (string.IsNullOrWhiteSpace(State.SearchText))
        {
            await LoadResources();
            return;
        }

        SetState(s => s.IsLoading = true);

        var resources = await _resourceRepo.SearchResourcesAsync(State.SearchText);

        // Apply current filters if necessary
        resources = FilterResourcesList(resources);

        SetState(s =>
        {
            s.Resources = resources;
            s.IsLoading = false;
        });
    }

    async Task FilterResources()
    {
        SetState(s => s.IsLoading = true);

        // Start with all resources or search results
        List<LearningResource> resources;

        if (!string.IsNullOrWhiteSpace(State.SearchText))
        {
            resources = await _resourceRepo.SearchResourcesAsync(State.SearchText);
        }
        else
        {
            resources = await _resourceRepo.GetAllResourcesAsync();
        }

        // Apply filters
        resources = FilterResourcesList(resources);

        SetState(s =>
        {
            s.Resources = resources;
            s.IsLoading = false;
        });
    }

    // Helper method to filter a list of resources
    private List<LearningResource> FilterResourcesList(List<LearningResource> resources)
    {
        // Apply type filter
        if (State.FilterType != "All")
        {
            resources = resources.Where(r => r.MediaType == State.FilterType).ToList();
        }

        // Apply language filter
        if (State.FilterLanguage != "All")
        {
            resources = resources.Where(r => r.Language == State.FilterLanguage).ToList();
        }

        return resources;
    }

    Task AddResource()
    {
        return MauiControls.Shell.Current.GoToAsync(nameof(AddLearningResourcePage));
    }

    Task ViewResource(int resourceId)
    {
        return MauiControls.Shell.Current.GoToAsync<ResourceProps>(
            nameof(EditLearningResourcePage),
            props => props.ResourceID = resourceId);
    }

    Task ViewVocabularyProgress()
    {
        return MauiControls.Shell.Current.GoToAsync(nameof(VocabularyLearningProgressPage));
    }

    async Task CreateStarterResource()
    {
        try
        {
            SetState(s => s.IsCreatingStarter = true);

            // Get user profile to get language preferences
            var profile = await _userProfileRepo.GetOrCreateDefaultAsync();

            if (string.IsNullOrEmpty(profile.NativeLanguage) || string.IsNullOrEmpty(profile.TargetLanguage))
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Profile Required",
                    "Please set up your native and target languages in your profile first.",
                    "OK");

                SetState(s => s.IsCreatingStarter = false);
                return;
            }

            // Create the starter vocabulary resource
            await _resourceRepo.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);

            // Reload resources to show the new starter resource
            await LoadResources();

            SetState(s => s.IsCreatingStarter = false);
        }
        catch (Exception ex)
        {
            SetState(s => s.IsCreatingStarter = false);

            await Application.Current.MainPage.DisplayAlert(
                "Error",
                $"Failed to create starter resource: {ex.Message}",
                "OK");
        }
    }

    async Task ShowTypeFilterDialog()
    {
        var selection = await Application.Current.MainPage.DisplayActionSheet(
            "Filter by Type", "Cancel", null, _mediaTypes.ToArray());
        if (string.IsNullOrEmpty(selection) || selection == "Cancel")
            return;

        var index = _mediaTypes.IndexOf(selection);
        if (index >= 0)
        {
            SetState(s =>
            {
                s.FilterTypeIndex = index;
                s.FilterType = _mediaTypes[index];
            });
            FilterResources();
        }
    }

    async Task ShowLanguageFilterDialog()
    {
        var selection = await Application.Current.MainPage.DisplayActionSheet(
            "Filter by Language", "Cancel", null, _languages.ToArray());
        if (string.IsNullOrEmpty(selection) || selection == "Cancel")
            return;

        var index = _languages.IndexOf(selection);
        if (index >= 0)
        {
            SetState(s =>
            {
                s.FilterLanguageIndex = index;
                s.FilterLanguage = _languages[index];
            });
            FilterResources();
        }
    }
}