using MauiReactor.Shapes;

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
}

class ResourceProps
{
    public int ResourceID { get; set; }
}

partial class ListLearningResourcesPage : Component<ListLearningResourcesState>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] VocabularyService _vocabService;
    
    LocalizationManager _localize => LocalizationManager.Instance;
    
    List<string> _mediaTypes = new() { "All" };
    List<string> _languages = new() { "All" };


    

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["LearningResources"]}",
            ToolbarItem("Search").OnClicked(() => SetState(s => s.SearchText = "")), // Clear search and focus the search field
            ToolbarItem("Add").OnClicked(AddResource),
            ToolbarItem("Migrate").OnClicked(MigrateVocabularyLists),

            Grid(rows: "Auto, *", columns: "*",
                VStack(
                    // Search bar
                    Border(
                        Grid(
                            Entry()
                                .Placeholder($"{_localize["Search"]}...")
                                .Text(State.SearchText)
                                .OnTextChanged(text =>
                                {
                                    SetState(s => s.SearchText = text);
                                    SearchResources();
                                }),

                            Button()
                                .ImageSource(SegoeFluentIcons.Search.ToImageSource())
                                .BackgroundColor(Colors.Transparent)
                                .OnClicked(SearchResources)
                                .GridColumn(1)
                                .Padding(10)
                                .HEnd()
                        )
                        .Columns("*, Auto")
                    )
                    .Style((Style)Application.Current.Resources["InputWrapper"])
                    .Margin(new Thickness(10, 10, 10, 5)),

                    // Filters
                    Grid(
                        // Type filter
                        new SfTextInputLayout(
                            Picker()
                                .ItemsSource(_mediaTypes)
                                .SelectedIndex(State.FilterTypeIndex)
                                .OnSelectedIndexChanged(index =>
                                {
                                    SetState(s =>
                                    {
                                        s.FilterTypeIndex = index;
                                        s.FilterType = _mediaTypes[index];
                                    });
                                    FilterResources();
                                })
                        )
                        .Hint("Type")
                        .GridColumn(0),

                        // Language filter
                        new SfTextInputLayout(
                            Picker()
                                .ItemsSource(_languages)
                                .SelectedIndex(State.FilterLanguageIndex)
                                .OnSelectedIndexChanged(index =>
                                {
                                    SetState(s =>
                                    {
                                        s.FilterLanguageIndex = index;
                                        s.FilterLanguage = _languages[index];
                                    });
                                    FilterResources();
                                })
                        )
                        .Hint("Language")
                        .GridColumn(1)
                    )
                    .Columns("*, *")
                    .ColumnSpacing(10)
                    .Margin(new Thickness(10, 5, 10, 10))
                ),
                State.IsLoading ?
                        ActivityIndicator().IsRunning(true).VCenter().HCenter().GridRow(1) :
                        State.Resources.Count == 0 ?
                            VStack(
                                Label($"{_localize["NoResourcesFound"]}")
                                    .VCenter().HCenter(),

                                Button("Add Your First Resource")
                                    .OnClicked(AddResource)
                                    .HCenter()
                                    .WidthRequest(200),

                                Button("Import from Vocabulary Lists")
                                    .OnClicked(MigrateVocabularyLists)
                                    .ThemeKey("Secondary")
                                    .HCenter()
                                    .WidthRequest(200)
                            )
                            .GridRow(1)
                            .Spacing(15)
                            .VCenter() :
                            CollectionView()
                                .GridRow(1)
                                .SelectionMode(SelectionMode.None)
                                .ItemsSource(State.Resources, RenderResourceItem)
            )
        ).OnAppearing(LoadResources);
    }

    VisualNode RenderResourceItem(LearningResource resource) =>
        Border(
            Grid(rows: "Auto, Auto", columns: "Auto, *, Auto",
                // Icon based on media type
                Image()
                .Source(ApplicationTheme.GetIconForMediaType(resource.MediaType))
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
                    Label(resource.MediaType)
                        .FontSize(12)
                        .TextColor(Colors.DarkGray),
                        
                    Label("•")
                        .FontSize(12)
                        .TextColor(Colors.DarkGray)
                        .Margin(new Thickness(5, 0)),
                        
                    Label(resource.Language)
                        .FontSize(12)
                        .TextColor(Colors.DarkGray)
                )
                .GridColumn(1)
                .GridRow(1)
                .Spacing(5),
                
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
            .Padding(10)
            .ColumnSpacing(20).RowSpacing(5)
            .OnTapped(() => ViewResource(resource.ID))
        )
        .Stroke(Colors.LightGray)
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Margin(new Thickness(10, 5));

    async Task LoadResources()
    {
        SetState(s => s.IsLoading = true);

        _mediaTypes = new List<string> { "All" };
        _mediaTypes.AddRange(Constants.MediaTypes);

        _languages = new List<string> { "All" };
        _languages.AddRange(Constants.Languages);       
        
        var resources = await _resourceRepo.GetAllResourcesAsync();
        
        SetState(s => {
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
        
        SetState(s => {
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
        
        SetState(s => {
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
    
    async Task MigrateVocabularyLists()
    {
        // Check if there are vocabulary lists to migrate
        var lists = await _vocabService.GetListsAsync();
        if (lists == null || lists.Count == 0)
        {
            await Application.Current.MainPage.DisplayAlert(
                "No Lists to Migrate", 
                "There are no vocabulary lists to migrate.", 
                "OK");
            return;
        }
        
        bool confirm = await Application.Current.MainPage.DisplayAlert(
            "Migrate Vocabulary Lists", 
            $"Would ye like to migrate {lists.Count} vocabulary lists to Learning Resources? The original lists will be preserved.", 
            "Aye, Migrate", 
            "Nay");
            
        if (!confirm) return;
        
        SetState(s => s.IsMigrating = true);
        
        // Use our new migration helper
        await MigrationHelper.MigrateVocabularyListsAsync(_vocabService, _resourceRepo);
        
        // Reload the resources
        await LoadResources();
        
        SetState(s => s.IsMigrating = false);
        
        await Application.Current.MainPage.DisplayAlert(
            "Migration Complete", 
            "Your vocabulary lists have been migrated to Learning Resources!", 
            "OK");
    }
}