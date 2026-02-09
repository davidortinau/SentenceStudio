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
        return ContentPage($"{_localize["LearningResources"]}",
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Add")
                // .IconImageSource(MyTheme.IconAdd)
                .OnClicked(AddResource),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Progress")
                // .IconImageSource(MyTheme.IconChart)
                .OnClicked(ViewVocabularyProgress),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Generate Starter")
                // .IconImageSource(MyTheme.IconGenerate)
                .OnClicked(CreateStarterResource),

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

                                        VStack(
                                            Button("Add Your First Resource")
                                                .OnClicked(AddResource)
                                                .HCenter()
                                                .WidthRequest(200),

                                            Button("Create a Starter Resource")
                                                .OnClicked(CreateStarterResource)
                                                .HCenter()
                                                .WidthRequest(200)
                                                .Background(MyTheme.HighlightDarkest)
                                        )
                                        .Spacing(MyTheme.ComponentSpacing)
                                    )
                                    .Spacing(MyTheme.LayoutSpacing)
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
                                            .ThemeKey(MyTheme.Body2)
                                            .TextColor(MyTheme.LightOnDarkBackground)
                                    )
                                    .Spacing(MyTheme.ComponentSpacing)
                                    .Center()
                                )
                                .BackgroundColor(MyTheme.Gray950.WithAlpha(0.6f))
                                .GridRow(0).GridRowSpan(2)
                                : null
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
                    .ThemeKey(MyTheme.Title3)
                    .GridColumn(1)
                    .GridRow(0)
                    .LineBreakMode(LineBreakMode.TailTruncation),

                // Metadata line
                Label($"{(resource.IsSmartResource ? "⚡ " : "")}{resource.MediaType} • {resource.Language}{(resource.IsSmartResource ? " • Auto-updated" : "")}")
                    .ThemeKey(MyTheme.Caption1)
                    .TextColor(MyTheme.SecondaryText)
                    .GridColumn(1)
                    .GridRow(1),

                // Date
                Label(resource.CreatedAt.ToString("d"))
                    .ThemeKey(MyTheme.Caption1)
                    .TextColor(MyTheme.SecondaryText)
                    .HEnd()
                    .GridColumn(2)
                    .GridRowSpan(2)
                    .VCenter()
            )
            .Padding(MyTheme.ComponentSpacing)
            .ColumnSpacing(MyTheme.SectionSpacing).RowSpacing(MyTheme.MicroSpacing)
            .OnTapped(() => ViewResource(resource.Id))
        )
        .Stroke(MyTheme.ItemBorder)
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
                    .ThemeKey(MyTheme.Caption1)
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
                .Background(MyTheme.LightSecondaryBackground)
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
                .Background(MyTheme.LightSecondaryBackground)
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
                        TextColor = MyTheme.LightOnDarkBackground,
                        FontSize = MyTheme.Size160,
                        Padding = new Thickness(MyTheme.Size80, MyTheme.Size120)
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