using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

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

    // Quick add
    public bool IsQuickAddExpanded { get; set; } = false;
    public string QuickAddTargetTerm { get; set; } = string.Empty;
    public string QuickAddNativeTerm { get; set; } = string.Empty;
    public HashSet<int> QuickAddResourceIds { get; set; } = new();
    public bool IsQuickAddSaving { get; set; } = false;
}

partial class VocabularyManagementPage : Component<VocabularyManagementPageState>, IDisposable
{
    [Inject] LearningResourceRepository _resourceRepo;
    private System.Threading.Timer? _searchTimer;

    public override VisualNode Render()
    {
        return ContentPage("Vocabulary Management",
            State.IsLoading ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "Auto,Auto,*", columns: "*",
                    RenderSearchAndFilters(),
                    RenderQuickAdd(),
                    RenderVocabularyList()
                )
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

    VisualNode RenderSearchAndFilters() =>
        VStack(spacing: 12,
            // Search bar
            Border(
                Entry()
                    .Placeholder("Search vocabulary...")
                    .Text(State.SearchText)
                    .OnTextChanged(OnSearchTextChanged)
            )
            .ThemeKey(MyTheme.InputWrapper),

            // Filter buttons with counts
            ScrollView(
                HStack(spacing: 8,
                    Button($"All ({State.Stats.TotalWords})")
                        .Background(State.SelectedFilter == VocabularyFilter.All ? MyTheme.HighlightDarkest : MyTheme.Gray200Brush)
                        .TextColor(State.SelectedFilter == VocabularyFilter.All ? Colors.White : MyTheme.Gray600)
                        .OnClicked(() => OnFilterChanged(VocabularyFilter.All)),
                    Button($"Associated ({State.Stats.AssociatedWords})")
                        .Background(State.SelectedFilter == VocabularyFilter.Associated ? MyTheme.SupportSuccessDark : MyTheme.Gray200Brush)
                        .TextColor(State.SelectedFilter == VocabularyFilter.Associated ? Colors.White : MyTheme.Gray600)
                        .OnClicked(() => OnFilterChanged(VocabularyFilter.Associated)),
                    Button($"Orphaned ({State.Stats.OrphanedWords})")
                        .Background(State.SelectedFilter == VocabularyFilter.Orphaned ? MyTheme.SupportErrorDark : MyTheme.Gray200Brush)
                        .TextColor(State.SelectedFilter == VocabularyFilter.Orphaned ? Colors.White : MyTheme.Gray600)
                        .OnClicked(() => OnFilterChanged(VocabularyFilter.Orphaned)),
                    Button("By Resource")
                        .Background(State.SelectedFilter == VocabularyFilter.SpecificResource ? MyTheme.HighlightDarkest : MyTheme.Gray200Brush)
                        .TextColor(State.SelectedFilter == VocabularyFilter.SpecificResource ? Colors.White : MyTheme.Gray600)
                        .OnClicked(() => OnFilterChanged(VocabularyFilter.SpecificResource))
                )
                .Padding(8, 0)
            )
            .Orientation(ScrollOrientation.Horizontal)
            .HorizontalScrollBarVisibility(ScrollBarVisibility.Never),

            // Resource picker (shown when SpecificResource filter is selected)
            State.SelectedFilter == VocabularyFilter.SpecificResource ?
                Picker()
                    .ItemsSource(State.AvailableResources.Select(r => r.Title).ToList())
                    .SelectedIndex(State.SelectedResource != null ?
                        State.AvailableResources.ToList().FindIndex(r => r.Id == State.SelectedResource.Id) : -1)
                    .OnSelectedIndexChanged(index => OnResourceFilterIndexChanged(index))
                    .Title("Select Learning Resource") :
                null,

            // Multi-select toggle and bulk actions
            State.IsMultiSelectMode ?
                HStack(spacing: 10,
                    Button("Cancel Selection")
                        .ThemeKey("Secondary")
                        .OnClicked(ExitMultiSelectMode),
                    Label($"{State.SelectedWordIds.Count} selected")
                        .VCenter()
                        .TextColor(MyTheme.Gray600),
                    Button("Delete Selected")
                        .ThemeKey("Danger")
                        .OnClicked(BulkDeleteSelected)
                        .IsEnabled(State.SelectedWordIds.Any()),
                    Button("Associate")
                        .ThemeKey("Primary")
                        .OnClicked(BulkAssociateSelected)
                        .IsEnabled(State.SelectedWordIds.Any())
                ) :
                Button("Multi-Select")
                    .ThemeKey("Secondary")
                    .OnClicked(EnterMultiSelectMode)
                    .HEnd()
        ).Padding(16, 8, 16, 8).GridRow(0);

    VisualNode RenderQuickAdd() =>
        VStack(spacing: 10,
            Button(State.IsQuickAddExpanded ? "− Hide Quick Add" : "+ Quick Add Vocabulary")
                .ThemeKey("Secondary")
                .OnClicked(ToggleQuickAdd)
                .HStart(),

            State.IsQuickAddExpanded ?
                Border(
                    VStack(spacing: 12,
                        HStack(spacing: 10,
                            VStack(spacing: 5,
                                Label("Target Language")
                                    .FontSize(12)
                                    .FontAttributes(FontAttributes.Bold),
                                Entry()
                                    .Text(State.QuickAddTargetTerm)
                                    .OnTextChanged(text => SetState(s => s.QuickAddTargetTerm = text))
                                    .Placeholder("e.g., 안녕하세요")
                                    .ReturnType(ReturnType.Next)
                            ).HorizontalOptions(LayoutOptions.FillAndExpand),

                            VStack(spacing: 5,
                                Label("Native Language")
                                    .FontSize(12)
                                    .FontAttributes(FontAttributes.Bold),
                                Entry()
                                    .Text(State.QuickAddNativeTerm)
                                    .OnTextChanged(text => SetState(s => s.QuickAddNativeTerm = text))
                                    .Placeholder("e.g., Hello")
                                    .ReturnType(ReturnType.Done)
                                    .OnCompleted(QuickAddVocabulary)
                            ).HorizontalOptions(LayoutOptions.FillAndExpand)
                        ),

                        Label("Associate with Resources (optional)")
                            .FontSize(12)
                            .FontAttributes(FontAttributes.Bold)
                            .HStart(),

                        State.AvailableResources.Any() ?
                            ScrollView(
                                HStack(spacing: 8,
                                    State.AvailableResources.Select(resource =>
                                        Button(resource.Title ?? "Unknown")
                                            .Background(State.QuickAddResourceIds.Contains(resource.Id) ?
                                                MyTheme.HighlightDarkest : MyTheme.Gray200Brush)
                                            .TextColor(State.QuickAddResourceIds.Contains(resource.Id) ?
                                                Colors.White : MyTheme.Gray600)
                                            .OnClicked(() => ToggleQuickAddResource(resource.Id))
                                            .FontSize(12)
                                            .Padding(8, 6)
                                    ).ToArray()
                                ).Padding(8, 0)
                            )
                            .Orientation(ScrollOrientation.Horizontal)
                            .HeightRequest(50) :
                            Label("No learning resources available")
                                .FontSize(12)
                                .TextColor(MyTheme.Gray600),

                        HStack(spacing: 10,
                            Button("Add Vocabulary")
                                .ThemeKey("Primary")
                                .OnClicked(QuickAddVocabulary)
                                .IsEnabled(!State.IsQuickAddSaving &&
                                          !string.IsNullOrWhiteSpace(State.QuickAddTargetTerm?.Trim()) &&
                                          !string.IsNullOrWhiteSpace(State.QuickAddNativeTerm?.Trim())),

                            State.IsQuickAddSaving ?
                                ActivityIndicator()
                                    .IsRunning(true)
                                    .Scale(0.8) :
                                null,

                            Button("Clear")
                                .ThemeKey("Secondary")
                                .OnClicked(ClearQuickAdd)
                        ).HStart()
                    ).Padding(16)
                )
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .Stroke(MyTheme.Gray300)
                .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground) :
                null
        ).Padding(16, 8, 16, 8).GridRow(1);

    VisualNode RenderVocabularyList()
    {
        if (!State.FilteredVocabularyItems.Any())
        {
            return VStack(
                Label(State.AllVocabularyItems.Any() ?
                    "No vocabulary words match the current filter." :
                    "No vocabulary words found. Use Quick Add to create your first vocabulary word.")
                    .FontSize(16)
                    .TextColor(MyTheme.Gray600)
                    .Center(),

                !State.AllVocabularyItems.Any() ?
                    Button("Get Started")
                        .ThemeKey("Primary")
                        .OnClicked(() => SetState(s => s.IsQuickAddExpanded = true))
                        .HCenter()
                        .Margin(0, 20, 0, 0) :
                    null
            )
            .VCenter()
            .HCenter()
            .GridRow(2);
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
            .GridRow(2);
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

    void OnFilterChanged(VocabularyFilter filter)
    {
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

    void OnResourceFilterChanged(object selectedItem)
    {
        if (selectedItem is LearningResource resource)
        {
            SetState(s => s.SelectedResource = resource);
            ApplyFilters();
        }
    }

    void OnResourceFilterIndexChanged(int index)
    {
        if (index >= 0 && index < State.AvailableResources.Count)
        {
            var resource = State.AvailableResources[index];
            SetState(s => s.SelectedResource = resource);
            ApplyFilters();
        }
    }

    void ToggleQuickAdd()
    {
        SetState(s => s.IsQuickAddExpanded = !s.IsQuickAddExpanded);
    }

    void ToggleQuickAddResource(int resourceId)
    {
        SetState(s =>
        {
            if (s.QuickAddResourceIds.Contains(resourceId))
            {
                s.QuickAddResourceIds.Remove(resourceId);
            }
            else
            {
                s.QuickAddResourceIds.Add(resourceId);
            }
        });
    }

    async Task QuickAddVocabulary()
    {
        SetState(s => s.IsQuickAddSaving = true);

        try
        {
            var targetTerm = State.QuickAddTargetTerm.Trim();
            var nativeTerm = State.QuickAddNativeTerm.Trim();

            // Check for duplicate
            var existingWord = await _resourceRepo.FindDuplicateVocabularyWordAsync(targetTerm, nativeTerm);
            if (existingWord != null)
            {
                bool shouldProceed = await Application.Current.MainPage.DisplayAlert(
                    "Duplicate Found",
                    $"A vocabulary word with these terms already exists. Do you want to associate it with the selected resources instead?",
                    "Yes", "No");

                if (shouldProceed)
                {
                    // Associate existing word with selected resources
                    foreach (var resourceId in State.QuickAddResourceIds)
                    {
                        await _resourceRepo.AddVocabularyToResourceAsync(resourceId, existingWord.Id);
                    }

                    await AppShell.DisplayToastAsync("✅ Existing vocabulary word associated with resources!");
                }

                ClearQuickAdd();
                await LoadVocabularyData();
                return;
            }

            var newWord = new VocabularyWord
            {
                TargetLanguageTerm = targetTerm,
                NativeLanguageTerm = nativeTerm,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Save the word
            await _resourceRepo.SaveWordAsync(newWord);

            // Associate with selected resources
            foreach (var resourceId in State.QuickAddResourceIds)
            {
                await _resourceRepo.AddVocabularyToResourceAsync(resourceId, newWord.Id);
            }

            await AppShell.DisplayToastAsync("✅ Vocabulary word added successfully!");

            // Clear form and reload data
            ClearQuickAdd();
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to add vocabulary: {ex.Message}", "OK");
        }
        finally
        {
            SetState(s => s.IsQuickAddSaving = false);
        }
    }

    void ClearQuickAdd()
    {
        SetState(s =>
        {
            s.QuickAddTargetTerm = string.Empty;
            s.QuickAddNativeTerm = string.Empty;
            s.QuickAddResourceIds.Clear();
        });
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

    public void Dispose()
    {
        _searchTimer?.Dispose();
    }
}