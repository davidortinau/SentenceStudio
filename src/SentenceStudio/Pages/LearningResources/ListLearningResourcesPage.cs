using MauiReactor.Shapes;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Helpers;
using UXDivers.Popups;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

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

    LocalizationManager _localize => LocalizationManager.Instance;

    List<string> _mediaTypes = new() { "All" };
    List<string> _languages = new() { "All" };




    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage($"{_localize["LearningResources"]}",
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Add")
                .OnClicked(AddResource),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Progress")
                .OnClicked(ViewVocabularyProgress),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Generate Starter")
                .OnClicked(CreateStarterResource),

                State.IsLoading ?
                    VStack(
                        ActivityIndicator().IsRunning(true).Center()
                    ).VCenter().HCenter() :
                    Grid(rows: "*,Auto", columns: "*",
                            CollectionView()
                                .Margin(16)
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
                                            .Muted()
                                            .VCenter().HCenter(),

                                        VStack(
                                            Button("Add Your First Resource")
                                                .Primary()
                                                .OnClicked(AddResource)
                                                .HCenter()
                                                .WidthRequest(200),

                                            Button("Create a Starter Resource")
                                                .OnClicked(CreateStarterResource)
                                                .HCenter()
                                                .WidthRequest(200)
                                                .Background(new SolidColorBrush(theme.Primary))
                                                .TextColor(Colors.White)
                                        )
                                        .Spacing(8)
                                    )
                                    .Spacing(16)
                                    .VCenter()
                                ),// emptyview, end of CollectionView
                            RenderBottomBar(),
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
                                            .TextColor(Colors.White)
                                    )
                                    .Spacing(8)
                                    .Center()
                                )
                                .BackgroundColor(Colors.Black.WithAlpha(0.6f))
                                .GridRow(0).GridRowSpan(2)
                                : null
                        ) // Grid
                        .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )// contentpage
        .OnAppearing(LoadResources);
    }

    VisualNode RenderResourceItem(LearningResource resource)
    {
        var theme = BootstrapTheme.Current;

        return Border(
            Grid(rows: "Auto, Auto", columns: "Auto, *, Auto",
                // Icon based on media type
                Image()
                    .Source(GetBootstrapIconForMediaType(resource.MediaType, theme))
                    .VCenter()
                    .HCenter()
                    .GridColumn(0)
                    .GridRowSpan(2),

                // Title and info
                Label(resource.Title)
                    .H5()
                    .GridColumn(1)
                    .GridRow(0)
                    .LineBreakMode(LineBreakMode.TailTruncation),

                // Metadata line
                Label($"{resource.MediaType} • {resource.Language}{(resource.IsSmartResource ? " • Auto-updated" : "")}")
                    .Small()
                    .Muted()
                    .GridColumn(1)
                    .GridRow(1),

                // Date
                Label(resource.CreatedAt.ToString("d"))
                    .Small()
                    .Muted()
                    .HEnd()
                    .GridColumn(2)
                    .GridRowSpan(2)
                    .VCenter()
            )
            .Padding(8)
            .ColumnSpacing(24).RowSpacing(4)
            .OnTapped(() => ViewResource(resource.Id))
        )
        .Stroke(theme.GetOutline())
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Margin(new Thickness(8, 4));
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

    VisualNode RenderBottomBar()
    {
        var theme = BootstrapTheme.Current;

        return Grid(rows: "Auto", columns: "*,Auto,Auto",
            Border(
                HStack(
                    Image()
                        .Source(BootstrapIcons.Create(BootstrapIcons.Search, theme.GetOnBackground(), 16))
                        .HeightRequest(16)
                        .WidthRequest(16),
                    Entry()
                        .Placeholder($"{_localize["Search"]}...")
                        .Text(State.SearchText)
                        .OnTextChanged(text =>
                        {
                            SetState(s => s.SearchText = text);
                            SearchResources();
                        })
                        .Small()
                        .VCenter()
                        .HFill()
                )
                .Spacing(8)
                .Padding(new Thickness(12, 0))
            )
            .BackgroundColor(theme.GetSurface())
            .Stroke(theme.GetOutline())
            .StrokeThickness(1)
            .StrokeShape(new RoundRectangle().CornerRadius(27))
            .HeightRequest(44)
            .GridColumn(0)
            .VStart(),

            // Type filter icon
            ImageButton()
                .Source(BootstrapIcons.Create(BootstrapIcons.Funnel, theme.GetOnBackground(), 18))
                .Background(new SolidColorBrush(theme.GetSurface()))
                .HeightRequest(36)
                .WidthRequest(36)
                .CornerRadius(18)
                .Padding(6)
                .OnClicked(ShowTypeFilterDialog)
                .GridColumn(1)
                .VStart(),

            // Language filter icon
            ImageButton()
                .Source(BootstrapIcons.Create(BootstrapIcons.Globe, theme.GetOnBackground(), 18))
                .Background(new SolidColorBrush(theme.GetSurface()))
                .HeightRequest(36)
                .WidthRequest(36)
                .CornerRadius(18)
                .Padding(6)
                .OnClicked(ShowLanguageFilterDialog)
                .GridColumn(2)
                .VStart()
        )
        .ColumnSpacing(8)
        .Padding(new Thickness(16, 16, 16, 0))
        .GridRow(1);
    }

    async Task LoadResources()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SetState(s => s.IsLoading = true);

        _mediaTypes = new List<string> { "All" };
        _mediaTypes.AddRange(Constants.MediaTypes);

        _languages = new List<string> { "All" };
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
        // Apply type filter
        if (State.FilterType != "All")
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
                        TextColor = Colors.White,
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

    async Task ShowTypeFilterDialog()
    {
        var typeTcs = new TaskCompletionSource<string>();
        var typeOptionItems = new List<OptionSheetItem>();
        foreach (var mediaType in _mediaTypes)
        {
            var capturedType = mediaType;
            var isSelected = capturedType == State.FilterType;
            typeOptionItems.Add(new OptionSheetItem
            {
                Text = isSelected ? $"\u2713  {capturedType}" : $"    {capturedType}",
                Command = new Command(async () =>
                {
                    typeTcs.TrySetResult(capturedType);
                    await IPopupService.Current.PopAsync();
                })
            });
        }
        var typePopup = new OptionSheetPopup
        {
            Title = "Filter by Type",
            Items = typeOptionItems,
            CloseWhenBackgroundIsClicked = true
        };
        IPopupService.Current.PopupPopped += typeHandler;
        void typeHandler(object s, PopupEventArgs e)
        {
            if (e.PopupPage == typePopup)
            {
                typeTcs.TrySetResult(null);
                IPopupService.Current.PopupPopped -= typeHandler;
            }
        }
        ;
        await IPopupService.Current.PushAsync(typePopup);
        var selection = await typeTcs.Task;
        if (string.IsNullOrEmpty(selection))
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
        var selected = new HashSet<string>(State.FilterLanguages);
        var tcs = new TaskCompletionSource();

        var popup = new OptionSheetPopup
        {
            Title = "Filter by Language",
            CloseWhenBackgroundIsClicked = true
        };

        void BuildItems()
        {
            var items = new List<OptionSheetItem>();
            foreach (var lang in Constants.Languages)
            {
                var capturedLang = lang;
                var isSelected = selected.Contains(capturedLang);
                items.Add(new OptionSheetItem
                {
                    Text = isSelected ? $"\u2713  {capturedLang}" : $"    {capturedLang}",
                    Command = new Command(() =>
                    {
                        if (!selected.Remove(capturedLang))
                            selected.Add(capturedLang);
                        BuildItems();
                    })
                });
            }
            popup.Items = items;
        }

        BuildItems();

        IPopupService.Current.PopupPopped += langHandler;
        void langHandler(object s, PopupEventArgs e)
        {
            if (e.PopupPage == popup)
            {
                tcs.TrySetResult();
                IPopupService.Current.PopupPopped -= langHandler;
            }
        }
        ;
        await IPopupService.Current.PushAsync(popup);
        await tcs.Task;

        SetState(s => s.FilterLanguages = selected.ToList());
        await FilterResources();
    }
}