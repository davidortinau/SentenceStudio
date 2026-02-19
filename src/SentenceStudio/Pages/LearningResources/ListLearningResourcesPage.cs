using MauiReactor.Shapes;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Helpers;
using UXDivers.Popups;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.LearningResources;

class ListLearningResourcesState
{
    public List<LearningResource> Resources { get; set; } = [];
    public bool IsLoading { get; set; } = false;
    public string SearchText { get; set; } = string.Empty;
    public string FilterType { get; set; } = "All";
    public List<string> FilterLanguages { get; set; } = [];
    public bool IsMigrating { get; set; } = false;
    public int FilterTypeIndex { get; set; } = 0;
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
    [Inject] ILogger<ListLearningResourcesPage> _logger;
    [Inject] NativeThemeService _themeService;

    LocalizationManager _localize => LocalizationManager.Instance;

    List<string> _mediaTypes = new() { "All" };
    List<string> _languages = new() { "All" };





    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
    }


    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage($"{_localize["LearningResources"]}",
            ToolbarItem().Order(ToolbarItemOrder.Primary).Text($"{_localize["Add"]}")
                .IconImageSource(BootstrapIcons.Create(BootstrapIcons.PlusLg, theme.GetOnBackground(), 20))
                .OnClicked(AddResource),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Progress")
                .IconImageSource(BootstrapIcons.Create(BootstrapIcons.GraphUp, theme.GetOnBackground(), 20))
                .OnClicked(ViewVocabularyProgress),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Generate Starter")
                .OnClicked(CreateStarterResource),

                State.IsLoading ?
                    VStack(
                        ActivityIndicator().IsRunning(true).Center()
                    ).VCenter().HCenter() :
                    Grid(rows: "Auto,*", columns: "*",
                            RenderSearchFilterBar(),
                            CollectionView()
                                .Margin(16)
                                .GridRow(1)
                                .SelectionMode(SelectionMode.None)
                                .ItemSizingStrategy(ItemSizingStrategy.MeasureFirstItem)
                                .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty,
                                    GridLayoutHelper.CalculateResponsiveLayout(
                                        desiredItemWidth: 280,
                                        orientation: ItemsLayoutOrientation.Vertical,
                                        maxColumns: 3))
                                .ItemsSource(State.Resources, RenderResourceItem)
                                .EmptyView(
                                    VStack(
                                        Label($"{_localize["NoResourcesFound"]}")
                                            .Muted()
                                            .VCenter().HCenter(),

                                        VStack(
                                            Button("Add Your First Resource")
                                                .Class("btn-primary")
                                                .OnClicked(AddResource)
                                                .HCenter()
                                                .WidthRequest(200),

                                            Button("Create a Starter Resource")
                                                .OnClicked(CreateStarterResource)
                                                .HCenter()
                                                .WidthRequest(200)
                                                .Class("btn-secondary")
                                        )
                                        .Spacing(8)
                                    )
                                    .Spacing(16)
                                    .VCenter()
                                ),// emptyview, end of CollectionView
                            State.IsCreatingStarter
                                ? Grid(
                                    VStack(
                                        ActivityIndicator()
                                            .IsRunning(true)
                                            .HCenter()
                                            .HeightRequest(30)
                                            .WidthRequest(30),
                                        Label("Creating starter vocabulary...")
                                            .HCenter()
                                            .FontSize(14)
                                            .TextColor(theme.GetOnSurface())
                                    )
                                    .Spacing(8)
                                    .Center()
                                )
                                .BackgroundColor(theme.GetOnBackground().WithAlpha(0.6f))
                                .GridRow(0).GridRowSpan(2)
                                : null
                        ) // Grid
                        .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )// contentpage
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(LoadResources);
    }

    VisualNode RenderResourceItem(LearningResource resource)
    {
        var theme = BootstrapTheme.Current;

        return Border(
            Grid(rows: "Auto,Auto", columns: "Auto,*,Auto",
                // Icon
                Image()
                    .Source(GetBootstrapIconForMediaType(resource.MediaType, theme))
                    .VStart()
                    .GridColumn(0).GridRow(0).GridRowSpan(2)
                    .Margin(0, 0, 12, 0),

                // Title
                Label(resource.Title)
                    .H6()
                    .LineBreakMode(LineBreakMode.TailTruncation)
                    .MaxLines(1)
                    .GridColumn(1).GridRow(0),

                // Date right-aligned
                Label(resource.CreatedAt.ToString("d"))
                    .Small()
                    .Muted()
                    .GridColumn(2).GridRow(0)
                    .Margin(8, 0, 0, 0),

                // Metadata line
                Label($"{(resource.IsSmartResource ? "⚡ " : "")}{resource.MediaType} • {resource.Language}{(resource.IsSmartResource ? " • Auto-updated" : "")}")
                    .Small()
                    .Muted()
                    .LineBreakMode(LineBreakMode.TailTruncation)
                    .MaxLines(1)
                    .GridColumn(1).GridColumnSpan(2).GridRow(1)
            )
            .Padding(12)
            .OnTapped(() => ViewResource(resource.Id))
        )
        .Class("card")
        .Margin(0);
    }

    static ImageSource GetBootstrapIconForMediaType(string mediaType, BootstrapTheme theme)
    {
        return mediaType switch
        {
            "Video" => BootstrapIcons.Create(BootstrapIcons.CameraVideo, theme.GetOnBackground(), 24),
            "Podcast" => BootstrapIcons.Create(BootstrapIcons.Soundwave, theme.GetOnBackground(), 24),
            "Image" => BootstrapIcons.Create(BootstrapIcons.Image, theme.GetOnBackground(), 24),
            "Vocabulary List" => BootstrapIcons.Create(BootstrapIcons.ListUl, theme.GetOnBackground(), 24),
            "Article" => BootstrapIcons.Create(BootstrapIcons.FileText, theme.GetOnBackground(), 24),
            _ => BootstrapIcons.Create(BootstrapIcons.FileText, theme.GetOnBackground(), 24)
        };
    }

    VisualNode RenderSearchFilterBar()
    {
        var theme = BootstrapTheme.Current;

        return Grid(rows: "Auto", columns: "*,Auto,Auto",
            Entry()
                .Placeholder($"{_localize["SearchResources"]}...")
                .Text(State.SearchText)
                .OnTextChanged(text =>
                {
                    SetState(s => s.SearchText = text);
                    SearchResources();
                })
                .Class("form-control")
                .HeightRequest(44)
                .HFill()
                .GridColumn(0)
                .VStart(),

            // Type filter dropdown
            Picker()
                .Title($"{_localize["AllTypes"]}")
                .ItemsSource(_mediaTypes)
                .SelectedIndex(State.FilterTypeIndex)
                .OnSelectedIndexChanged(idx =>
                {
                    if (idx < 0 || idx >= _mediaTypes.Count) return;
                    SetState(s =>
                    {
                        s.FilterTypeIndex = idx;
                        s.FilterType = idx == 0 ? "All" : _mediaTypes[idx];
                    });
                    _ = FilterResources();
                })
                .FormSelect()
                .HeightRequest(44)
                .WidthRequest(160)
                .GridColumn(1)
                .VStart(),

            // Language filter dropdown
            Picker()
                .Title($"{_localize["AllLanguages"]}")
                .ItemsSource(_languages)
                .SelectedIndex(0)
                .OnSelectedIndexChanged(idx =>
                {
                    if (idx < 0 || idx >= _languages.Count) return;
                    var lang = _languages[idx];
                    SetState(s =>
                    {
                        s.FilterLanguages = idx == 0 ? new List<string>() : new List<string> { lang };
                    });
                    _ = FilterResources();
                })
                .FormSelect()
                .HeightRequest(44)
                .WidthRequest(160)
                .GridColumn(2)
                .VStart()
        )
        .ColumnSpacing(8)
        .Padding(new Thickness(16, 8, 16, 8))
        .GridRow(0);
    }

    async Task LoadResources()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SetState(s => s.IsLoading = true);

        _mediaTypes = new List<string> { $"{_localize["AllTypes"]}" };
        _mediaTypes.AddRange(Constants.MediaTypes);

        _languages = new List<string> { $"{_localize["AllLanguages"]}" };
        _languages.AddRange(Constants.Languages);

        var resources = await _resourceRepo.GetAllResourcesLightweightAsync(
            State.FilterType, State.FilterLanguages);

        sw.Stop();
        _logger.LogInformation("✅ LoadResources: {Elapsed}ms ({Count} resources)", sw.ElapsedMilliseconds, resources.Count);

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

        // Apply current filters on search results (small list, client-side is fine)
        resources = FilterResourcesList(resources);

        SetState(s =>
        {
            s.Resources = resources;
            s.IsLoading = false;
        });
    }

    async Task FilterResources()
    {
        if (!string.IsNullOrWhiteSpace(State.SearchText))
        {
            await SearchResources();
            return;
        }

        // Filters are pushed to SQL via LoadResources
        await LoadResources();
    }

    // Helper method to filter a list of resources
    private List<LearningResource> FilterResourcesList(List<LearningResource> resources)
    {
        // Apply type filter (index 0 = "All Types")
        if (State.FilterTypeIndex > 0 && !string.IsNullOrEmpty(State.FilterType))
        {
            resources = resources.Where(r => r.MediaType == State.FilterType).ToList();
        }

        // Apply language filter
        if (State.FilterLanguages.Count > 0)
        {
            resources = resources.Where(r => State.FilterLanguages.Contains(r.Language)).ToList();
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
            var profile = await _userProfileRepo.GetOrCreateDefaultAsync();

            if (string.IsNullOrEmpty(profile.NativeLanguage))
            {
                await IPopupService.Current.PushAsync(new SimpleActionPopup
                {
                    Title = "Profile Required",
                    Text = "Please set up your native language in your profile first.",
                    ActionButtonText = "OK",
                    ShowSecondaryActionButton = false
                });
                return;
            }

            string selectedLanguage = null;

            var popup = new ListActionPopup
            {
                Title = "Select Language",
                ShowActionButton = false,
                ItemsSource = Constants.Languages,
                ItemDataTemplate = new MauiControls.DataTemplate(() =>
                {
                    var tapGesture = new MauiControls.TapGestureRecognizer();
                    tapGesture.Tapped += async (s, e) =>
                    {
                        if (s is MauiControls.Label label && label.BindingContext is string lang)
                        {
                            selectedLanguage = lang;
                            await IPopupService.Current.PopAsync();
                        }
                    };

                    var label = new MauiControls.Label
                    {
                        TextColor = BootstrapTheme.Current.GetOnSurface(),
                        FontSize = 16,
                        Padding = new Thickness(8, 12)
                    };
                    label.SetBinding(MauiControls.Label.TextProperty, ".");
                    label.GestureRecognizers.Add(tapGesture);
                    return label;
                })
            };

            await IPopupService.Current.PushAsync(popup);

            if (string.IsNullOrEmpty(selectedLanguage))
                return;

            SetState(s => s.IsCreatingStarter = true);

            await _resourceRepo.GetStarterVocabulary(profile.NativeLanguage, selectedLanguage);

            await LoadResources();

            SetState(s => s.IsCreatingStarter = false);

            var starterToast = new UXDivers.Popups.Maui.Controls.Toast { Title = $"Starter resource created for {selectedLanguage}!" };
            await IPopupService.Current.PushAsync(starterToast);
            _ = Task.Delay(3000).ContinueWith(async _ =>
            {
                try { await IPopupService.Current.PopAsync(starterToast); } catch { }
            });
        }
        catch (Exception ex)
        {
            SetState(s => s.IsCreatingStarter = false);

            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to create starter resource: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

}
