using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Helpers;
using Microsoft.Extensions.Logging;

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

    // Progress tracking
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }

    // Status helpers (matching VocabularyProgressItem pattern)
    public bool IsKnown => Progress?.IsKnown ?? false;
    public bool IsLearning => Progress?.IsLearning ?? false;
    public bool IsUnknown => Progress == null || (!Progress.IsKnown && !Progress.IsLearning);

    public Color StatusColor => IsKnown ? MyTheme.Success :
                                IsLearning ? MyTheme.Warning :
                                MyTheme.Gray400;

    public string StatusText
    {
        get
        {
            var localize = LocalizationManager.Instance;
            return IsKnown ? $"{localize["Known"]}" :
                            IsLearning ? $"{localize["Learning"]}" :
                            $"{localize["Unknown"]}";
        }
    }
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

    // Cleanup operations
    public bool IsCleanupSheetOpen { get; set; } = false;
    public bool IsCleanupRunning { get; set; } = false;

    // Platform cache
    public bool IsPhoneIdiom { get; set; }

}

partial class VocabularyManagementPage : Component<VocabularyManagementPageState>, IDisposable
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] UserProfileRepository _userProfileRepo;
    [Inject] VocabularyProgressService _progressService;
    [Inject] ILogger<VocabularyManagementPage> _logger;
    private System.Threading.Timer? _searchTimer;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["VocabularyManagement"]}",
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text($"{_localize["Add"]}")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = FluentUI.add_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(async () => await ToggleQuickAdd()),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text($"{_localize["Select"]}")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = State.IsMultiSelectMode ? FluentUI.dismiss_20_regular : FluentUI.select_all_on_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(State.IsMultiSelectMode ? ExitMultiSelectMode : EnterMultiSelectMode),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text($"{_localize["Cleanup"]}")
                .IconImageSource(new FontImageSource
                {
                    FontFamily = FluentUI.FontFamily,
                    Glyph = FluentUI.broom_20_regular,
                    Color = MyTheme.HighlightDarkest
                })
                .OnClicked(() => SetState(s => s.IsCleanupSheetOpen = true)),
            State.IsLoading ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "*,Auto", columns: "*",
                    RenderVocabularyList(),
                    RenderBottomBar(),
                    RenderCleanupSheet()
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

    protected override void OnMounted()
    {
        base.OnMounted();
        // Cache device idiom check for performance optimization
        SetState(s => s.IsPhoneIdiom = DeviceInfo.Idiom == DeviceIdiom.Phone);
    }

    // Bottom bar with compact search and icon filters (or bulk actions in multi-select mode)
    VisualNode RenderBottomBar()
        => State.IsMultiSelectMode ? RenderBulkActionsBar() : RenderCompactSearchBar();

    VisualNode RenderCompactSearchBar()
        =>
                Grid(rows: "Auto", columns: "*,Auto,Auto",
                        new SfTextInputLayout(
                            Entry()
                                .Placeholder($"{_localize["Search"]}")
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

                    // Status filter icon
                    ImageButton()
                        .Source(MyTheme.IconFilter)
                        .BackgroundColor(MyTheme.LightSecondaryBackground)
                        .HeightRequest(36)
                        .WidthRequest(36)
                        .CornerRadius(18)
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
                ).ColumnSpacing(MyTheme.LayoutSpacing)
            .Padding(MyTheme.LayoutSpacing, 0)
            .GridRow(1);

    VisualNode RenderBulkActionsBar()
        => Border(
                HStack(spacing: MyTheme.LayoutSpacing,
                    Label($"{State.SelectedWordIds.Count} selected")
                        .ThemeKey(MyTheme.Body2)
                        .VCenter()
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
            .Padding(MyTheme.CardPadding, MyTheme.ComponentSpacing)
            .Margin(new Thickness(MyTheme.CardMargin, MyTheme.MicroSpacing, MyTheme.CardMargin, MyTheme.ComponentSpacing))
            .GridRow(1);


    VisualNode RenderVocabularyList()
    {
        if (!State.FilteredVocabularyItems.Any())
        {
            return VStack(
                Label(State.AllVocabularyItems.Any() ?
                    $"{_localize["NoMatchFilter"]}" :
                    $"{_localize["NoVocabularyWords"]}")
                    .ThemeKey(MyTheme.Body1)
                    .Center(),

                !State.AllVocabularyItems.Any() ?
                    Button($"{_localize["GetStarted"]}")
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

        return CollectionView()
            .ItemsSource(State.FilteredVocabularyItems,
                State.IsPhoneIdiom
                    ? RenderVocabularyCardMobile
                    : RenderVocabularyCard)
            .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty,
                State.IsPhoneIdiom
                    ? new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = MyTheme.LayoutSpacing }
                    : GridLayoutHelper.CalculateResponsiveLayout(desiredItemWidth: 300, maxColumns: 3))
            .BackgroundColor(Colors.Transparent)
            .ItemSizingStrategy(ItemSizingStrategy.MeasureFirstItem)
            .Margin(MyTheme.LayoutPadding)
            .GridRow(0);
    }

    VisualNode RenderVocabularyCardMobile(VocabularyCardViewModel item)
    {
        return Border(
            HStack(spacing: MyTheme.LayoutSpacing,
                // Header with select checkbox (if in multi-select mode)
                State.IsMultiSelectMode
                    ? CheckBox()
                            .IsChecked(item.IsSelected)
                            .OnCheckedChanged(isChecked => ToggleItemSelection(item.Word.Id, isChecked))
                    : null,

                // Main content - view mode only
                VStack(spacing: MyTheme.MicroSpacing,
                    Label(item.Word.TargetLanguageTerm ?? "")
                        .ThemeKey(MyTheme.Body2Strong),
                    Label(item.Word.NativeLanguageTerm ?? "")
                        .ThemeKey(MyTheme.Caption1),
                    // Progress status
                    Label(item.StatusText)
                        .ThemeKey(MyTheme.Caption2)
                        .TextColor(item.StatusColor)
                )
            ).Padding(MyTheme.MicroSpacing)
        )
        .Padding(MyTheme.ComponentSpacing, MyTheme.MicroSpacing)
        .StrokeShape(new Rectangle())
        .StrokeThickness(1)
        .Stroke(item.StatusColor)
        .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground)
        .OnTapped(State.IsMultiSelectMode ?
            () => ToggleItemSelection(item.Word.Id, !item.IsSelected) :
            () => NavigateToEditPage(item.Word.Id));
    }

    VisualNode RenderVocabularyCard(VocabularyCardViewModel item)
    {
        return Border(
            VStack(
                // Header with select checkbox (if in multi-select mode)
                State.IsMultiSelectMode ?
                    HStack(
                        CheckBox()
                            .IsChecked(item.IsSelected)
                            .OnCheckedChanged(isChecked => ToggleItemSelection(item.Word.Id, isChecked)),
                        Label($"{_localize["Select"]}")
                            .ThemeKey(MyTheme.Caption2)
                            .VCenter()
                    ).HStart() :
                    null,

                // Main content - view mode only
                RenderVocabularyCardViewMode(item)
            )
        )
        .OnTapped(State.IsMultiSelectMode ?
            () => ToggleItemSelection(item.Word.Id, !item.IsSelected) :
            () => NavigateToEditPage(item.Word.Id));
    }

    VisualNode RenderVocabularyCardViewMode(VocabularyCardViewModel item) =>
        VStack(
            // Terms            
            Label(item.Word.TargetLanguageTerm ?? "")
                .ThemeKey(MyTheme.Body1Strong),
            Label(item.Word.NativeLanguageTerm ?? "")
                .ThemeKey(MyTheme.Body2),

            // Progress status
            Label(item.StatusText)
                .ThemeKey(MyTheme.Caption1)
                .TextColor(item.StatusColor),

            // Resource association status
            Label(item.IsOrphaned ? $"{_localize["Orphaned"]}" : string.Format($"{_localize["ResourceCount"]}", item.AssociatedResources.Count))
                .ThemeKey(MyTheme.Caption2)
                .TextColor(item.IsOrphaned ? MyTheme.Warning : MyTheme.Gray500)

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
            _logger.LogError(ex, "LoadData error");
            await Application.Current.MainPage.DisplayAlert($"{_localize["Error"]}", $"{_localize["FailedToLoadVocabularyData"]}", $"{_localize["OK"]}");

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

        // Get progress for all words efficiently
        var wordIds = allWords.Select(w => w.Id).ToList();
        var progressData = await _progressService.GetProgressForWordsAsync(wordIds);

        var vocabularyItems = new List<VocabularyCardViewModel>();

        foreach (var word in allWords)
        {
            var item = new VocabularyCardViewModel
            {
                Word = word,
                AssociatedResources = word.LearningResources?.ToList() ?? new List<LearningResource>(),
                Progress = progressData.ContainsKey(word.Id) ? progressData[word.Id] : null
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
            $"{_localize["ConfirmDelete"]}",
            string.Format($"{_localize["ConfirmDeleteMultiple"]}", State.SelectedWordIds.Count),
            $"{_localize["Yes"]}", $"{_localize["No"]}");

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
            await Application.Current.MainPage.DisplayAlert($"{_localize["Error"]}", string.Format($"{_localize["FailedToDeleteVocabulary"]}", ex.Message), $"{_localize["OK"]}");
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
            await Application.Current.MainPage.DisplayAlert($"{_localize["Error"]}", string.Format($"{_localize["FailedToAssociateVocabulary"]}", ex.Message), $"{_localize["OK"]}");
        }
    }

    async Task<LearningResource?> ShowResourceSelectionDialog()
    {
        if (!State.AvailableResources.Any())
        {
            await Application.Current.MainPage.DisplayAlert($"{_localize["NoResources"]}", "No learning resources available for association.", $"{_localize["OK"]}");
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

    VisualNode RenderCleanupSheet()
        => new SfBottomSheet(
            VStack(
                Label($"{_localize["VocabularyCleanup"]}")
                    .FontSize(20)
                    .FontAttributes(MauiControls.FontAttributes.Bold)
                    .Margin(0, 0, 0, 8),

                Label($"{_localize["FixSwappedLanguages"]}")
                    .FontSize(16)
                    .FontAttributes(MauiControls.FontAttributes.Bold),
                Label($"{_localize["SwapLanguagesDescription"]}")
                    .FontSize(12)
                    .Opacity(0.7)
                    .Margin(0, 0, 0, 4),
                Button($"{_localize["RunLanguageSwapCleanup"]}")
                    .OnClicked(RunLanguageSwapCleanup)
                    .IsEnabled(!State.IsCleanupRunning),

                BoxView()
                    .HeightRequest(1)
                    .BackgroundColor(Colors.Gray.WithAlpha(0.3f))
                    .Margin(0, 8),

                Label($"{_localize["AssignOrphanedWords"]}")
                    .FontSize(16)
                    .FontAttributes(MauiControls.FontAttributes.Bold),
                Label($"{_localize["AssignOrphanedDescription"]}")
                    .FontSize(12)
                    .Opacity(0.7)
                    .Margin(0, 0, 0, 4),
                Button($"{_localize["RunOrphanAssignment"]}")
                    .OnClicked(RunOrphanAssignment)
                    .IsEnabled(!State.IsCleanupRunning),

                State.IsCleanupRunning
                    ? HStack(
                        ActivityIndicator().IsRunning(true),
                        Label($"{_localize["RunningCleanup"]}").VCenter()
                    ).Spacing(8).HCenter().Margin(8, 16, 8, 0)
                    : null
            )
            .Spacing(16)
            .Padding(24)
        )
        .IsOpen(State.IsCleanupSheetOpen)
        .OnStateChanged((sender, args) => SetState(s => s.IsCleanupSheetOpen = !s.IsCleanupSheetOpen));

    async Task RunLanguageSwapCleanup()
    {
        SetState(s => s.IsCleanupRunning = true);

        try
        {
            var allWords = await _resourceRepo.GetAllVocabularyWordsWithResourcesAsync();
            int swappedCount = 0;
            int mergedCount = 0;

            // Collect words that need swapping to avoid context issues
            var wordsToSwap = new List<(int Id, string NewTarget, string NewNative)>();
            var wordsToMerge = new List<(int FromId, int ToId)>();

            foreach (var word in allWords)
            {
                if (string.IsNullOrWhiteSpace(word.TargetLanguageTerm) ||
                    string.IsNullOrWhiteSpace(word.NativeLanguageTerm))
                    continue;

                bool targetIsEnglish = IsEnglish(word.TargetLanguageTerm);
                bool nativeIsKorean = IsKorean(word.NativeLanguageTerm);

                if (targetIsEnglish && nativeIsKorean)
                {
                    var swappedTarget = word.NativeLanguageTerm;
                    var swappedNative = word.TargetLanguageTerm;

                    var existingWord = await _resourceRepo.FindDuplicateVocabularyWordAsync(swappedTarget, swappedNative);

                    if (existingWord != null && existingWord.Id != word.Id)
                    {
                        // Mark for merge
                        wordsToMerge.Add((word.Id, existingWord.Id));
                    }
                    else
                    {
                        // Mark for swap
                        wordsToSwap.Add((word.Id, swappedTarget, swappedNative));
                    }
                }
            }

            // Now perform the actual updates in separate operations
            foreach (var (fromId, toId) in wordsToMerge)
            {
                var wordResources = await _resourceRepo.GetResourcesContainingWordAsync(fromId);
                foreach (var resource in wordResources)
                {
                    await _resourceRepo.AddVocabularyToResourceAsync(resource.Id, toId);
                }
                await _resourceRepo.DeleteVocabularyWordAsync(fromId);
                mergedCount++;
            }

            foreach (var (id, newTarget, newNative) in wordsToSwap)
            {
                await _resourceRepo.UpdateVocabularyWordTermsAsync(id, newTarget, newNative);
                swappedCount++;
            }

            await AppShell.DisplayToastAsync($"🔄 Swapped {swappedCount} word(s), merged {mergedCount} duplicate(s)!");
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", $"Cleanup failed: {ex.Message}", "OK");
        }
        finally
        {
            SetState(s =>
            {
                s.IsCleanupRunning = false;
                s.IsCleanupSheetOpen = false;
            });
        }
    }

    async Task RunOrphanAssignment()
    {
        SetState(s => s.IsCleanupRunning = true);

        try
        {
            // Get user profile for fallback language
            var userProfile = await _userProfileRepo.GetOrCreateDefaultAsync();
            var targetLanguage = userProfile?.TargetLanguage ?? "Korean";

            var generalResource = State.AvailableResources.FirstOrDefault(r =>
                r.Title == "General Vocabulary" && r.MediaType == "Vocabulary List");

            if (generalResource == null)
            {
                generalResource = new LearningResource
                {
                    Title = "General Vocabulary",
                    Description = "Catch-all vocabulary list for words without a specific learning resource",
                    MediaType = "Vocabulary List",
                    Language = targetLanguage,
                    Tags = "general,unassigned",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _resourceRepo.SaveResourceAsync(generalResource);
            }

            var orphanedWords = await _resourceRepo.GetOrphanedVocabularyWordsAsync();

            if (orphanedWords.Any())
            {
                var orphanIds = orphanedWords.Select(w => w.Id).ToList();
                await _resourceRepo.BulkAssociateWordsWithResourceAsync(generalResource.Id, orphanIds);

                await AppShell.DisplayToastAsync($"📦 Assigned {orphanedWords.Count} orphaned word(s) to 'General Vocabulary'!");
                await LoadVocabularyData();
            }
            else
            {
                await AppShell.DisplayToastAsync("✨ No orphaned words found!");
            }
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", $"Cleanup failed: {ex.Message}", "OK");
        }
        finally
        {
            SetState(s =>
            {
                s.IsCleanupRunning = false;
                s.IsCleanupSheetOpen = false;
            });
        }
    }

    static bool IsEnglish(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.All(c => c <= 127 || char.IsWhiteSpace(c));
    }

    static bool IsKorean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Any(c =>
            (c >= 0xAC00 && c <= 0xD7AF) ||
            (c >= 0x1100 && c <= 0x11FF) ||
            (c >= 0x3130 && c <= 0x318F));
    }

    public void Dispose()
    {
        _searchTimer?.Dispose();
    }
}