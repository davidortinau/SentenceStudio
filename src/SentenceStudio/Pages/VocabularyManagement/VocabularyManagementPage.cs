using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using Fonts;

namespace SentenceStudio.Pages.VocabularyManagement;

public enum VocabularyFilter
{
    All,
    Associated,
    Orphaned,
    SpecificResource
}

public class VocabularyStats
{
    public int TotalWords { get; set; }
    public int AssociatedWords { get; set; }
    public int OrphanedWords { get; set; }
}

public class VocabularyCardViewModel
{
    public VocabularyWord Word { get; set; } = null!;
    public List<LearningResource> AssociatedResources { get; set; } = new();
    public bool IsSelected { get; set; } = false;
    public bool IsOrphaned => !AssociatedResources.Any();
}

class VocabularyManagementPageState
{
    public bool IsLoading { get; set; } = true;
    public ObservableCollection<VocabularyCardViewModel> AllVocabularyItems { get; set; } = new();
    public ObservableCollection<VocabularyCardViewModel> FilteredVocabularyItems { get; set; } = new();
    public ObservableCollection<LearningResource> AvailableResources { get; set; } = new();

    // Search and filtering
    public string SearchText { get; set; } = string.Empty;
    public VocabularyFilter SelectedFilter { get; set; } = VocabularyFilter.All;
    public LearningResource? SelectedResource { get; set; }

    // Statistics
    public VocabularyStats Stats { get; set; } = new();

    // Multi-select and bulk operations
    public bool IsMultiSelectMode { get; set; } = false;
    public HashSet<int> SelectedWordIds { get; set; } = new();

}

partial class VocabularyManagementPage : Component<VocabularyManagementPageState>, IDisposable
{
    [Inject] LearningResourceRepository _resourceRepo;
    private System.Threading.Timer? _searchTimer;

    public override VisualNode Render()
    {
        return ContentPage("Vocabulary Management",
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Add")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = FluentUI.add_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(async () => await ToggleQuickAdd()),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text("Select")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = State.IsMultiSelectMode ? FluentUI.dismiss_20_regular : FluentUI.select_all_on_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(State.IsMultiSelectMode ? ExitMultiSelectMode : EnterMultiSelectMode),
            State.IsLoading ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "*,Auto", columns: "*",
                    RenderVocabularyList(),
                    RenderBottomBar()
                )
                .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )
        .OnAppearing(async () =>
        {
            await LoadData();
            // Refresh data when returning from edit page if already loaded
            if (!State.IsLoading)
            {
                await LoadVocabularyData();
            }
        });
    }

    // Bottom bar with compact search and icon filters (or bulk actions in multi-select mode)
    VisualNode RenderBottomBar()
        => State.IsMultiSelectMode ? RenderBulkActionsBar() : RenderCompactSearchBar();

    VisualNode RenderCompactSearchBar()
        =>
                Grid(rows: "Auto", columns: "*,Auto,Auto",
                        new SfTextInputLayout(
                            Entry()
                                .Placeholder("Search")
                                .Text(State.SearchText)
                                .OnTextChanged(OnSearchTextChanged)
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
                    // )
                    // .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground)
                    // .StrokeShape(new RoundRectangle().CornerRadius(16))
                    // .Stroke(MyTheme.Gray300)
                    // .StrokeThickness(1)
                    // .GridColumn(0),

                    // Status filter icon
                    ImageButton()
                        .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconStatus)
                        .BackgroundColor(MyTheme.LightSecondaryBackground)
                        .HeightRequest(36)
                        .WidthRequest(36)
                        .CornerRadius(24)
                        .Padding(6)
                        .OnClicked(async () => await ShowStatusFilterDialog())
                        .GridColumn(1)
                        .VStart(),

                    // Resource filter icon
                    ImageButton()
                        .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconDictionary)
                        .BackgroundColor(MyTheme.LightSecondaryBackground)
                        .HeightRequest(36)
                        .WidthRequest(36)
                        .CornerRadius(18)
                        .Padding(6)
                        .OnClicked(async () => await ShowResourceFilterDialog())
                        .GridColumn(2)
                        .VStart()
                ).ColumnSpacing(8)
            .Padding(15, 0)
            .GridRow(1);

    VisualNode RenderBulkActionsBar()
        => Border(
                HStack(spacing: 10,
                    Label($"{State.SelectedWordIds.Count} selected")
                        .VCenter()
                        .TextColor(MyTheme.Gray600)
                        .HorizontalOptions(LayoutOptions.FillAndExpand),
                    Button("Delete")
                        .ThemeKey("Danger")
                        .OnClicked(BulkDeleteSelected)
                        .IsEnabled(State.SelectedWordIds.Any()),
                    Button("Associate")
                        .ThemeKey("Primary")
                        .OnClicked(BulkAssociateSelected)
                        .IsEnabled(State.SelectedWordIds.Any())
                )
            )
            .ThemeKey(MyTheme.Surface1)
            .Padding(12, 8)
            .Margin(12, 6, 12, 10)
            .GridRow(1);


    VisualNode RenderVocabularyList()
    {
        if (!State.FilteredVocabularyItems.Any())
        {
            return VStack(
                Label(State.AllVocabularyItems.Any() ?
                    "No vocabulary words match the current filter." :
                    "No vocabulary words found. Use the + button to create your first vocabulary word.")
                    .FontSize(16)
                    .TextColor(MyTheme.Gray600)
                    .Center(),

                !State.AllVocabularyItems.Any() ?
                    Button("Get Started")
                        .ThemeKey("Primary")
                        .OnClicked(async () => await ToggleQuickAdd())
                        .HCenter()
                        .Margin(0, 20, 0, 0) :
                    null
            )
            .VCenter()
            .HCenter()
            .GridRow(0);
        }

        // Calculate optimal number of columns based on screen width
        var screenWidth = DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;
        var cardWidth = 300; // Minimum card width for good readability
        var horizontalSpacing = 16;
        var containerPadding = 32;

        var availableWidth = screenWidth - containerPadding;
        var itemWidthWithSpacing = cardWidth + horizontalSpacing;
        var calculatedSpan = Math.Max(1, (int)(availableWidth / itemWidthWithSpacing));
        var span = Math.Max(1, Math.Min(3, calculatedSpan)); // Clamp between 1-3 columns

        var gridLayout = new GridItemsLayout(span, ItemsLayoutOrientation.Vertical)
        {
            VerticalItemSpacing = 16,
            HorizontalItemSpacing = 16
        };

        return CollectionView()
            .ItemsSource(State.FilteredVocabularyItems,
                DeviceInfo.Idiom == DeviceIdiom.Phone
                    ? RenderVocabularyCardMobile
                    : RenderVocabularyCard)
            .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty,
                DeviceInfo.Idiom == DeviceIdiom.Phone
                    ? new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 8 }
                    : gridLayout)
            .BackgroundColor(Colors.Transparent)
            .ItemSizingStrategy(ItemSizingStrategy.MeasureFirstItem)
            .Margin(16)
            .GridRow(0);
    }

    VisualNode RenderVocabularyCardMobile(VocabularyCardViewModel item)
    {
        return Border(
            HStack(spacing: 8,
                // Header with select checkbox (if in multi-select mode)
                State.IsMultiSelectMode
                    ? CheckBox()
                            .IsChecked(item.IsSelected)
                            .OnCheckedChanged(isChecked => ToggleItemSelection(item.Word.Id, isChecked))
                    : null,

                // Main content - view mode only
                VStack(spacing: 2,
                    Label(item.Word.TargetLanguageTerm ?? "")
                        .FontSize(16)
                        .FontAttributes(FontAttributes.Bold)
                        .TextColor(MyTheme.HighlightDarkest),
                    Label(item.Word.NativeLanguageTerm ?? "")
                        .FontSize(14)
                        .TextColor(MyTheme.Gray600)
                )
            ).Padding(4)
        )
        .Padding(8, 4)
        .StrokeShape(new Rectangle())
        .StrokeThickness(1)
        .Stroke(item.IsOrphaned ? MyTheme.Warning : MyTheme.Gray300)
        .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground)
        .OnTapped(State.IsMultiSelectMode ?
            () => ToggleItemSelection(item.Word.Id, !item.IsSelected) :
            () => NavigateToEditPage(item.Word.Id));
    }

    VisualNode RenderVocabularyCard(VocabularyCardViewModel item)
    {
        return Border(
            VStack(spacing: 8,
                // Header with select checkbox (if in multi-select mode)
                State.IsMultiSelectMode ?
                    HStack(
                        CheckBox()
                            .IsChecked(item.IsSelected)
                            .OnCheckedChanged(isChecked => ToggleItemSelection(item.Word.Id, isChecked)),
                        Label("Select")
                            .FontSize(12)
                            .TextColor(MyTheme.Gray600)
                            .VCenter()
                    ).HStart() :
                    null,

                // Main content - view mode only
                RenderVocabularyCardViewMode(item)
            ).Padding(16)
        )
        .StrokeShape(new RoundRectangle().CornerRadius(12))
        .StrokeThickness(2)
        .Stroke(item.IsOrphaned ? MyTheme.Warning : MyTheme.Gray300)
        .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground)
        .WidthRequest(280)
        .OnTapped(State.IsMultiSelectMode ?
            () => ToggleItemSelection(item.Word.Id, !item.IsSelected) :
            () => NavigateToEditPage(item.Word.Id));
    }

    VisualNode RenderVocabularyCardViewMode(VocabularyCardViewModel item) =>
        VStack(spacing: 8,
            // Terms
            VStack(spacing: 4,
                Label(item.Word.TargetLanguageTerm ?? "")
                    .FontSize(18)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(MyTheme.HighlightDarkest),
                Label(item.Word.NativeLanguageTerm ?? "")
                    .FontSize(16)
                    .TextColor(MyTheme.Gray600)
            ),

            // Status and resources
            VStack(spacing: 6,
                Label(item.IsOrphaned ? "⚠️ Orphaned" : $"📚 {item.AssociatedResources.Count} resource(s)")
                    .FontSize(12)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(item.IsOrphaned ? MyTheme.Warning : MyTheme.Success)
            ),

            // Edit button (only show if not in multi-select mode)
            !State.IsMultiSelectMode ?
                Button("Edit")
                    .ThemeKey("Secondary")
                    .OnClicked(() => NavigateToEditPage(item.Word.Id))
                    .FontSize(12)
                    .Padding(8, 6)
                    .HEnd() :
                null
        );

    // Event handlers and logic methods
    async Task LoadData()
    {
        SetState(s => s.IsLoading = true);

        try
        {
            // Load all learning resources
            var resources = await _resourceRepo.GetAllResourcesAsync();
            SetState(s => s.AvailableResources = new ObservableCollection<LearningResource>(resources ?? new List<LearningResource>()));

            // Load all vocabulary words with their associations
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadData error: {ex}");
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to load vocabulary data. Please try again.", "OK");

            // Set safe defaults on error
            SetState(s =>
            {
                s.AllVocabularyItems = new ObservableCollection<VocabularyCardViewModel>();
                s.FilteredVocabularyItems = new ObservableCollection<VocabularyCardViewModel>();
                s.AvailableResources = new ObservableCollection<LearningResource>();
                s.Stats = new VocabularyStats();
            });
        }
        finally
        {
            SetState(s => s.IsLoading = false);
        }
    }

    async Task LoadVocabularyData()
    {
        // Load all vocabulary words with their associated learning resources
        var allWords = await _resourceRepo.GetAllVocabularyWordsWithResourcesAsync();

        var vocabularyItems = new List<VocabularyCardViewModel>();

        foreach (var word in allWords)
        {
            var item = new VocabularyCardViewModel
            {
                Word = word,
                AssociatedResources = word.LearningResources?.ToList() ?? new List<LearningResource>()
            };

            vocabularyItems.Add(item);
        }

        // Get statistics using the new repository method
        var (totalWords, associatedWords, orphanedWords) = await _resourceRepo.GetVocabularyStatsAsync();

        SetState(s =>
        {
            s.AllVocabularyItems = new ObservableCollection<VocabularyCardViewModel>(vocabularyItems);
            s.Stats = new VocabularyStats
            {
                TotalWords = totalWords,
                AssociatedWords = associatedWords,
                OrphanedWords = orphanedWords
            };
        });

        ApplyFilters();
    }

    void ApplyFilters()
    {
        var filtered = State.AllVocabularyItems.AsEnumerable();

        // Apply vocabulary filter
        filtered = State.SelectedFilter switch
        {
            VocabularyFilter.Associated => filtered.Where(v => !v.IsOrphaned),
            VocabularyFilter.Orphaned => filtered.Where(v => v.IsOrphaned),
            VocabularyFilter.SpecificResource => State.SelectedResource != null ?
                filtered.Where(v => v.AssociatedResources.Any(r => r.Id == State.SelectedResource.Id)) :
                filtered,
            _ => filtered
        };

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(State.SearchText))
        {
            var searchLower = State.SearchText.ToLower();
            filtered = filtered.Where(v =>
                (v.Word.TargetLanguageTerm?.ToLower().Contains(searchLower) == true) ||
                (v.Word.NativeLanguageTerm?.ToLower().Contains(searchLower) == true));
        }

        SetState(s => s.FilteredVocabularyItems = new ObservableCollection<VocabularyCardViewModel>(filtered.ToList()));
    }

    void OnSearchTextChanged(string searchText)
    {
        SetState(s => s.SearchText = searchText);

        // Debounce search to avoid excessive filtering
        _searchTimer?.Dispose();
        _searchTimer = new System.Threading.Timer(_ => ApplyFilters(), null, 300, Timeout.Infinite);
    }

    void OnStatusFilterChanged(int index)
    {
        var filter = (VocabularyFilter)index;
        SetState(s =>
        {
            s.SelectedFilter = filter;
            if (filter != VocabularyFilter.SpecificResource)
            {
                s.SelectedResource = null;
            }
        });
        ApplyFilters();
    }


    void OnResourcePickerIndexChanged(int index)
    {
        if (index == 0) // "All Resources" option
        {
            SetState(s =>
            {
                s.SelectedFilter = VocabularyFilter.All;
                s.SelectedResource = null;
            });
        }
        else if (index > 0 && index <= State.AvailableResources.Count)
        {
            var resource = State.AvailableResources[index - 1]; // -1 because "All Resources" is at index 0
            SetState(s =>
            {
                s.SelectedFilter = VocabularyFilter.SpecificResource;
                s.SelectedResource = resource;
            });
        }
        ApplyFilters();
    }

    List<string> GetResourcePickerItems()
    {
        var items = new List<string> { "All Resources" };
        items.AddRange(State.AvailableResources.Select(r => r.Title ?? "Unknown"));
        return items;
    }

    int GetResourcePickerSelectedIndex()
    {
        if (State.SelectedFilter != VocabularyFilter.SpecificResource || State.SelectedResource == null)
        {
            return 0; // "All Resources"
        }

        var resourceIndex = State.AvailableResources.ToList().FindIndex(r => r.Id == State.SelectedResource.Id);
        return resourceIndex >= 0 ? resourceIndex + 1 : 0; // +1 because "All Resources" is at index 0
    }

    Task ToggleQuickAdd()
    {
        // Navigate to edit page with ID 0 to indicate new vocabulary word
        return MauiControls.Shell.Current.GoToAsync<VocabularyWordProps>(
            nameof(EditVocabularyWordPage),
            props => props.VocabularyWordId = 0);
    }


    void EnterMultiSelectMode()
    {
        SetState(s =>
        {
            s.IsMultiSelectMode = true;
            s.SelectedWordIds.Clear();
            // Update all items to show they're not selected
            foreach (var item in s.AllVocabularyItems)
            {
                item.IsSelected = false;
            }
        });
    }

    void ExitMultiSelectMode()
    {
        SetState(s =>
        {
            s.IsMultiSelectMode = false;
            s.SelectedWordIds.Clear();
            // Update all items to clear selection
            foreach (var item in s.AllVocabularyItems)
            {
                item.IsSelected = false;
            }
        });
    }

    void ToggleItemSelection(int wordId, bool isSelected)
    {
        SetState(s =>
        {
            var item = s.AllVocabularyItems.FirstOrDefault(v => v.Word.Id == wordId);
            if (item != null)
            {
                item.IsSelected = isSelected;
                if (isSelected)
                {
                    s.SelectedWordIds.Add(wordId);
                }
                else
                {
                    s.SelectedWordIds.Remove(wordId);
                }
            }
        });
    }

    async Task BulkDeleteSelected()
    {
        if (!State.SelectedWordIds.Any()) return;

        bool confirm = await Application.Current.MainPage.DisplayAlert(
            "Confirm Delete",
            $"Are you sure you want to delete {State.SelectedWordIds.Count} vocabulary word(s)?",
            "Yes", "No");

        if (!confirm) return;

        try
        {
            await _resourceRepo.BulkDeleteVocabularyWordsAsync(State.SelectedWordIds.ToList());

            await AppShell.DisplayToastAsync($"🗑️ {State.SelectedWordIds.Count} vocabulary word(s) deleted!");
            ExitMultiSelectMode();
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to delete vocabulary: {ex.Message}", "OK");
        }
    }

    async Task BulkAssociateSelected()
    {
        if (!State.SelectedWordIds.Any()) return;

        // Show resource selection dialog
        var selectedResource = await ShowResourceSelectionDialog();
        if (selectedResource == null) return;

        try
        {
            await _resourceRepo.BulkAssociateWordsWithResourceAsync(selectedResource.Id, State.SelectedWordIds.ToList());

            await AppShell.DisplayToastAsync($"✅ {State.SelectedWordIds.Count} vocabulary word(s) associated with '{selectedResource.Title}'!");
            ExitMultiSelectMode();
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to associate vocabulary: {ex.Message}", "OK");
        }
    }

    async Task<LearningResource?> ShowResourceSelectionDialog()
    {
        if (!State.AvailableResources.Any())
        {
            await Application.Current.MainPage.DisplayAlert("No Resources", "No learning resources available for association.", "OK");
            return null;
        }

        var resourceNames = State.AvailableResources.Select(r => r.Title ?? "Unknown").ToArray();
        var selectedName = await Application.Current.MainPage.DisplayActionSheet(
            "Select Learning Resource", "Cancel", null, resourceNames);

        if (selectedName == "Cancel" || string.IsNullOrEmpty(selectedName))
            return null;

        return State.AvailableResources.FirstOrDefault(r => r.Title == selectedName);
    }

    Task NavigateToEditPage(int vocabularyWordId)
    {
        return MauiControls.Shell.Current.GoToAsync<VocabularyWordProps>(
            nameof(EditVocabularyWordPage),
            props => props.VocabularyWordId = vocabularyWordId);
    }

    // Bottom bar dialogs
    async Task ShowStatusFilterDialog()
    {
        var options = new[]
        {
            $"All ({State.Stats.TotalWords})",
            $"Associated ({State.Stats.AssociatedWords})",
            $"Orphaned ({State.Stats.OrphanedWords})"
        };

        var selection = await Application.Current.MainPage.DisplayActionSheet(
            "Filter by Status", "Cancel", null, options);
        if (string.IsNullOrEmpty(selection) || selection == "Cancel")
            return;

        var index = Array.IndexOf(options, selection);
        if (index >= 0)
        {
            OnStatusFilterChanged(index);
        }
    }

    async Task ShowResourceFilterDialog()
    {
        var items = GetResourcePickerItems();
        var selection = await Application.Current.MainPage.DisplayActionSheet(
            "Filter by Resource", "Cancel", null, items.ToArray());
        if (string.IsNullOrEmpty(selection) || selection == "Cancel")
            return;

        var index = items.IndexOf(selection);
        // Index maps 1..N => specific resource, 0 => All Resources
        OnResourcePickerIndexChanged(index);
    }

    public void Dispose()
    {
        _searchTimer?.Dispose();
    }
}