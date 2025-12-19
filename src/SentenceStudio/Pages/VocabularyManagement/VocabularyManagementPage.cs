using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Helpers;
using Microsoft.Extensions.Logging;
using SentenceStudio.Models;
using SentenceStudio.Services;

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

// Helper class for tag autocomplete popup
public class TagSelectionItem
{
    public string Tag { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

// [T040] Helper class for resource autocomplete popup
public class ResourceSelectionItem
{
    public int ResourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

// [T048] Helper class for lemma autocomplete popup
public class LemmaSelectionItem
{
    public string Lemma { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

// [T057] Helper class for status autocomplete popup
public class StatusSelectionItem
{
    public string Status { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}

class VocabularyManagementPageState
{
    public bool IsLoading { get; set; } = true;
    public ObservableCollection<VocabularyCardViewModel> AllVocabularyItems { get; set; } = new();
    public ObservableCollection<VocabularyCardViewModel> FilteredVocabularyItems { get; set; } = new();
    public ObservableCollection<LearningResource> AvailableResources { get; set; } = new();

    // [T012] GitHub-style search syntax (replaces old filter properties)
    public string RawSearchQuery { get; set; } = string.Empty;  // User's typed text
    public ParsedQuery? ParsedQuery { get; set; }               // Parsed filter structure
    public List<FilterChip> ActiveFilterChips { get; set; } = new();

    // [T012] Autocomplete state
    public bool ShowAutocomplete { get; set; } = false;
    public string AutocompleteFilterType { get; set; } = string.Empty;
    public string AutocompletePartialValue { get; set; } = string.Empty;
    public List<AutocompleteSuggestion> AutocompleteSuggestions { get; set; } = new();

    // Legacy search and filtering (kept for backward compatibility during transition)
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

    // [US3] Encoding metadata filtering and sorting
    public string? SelectedTag { get; set; }
    public bool SortByEncoding { get; set; } = false;
    public List<string> AvailableTags { get; set; } = new();
    // [T047] Available lemmas for autocomplete
    public List<string> AvailableLemmas { get; set; } = new();

    // Filter Bottom Sheet state (unified for all filter types)
    public bool IsFilterSheetOpen { get; set; } = false;
    public string ActiveFilterType { get; set; } = string.Empty; // "tag", "resource", "lemma", "status"
    public HashSet<string> PendingFilterSelections { get; set; } = new(); // Pending selections before Apply
}

partial class VocabularyManagementPage : Component<VocabularyManagementPageState>, IDisposable
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] UserProfileRepository _userProfileRepo;
    [Inject] VocabularyProgressService _progressService;
    [Inject] ILogger<VocabularyManagementPage> _logger;
    [Inject] ISearchQueryParser _searchParser;  // [T012] GitHub-style search parser
    [Inject] VocabularyEncodingRepository _encodingRepo;
    [Inject] EncodingStrengthCalculator _encodingCalculator;
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
                    RenderCleanupSheet(),
                    RenderFilterBottomSheet()
                )
                .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )
        .OnAppearing(() =>
        {
            // PERF: Defer data loading to allow page transition to complete first
            // Using BeginInvokeOnMainThread ensures the page renders before heavy I/O starts
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // Small delay to ensure page transition animation completes
                await Task.Delay(50);
                await LoadData();
            });
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
                Grid(rows: "Auto,Auto,Auto", columns: "*,Auto",
                        new SfTextInputLayout(
                            Entry()
                                .Placeholder($"{_localize["SearchVocabulary"]}")
                                .Text(State.RawSearchQuery)
                                .OnTextChanged(OnSearchTextUpdated)  // Just update state, no autocomplete
                                .OnCompleted(OnSearchSubmitted)      // Execute search on Enter/Return
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
                        .TrailingView(
                            // Clear button (only show when there's content)
                            !string.IsNullOrWhiteSpace(State.RawSearchQuery) ?
                                ImageButton()
                                    .Source(MyTheme.IconClose)
                                    .HeightRequest(20)
                                    .WidthRequest(20)
                                    .OnClicked(ClearAllFilters)
                                    .Background(Colors.Transparent) :
                                null
                        )
                        .HeightRequest(54)
                        .FocusedStrokeThickness(0)
                        .UnfocusedStrokeThickness(0)
                        .HFill()
                        .GridColumn(0)
                        .GridRow(1),

                        // Filter buttons (GitHub-style dropdowns)
                        RenderFilterButtons()

                ).RowSpacing(MyTheme.MicroSpacing)
            .Padding(MyTheme.LayoutSpacing, 0)
            .GridRow(1);

    // GitHub-style filter buttons that open bottom sheet with chip selection
    VisualNode RenderFilterButtons()
    {
        return HStack(spacing: 4,
            // Tag filter button
            State.AvailableTags.Any() ?
                ImageButton()
                    .Source(MyTheme.IconTag)
                    .Background(Colors.Transparent)
                    .VCenter()
                    .OnClicked(() => OpenFilterSheet("tag")) :
                null,

            // Resource filter button
            State.AvailableResources.Any() ?
                ImageButton()
                    .Source(MyTheme.IconResource)
                    .Background(Colors.Transparent)
                    .VCenter()
                    .OnClicked(() => OpenFilterSheet("resource")) :
                null,

            // Lemma filter button
            State.AvailableLemmas.Any() ?
                ImageButton()
                    .Source(MyTheme.IconLemma)
                    .Background(Colors.Transparent)
                    .VCenter()
                    .OnClicked(() => OpenFilterSheet("lemma")) :
                null,

            // Status filter button
            ImageButton()
                .Source(MyTheme.IconStatusFilter)
                .Background(Colors.Transparent)
                .VCenter()
                .OnClicked(() => OpenFilterSheet("status"))
        ).GridColumn(1).GridRow(1).VFill();
    }

    VisualNode RenderBulkActionsBar()
        => Border(
                HStack(spacing: MyTheme.LayoutSpacing,
                    Label(string.Format($"{_localize["Selected"]}", State.SelectedWordIds.Count))
                        .ThemeKey(MyTheme.Body2)
                        .VCenter()
                        .HFill(),
                    Button($"{_localize["Delete"]}")
                        .ThemeKey("Danger")
                        .OnClicked(BulkDeleteSelected)
                        .IsEnabled(State.SelectedWordIds.Any()),
                    Button($"{_localize["Associate"]}")
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
            .Background(Colors.Transparent)
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

    // [US3-T044] Render encoding strength badge for vocabulary card
    VisualNode RenderEncodingBadge(VocabularyWord word)
    {
        // Calculate encoding strength
        var exampleCount = word.ExampleSentences?.Count ?? 0;
        var score = _encodingCalculator.Calculate(word, exampleCount);
        var label = _encodingCalculator.GetLabel(score);

        // Determine badge color based on strength
        var badgeColor = label switch
        {
            "Basic" => MyTheme.Warning,
            "Good" => MyTheme.Gray400,
            "Strong" => MyTheme.Success,
            _ => MyTheme.Gray300
        };

        var localizedLabel = label switch
        {
            "Basic" => $"{_localize["EncodingStrengthBasic"]}",
            "Good" => $"{_localize["EncodingStrengthGood"]}",
            "Strong" => $"{_localize["EncodingStrengthStrong"]}",
            _ => label
        };

        return Border(
            Label(localizedLabel)
                .FontSize(10)
                .TextColor(Colors.White)
                .Padding(4, 2)
        )
        .VStart()
        .Background(badgeColor)
        .StrokeThickness(0)
        .StrokeShape(new RoundRectangle().CornerRadius(4));
    }

    VisualNode RenderVocabularyCardViewMode(VocabularyCardViewModel item) =>
        VStack(
            // Terms            
            Label(item.Word.TargetLanguageTerm ?? "")
                .ThemeKey(MyTheme.Body1Strong),
            Label(item.Word.NativeLanguageTerm ?? "")
                .ThemeKey(MyTheme.Body2),

            // [US3-T044] Inline encoding strength indicator
            HStack(spacing: MyTheme.MicroSpacing,
                // Progress status
                Label(item.StatusText)
                    .ThemeKey(MyTheme.Caption1)
                    .TextColor(item.StatusColor),

                // Encoding strength badge
                RenderEncodingBadge(item.Word)
            ),

            // Resource association status
            Label(item.IsOrphaned ? $"{_localize["Orphaned"]}" : string.Format($"{_localize["ResourceCount"]}", item.AssociatedResources.Count))
                .ThemeKey(MyTheme.Caption2)
                .TextColor(item.IsOrphaned ? MyTheme.Warning : MyTheme.Gray500)

        );

    // Event handlers and logic methods
    async Task LoadData()
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("🚀 LoadData START (OPTIMIZED - parallel queries)");

        SetState(s => s.IsLoading = true);

        try
        {
            // OPTIMIZATION: Run all independent queries in PARALLEL
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Start all tasks simultaneously
            var resourcesTask = _resourceRepo.GetAllResourcesLightweightAsync(); // FIX: Use lightweight version
            var tagsTask = _encodingRepo.GetAllTagsAsync();
            var lemmasTask = _encodingRepo.GetAllLemmasAsync();

            // Wait for all to complete
            await Task.WhenAll(resourcesTask, tagsTask, lemmasTask);
            sw.Stop();

            var resources = await resourcesTask;
            var tags = await tagsTask;
            var lemmas = await lemmasTask;

            _logger.LogInformation("⚡ PARALLEL queries completed: {ElapsedMs}ms (resources={ResourceCount}, tags={TagCount}, lemmas={LemmaCount})",
                sw.ElapsedMilliseconds, resources?.Count ?? 0, tags?.Count ?? 0, lemmas?.Count ?? 0);

            SetState(s =>
            {
                s.AvailableResources = new ObservableCollection<LearningResource>(resources ?? new List<LearningResource>());
                s.AvailableTags = tags;
                s.AvailableLemmas = lemmas;
            });

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
            totalStopwatch.Stop();
            _logger.LogInformation("✅ LoadData COMPLETE: {TotalMs}ms total", totalStopwatch.ElapsedMilliseconds);
        }
    }

    // [US3-T042] Load vocabulary with encoding strength
    async Task LoadVocabularyData()
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("🔄 LoadVocabularyData START (OPTIMIZED - parallel queries)");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // OPTIMIZATION: Start vocabulary and progress queries in PARALLEL
        Task<List<VocabularyWord>> wordsTask;

        // [US3] Use encoding repository if filtering/sorting by encoding metadata
        if (!string.IsNullOrWhiteSpace(State.SelectedTag) || State.SortByEncoding)
        {
            wordsTask = _encodingRepo.GetWithEncodingStrengthAsync(
                tagFilter: State.SelectedTag,
                sortByEncodingStrength: State.SortByEncoding);
        }
        else
        {
            // Default: Load all vocabulary words with their associated learning resources
            wordsTask = _resourceRepo.GetAllVocabularyWordsWithResourcesAsync();
        }

        // Start progress query in parallel with vocabulary query
        var progressTask = _progressService.GetAllProgressDictionaryAsync();
        var statsTask = _resourceRepo.GetVocabularyStatsAsync();

        // Wait for all to complete
        await Task.WhenAll(wordsTask, progressTask, statsTask);
        sw.Stop();

        var allWords = await wordsTask;
        var progressData = await progressTask;
        var (totalWords, associatedWords, orphanedWords) = await statsTask;

        _logger.LogInformation("⚡ PARALLEL queries completed: {ElapsedMs}ms (words={WordCount}, progress={ProgressCount}, stats ready)",
            sw.ElapsedMilliseconds, allWords?.Count ?? 0, progressData?.Count ?? 0);

        sw.Restart();
        var vocabularyItems = new List<VocabularyCardViewModel>(allWords.Count); // Pre-size list

        foreach (var word in allWords)
        {
            var item = new VocabularyCardViewModel
            {
                Word = word,
                AssociatedResources = word.LearningResources?.ToList() ?? new List<LearningResource>(),
                Progress = progressData.TryGetValue(word.Id, out var progress) ? progress : null
            };

            vocabularyItems.Add(item);
        }
        sw.Stop();
        _logger.LogInformation("🔨 ViewModel creation loop: {ElapsedMs}ms ({Count} items)", sw.ElapsedMilliseconds, vocabularyItems.Count);

        sw.Restart();
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
        sw.Stop();
        _logger.LogInformation("💾 SetState (AllVocabularyItems): {ElapsedMs}ms", sw.ElapsedMilliseconds);

        sw.Restart();
        ApplyFilters();
        sw.Stop();
        _logger.LogInformation("🔍 ApplyFilters: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        totalStopwatch.Stop();
        _logger.LogInformation("✅ LoadVocabularyData COMPLETE: {TotalMs}ms total", totalStopwatch.ElapsedMilliseconds);
    }

    void ApplyFilters()
    {
        var filtered = State.AllVocabularyItems.AsEnumerable();

        // [T018/T019] Apply GitHub-style parsed query filters
        if (State.ParsedQuery?.HasContent == true)
        {
            filtered = ApplyParsedQueryFilters(filtered, State.ParsedQuery);
        }
        else
        {
            // Fallback to legacy filter behavior if no parsed query
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

            // Apply legacy search filter
            if (!string.IsNullOrWhiteSpace(State.SearchText))
            {
                var searchLower = State.SearchText.ToLower();
                filtered = filtered.Where(v =>
                    (v.Word.TargetLanguageTerm?.ToLower().Contains(searchLower) == true) ||
                    (v.Word.NativeLanguageTerm?.ToLower().Contains(searchLower) == true));
            }
        }

        SetState(s => s.FilteredVocabularyItems = new ObservableCollection<VocabularyCardViewModel>(filtered.ToList()));
    }

    // [T018/T019] Apply parsed query filters to vocabulary items
    IEnumerable<VocabularyCardViewModel> ApplyParsedQueryFilters(
        IEnumerable<VocabularyCardViewModel> items,
        ParsedQuery query)
    {
        var filtered = items;

        // Apply tag filters (AND logic between multiple tags)
        var tagFilters = query.TagFilters.ToList();
        if (tagFilters.Any())
        {
            foreach (var tag in tagFilters)
            {
                var tagLower = tag.ToLower();
                filtered = filtered.Where(v =>
                    v.Word.Tags?.ToLower().Contains(tagLower) == true);
            }
        }

        // Apply resource filters (OR logic between multiple resources)
        var resourceFilters = query.ResourceFilters.ToList();
        if (resourceFilters.Any())
        {
            filtered = filtered.Where(v =>
                v.AssociatedResources.Any(r =>
                    resourceFilters.Any(rf =>
                        r.Title?.Equals(rf, StringComparison.OrdinalIgnoreCase) == true)));
        }

        // Apply lemma filters (OR logic)
        var lemmaFilters = query.LemmaFilters.ToList();
        if (lemmaFilters.Any())
        {
            filtered = filtered.Where(v =>
                lemmaFilters.Any(lf =>
                    v.Word.Lemma?.Equals(lf, StringComparison.OrdinalIgnoreCase) == true));
        }

        // Apply status filters (OR logic)
        var statusFilters = query.StatusFilters.Select(s => s.ToLower()).ToList();
        if (statusFilters.Any())
        {
            filtered = filtered.Where(v =>
            {
                if (statusFilters.Contains("known")) return v.IsKnown;
                if (statusFilters.Contains("learning")) return v.IsLearning;
                if (statusFilters.Contains("unknown")) return v.IsUnknown;
                return true;
            });
        }

        // Apply free text search (searches target, native, and lemma fields)
        if (query.FreeTextTerms.Any())
        {
            var freeTextLower = query.CombinedFreeText.ToLower();
            filtered = filtered.Where(v =>
                (v.Word.TargetLanguageTerm?.ToLower().Contains(freeTextLower) == true) ||
                (v.Word.NativeLanguageTerm?.ToLower().Contains(freeTextLower) == true) ||
                (v.Word.Lemma?.ToLower().Contains(freeTextLower) == true));
        }

        return filtered;
    }

    void OnSearchTextChanged(string searchText)
    {
        SetState(s => s.SearchText = searchText);

        // Debounce search to avoid excessive filtering
        _searchTimer?.Dispose();
        _searchTimer = new System.Threading.Timer(_ => ApplyFilters(), null, 300, Timeout.Infinite);
    }

    // [T016/T017] Update search text state only - no autocomplete while typing
    void OnSearchTextUpdated(string rawQuery)
    {
        SetState(s =>
        {
            s.RawSearchQuery = rawQuery;
            // Also update legacy SearchText for backward compatibility
            s.SearchText = rawQuery;
        });
    }

    // Execute search when user presses Enter/Return
    void OnSearchSubmitted()
    {
        _logger.LogDebug("🔍 Search submitted: {Query}", State.RawSearchQuery);
        ParseSearchQuery(State.RawSearchQuery);
        ApplyFilters();
    }

    // ==================== FILTER BOTTOM SHEET ====================

    /// <summary>
    /// Opens the unified filter bottom sheet for the specified filter type
    /// </summary>
    void OpenFilterSheet(string filterType)
    {
        // Initialize pending selections from current query filters
        var pendingSelections = new HashSet<string>();

        if (State.ParsedQuery != null)
        {
            var existingFilters = filterType switch
            {
                "tag" => State.ParsedQuery.TagFilters?.ToList() ?? new List<string>(),
                "resource" => State.ParsedQuery.ResourceFilters?.ToList() ?? new List<string>(),
                "lemma" => State.ParsedQuery.LemmaFilters?.ToList() ?? new List<string>(),
                "status" => State.ParsedQuery.StatusFilters?.ToList() ?? new List<string>(),
                _ => new List<string>()
            };
            foreach (var f in existingFilters)
                pendingSelections.Add(f);
        }

        SetState(s =>
        {
            s.ActiveFilterType = filterType;
            s.PendingFilterSelections = pendingSelections;
            s.IsFilterSheetOpen = true;
        });
    }

    /// <summary>
    /// Renders the unified filter bottom sheet with chip-style multi-select UI
    /// </summary>
    VisualNode RenderFilterBottomSheet()
    {
        var title = State.ActiveFilterType switch
        {
            "tag" => $"{_localize["FilterByTag"]}",
            "resource" => $"{_localize["FilterByResource"]}",
            "lemma" => $"{_localize["FilterByLemma"]}",
            "status" => $"{_localize["FilterByStatus"]}",
            _ => $"{_localize["Filter"]}"
        };

        var items = GetFilterItemsForType(State.ActiveFilterType);

        return new SfBottomSheet(
            VStack(spacing: MyTheme.LayoutSpacing,
                // Header row with title and Apply button
                Grid(rows: "Auto", columns: "Auto,*,Auto",
                    // Clear button on left
                    Button($"{_localize["Clear"]}")
                        .TextColor(MyTheme.DarkOnLightBackground)
                        .Background(Colors.Transparent)
                        .OnClicked(ClearFilterSelections)
                        .GridColumn(0),
                    // Title centered
                    Label(title)
                        .FontSize(20)
                        .FontAttributes(FontAttributes.Bold)
                        .HCenter()
                        .VCenter()
                        .GridColumn(1),
                    // Apply button on right
                    Button($"{_localize["Apply"]}")
                        .ThemeKey(MyTheme.PrimaryButton)
                        .OnClicked(ApplyFilterSelections)
                        .GridColumn(2)
                ).Margin(0, 0, 0, MyTheme.ComponentSpacing),

                // Scrollable chip container
                RenderFilterChipContainer(items)
            )
            .Padding(MyTheme.CardPadding)
        )
        .IsOpen(State.IsFilterSheetOpen)
        .OnStateChanged((sender, args) =>
        {
            if (!State.IsFilterSheetOpen) return;
            SetState(s => s.IsFilterSheetOpen = false);
        });
    }

    /// <summary>
    /// Gets the list of available items for the current filter type
    /// </summary>
    List<(string Value, string DisplayName)> GetFilterItemsForType(string filterType)
    {
        return filterType switch
        {
            "tag" => State.AvailableTags
                .OrderBy(t => t)
                .Select(t => (t, t))
                .ToList(),

            "resource" => State.AvailableResources
                .OrderBy(r => r.Title)
                .Select(r => (r.Title ?? $"{_localize["UnnamedResource"]}", r.Title ?? $"{_localize["UnnamedResource"]}"))
                .ToList(),

            "lemma" => State.AvailableLemmas
                .OrderBy(l => l)
                .Select(l => (l, l))
                .ToList(),

            "status" => new List<(string, string)>
            {
                ("known", $"✓ {_localize["StatusKnown"]}"),
                ("learning", $"⏳ {_localize["StatusLearning"]}"),
                ("unknown", $"? {_localize["StatusUnknown"]}")
            },

            _ => new List<(string, string)>()
        };
    }

    /// <summary>
    /// Renders the chip container with wrapping FlexLayout inside ScrollView
    /// </summary>
    VisualNode RenderFilterChipContainer(List<(string Value, string DisplayName)> items)
    {
        if (!items.Any())
        {
            return Label($"{_localize["NoItemsAvailable"]}")
                .ThemeKey(MyTheme.Body1)
                .Opacity(0.6)
                .HCenter();
        }

        // Wrap FlexLayout in ScrollView for scrollable chips
        return ScrollView(
            FlexLayout(
                items.Select(item => RenderFilterChip(item.Value, item.DisplayName)).ToArray()
            )
            .Wrap(Microsoft.Maui.Layouts.FlexWrap.Wrap)
            .JustifyContent(Microsoft.Maui.Layouts.FlexJustify.Start)
            .AlignItems(Microsoft.Maui.Layouts.FlexAlignItems.Start)
            .AlignContent(Microsoft.Maui.Layouts.FlexAlignContent.Start)
        )
        .MaximumHeightRequest(300); // Limit height so it scrolls
    }

    /// <summary>
    /// Renders a single filter chip with selected/unselected state
    /// </summary>
    VisualNode RenderFilterChip(string value, string displayName)
    {
        var isSelected = State.PendingFilterSelections.Contains(value);

        // Selected: dark background with white text
        // Unselected: light gray background with dark text
        var bgColor = isSelected ? MyTheme.HighlightDarkest : MyTheme.Gray200;
        var textColor = isSelected ? Colors.White : MyTheme.DarkOnLightBackground;
        var borderColor = isSelected ? MyTheme.HighlightDarkest : MyTheme.Gray300;

        return Border(
            Label(displayName)
                .TextColor(textColor)
                .FontSize(14)
                .VCenter().TranslationY(-2)
                .HCenter()
        )
        .Background(new SolidColorBrush(bgColor))
        .StrokeShape(new RoundRectangle().CornerRadius(16))
        .Stroke(borderColor)
        .StrokeThickness(1)
        .Padding(12, 8)
        .Margin(4)
        .OnTapped(() => ToggleFilterChipSelection(value));
    }

    /// <summary>
    /// Toggles a chip selection on/off
    /// </summary>
    void ToggleFilterChipSelection(string value)
    {
        SetState(s =>
        {
            if (s.PendingFilterSelections.Contains(value))
                s.PendingFilterSelections.Remove(value);
            else
                s.PendingFilterSelections.Add(value);
        });
    }

    /// <summary>
    /// Clears all pending filter selections
    /// </summary>
    void ClearFilterSelections()
    {
        SetState(s => s.PendingFilterSelections = new HashSet<string>());
    }

    /// <summary>
    /// Applies the pending filter selections to the search query
    /// </summary>
    void ApplyFilterSelections()
    {
        var filterType = State.ActiveFilterType;
        var selections = State.PendingFilterSelections.ToList();

        // Close the sheet
        SetState(s => s.IsFilterSheetOpen = false);

        if (!selections.Any())
        {
            // Remove all filters of this type from the query
            RemoveFiltersOfType(filterType);
            return;
        }

        // Build filter text for all selections
        var filterTexts = selections.Select(value =>
        {
            var formattedValue = value.Contains(' ') ? $"\"{value}\"" : value;
            return $"{filterType}:{formattedValue}";
        });

        // Remove existing filters of this type, then add new ones
        RemoveFiltersOfType(filterType);

        var newFilterText = string.Join(" ", filterTexts);
        var newQuery = string.IsNullOrWhiteSpace(State.RawSearchQuery)
            ? newFilterText
            : $"{State.RawSearchQuery} {newFilterText}";

        SetState(s =>
        {
            s.RawSearchQuery = newQuery;
            s.SearchText = newQuery;
        });

        // Parse and apply
        ParseSearchQuery(newQuery);
        ApplyFilters();
    }

    /// <summary>
    /// Removes all filters of a specific type from the current query
    /// </summary>
    void RemoveFiltersOfType(string filterType)
    {
        if (string.IsNullOrWhiteSpace(State.RawSearchQuery)) return;

        // Parse current query, remove filters of this type, rebuild
        var pattern = $@"{filterType}:""[^""]*""|{filterType}:\S+";
        var newQuery = System.Text.RegularExpressions.Regex.Replace(
            State.RawSearchQuery, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Clean up extra spaces
        newQuery = System.Text.RegularExpressions.Regex.Replace(newQuery.Trim(), @"\s+", " ");

        SetState(s =>
        {
            s.RawSearchQuery = newQuery;
            s.SearchText = newQuery;
        });

        ParseSearchQuery(newQuery);
        ApplyFilters();
    }

    // ==================== END FILTER BOTTOM SHEET ====================

#if ANDROID || IOS || MACCATALYST || WINDOWS
    // GitHub-style filter popup for tags - shows all tags, selection inserts filter text
    async Task dShowTagFilterPopup()
    {
        if (!State.AvailableTags.Any()) return;

        var tags = State.AvailableTags.OrderBy(t => t).ToArray();

        var selectedTag = await Application.Current.MainPage.DisplayActionSheet(
            $"{_localize["SelectTag"]}", $"{_localize["Cancel"]}", null, tags);

        if (!string.IsNullOrEmpty(selectedTag) && selectedTag != $"{_localize["Cancel"]}")
        {
            InsertFilterText("tag", selectedTag);
        }
    }

    // GitHub-style filter popup for resources
    async Task ShowResourceFilterPopup()
    {
        if (!State.AvailableResources.Any()) return;

        var resourceNames = State.AvailableResources
            .OrderBy(r => r.Title)
            .Select(r => r.Title ?? $"{_localize["UnnamedResource"]}")
            .ToArray();

        var selectedResource = await Application.Current.MainPage.DisplayActionSheet(
            $"{_localize["SelectResource"]}", $"{_localize["Cancel"]}", null, resourceNames);

        if (!string.IsNullOrEmpty(selectedResource) && selectedResource != $"{_localize["Cancel"]}")
        {
            InsertFilterText("resource", selectedResource);
        }
    }

    // GitHub-style filter popup for lemmas
    async Task ShowLemmaFilterPopup()
    {
        if (!State.AvailableLemmas.Any()) return;

        var lemmas = State.AvailableLemmas.OrderBy(l => l).ToArray();

        var selectedLemma = await Application.Current.MainPage.DisplayActionSheet(
            $"{_localize["SelectLemma"]}", $"{_localize["Cancel"]}", null, lemmas);

        if (!string.IsNullOrEmpty(selectedLemma) && selectedLemma != $"{_localize["Cancel"]}")
        {
            InsertFilterText("lemma", selectedLemma);
        }
    }

    // GitHub-style filter popup for status
    async Task ShowStatusFilterPopup()
    {
        var statusOptions = new[]
        {
            $"✓ {_localize["StatusKnown"]}",
            $"⏳ {_localize["StatusLearning"]}",
            $"? {_localize["StatusUnknown"]}"
        };

        var selectedOption = await Application.Current.MainPage.DisplayActionSheet(
            $"{_localize["SelectStatus"]}", $"{_localize["Cancel"]}", null, statusOptions);

        if (!string.IsNullOrEmpty(selectedOption) && selectedOption != $"{_localize["Cancel"]}")
        {
            // Map display text back to status value
            string statusValue = selectedOption switch
            {
                var s when s.Contains("StatusKnown") || s.StartsWith("✓") => "known",
                var s when s.Contains("StatusLearning") || s.StartsWith("⏳") => "learning",
                var s when s.Contains("StatusUnknown") || s.StartsWith("?") => "unknown",
                _ => selectedOption.Split(' ').LastOrDefault()?.ToLowerInvariant() ?? "unknown"
            };
            InsertFilterText("status", statusValue);
        }
    }

    // Insert filter text into search box (GitHub-style)
    void InsertFilterText(string filterType, string value)
    {
        // Quote value if it contains spaces
        var formattedValue = value.Contains(' ') ? $"\"{value}\"" : value;
        var filterText = $"{filterType}:{formattedValue}";

        // Append to existing query with space separator
        var newQuery = string.IsNullOrWhiteSpace(State.RawSearchQuery)
            ? filterText
            : $"{State.RawSearchQuery} {filterText}";

        SetState(s =>
        {
            s.RawSearchQuery = newQuery;
            s.SearchText = newQuery;
        });

        // Parse and apply the filter immediately
        ParseSearchQuery(newQuery);
        ApplyFilters();

        _logger.LogDebug("🏷️ Inserted filter: {FilterText}", filterText);
    }
#endif

    // [T017/T020] Parse search query and generate filter chips
    void ParseSearchQuery(string rawQuery)
    {
        try
        {
            var parsed = _searchParser.Parse(rawQuery);

            // Generate filter chips from parsed query
            var chips = GenerateFilterChips(parsed);

            SetState(s =>
            {
                s.ParsedQuery = parsed;
                s.ActiveFilterChips = chips;
            });

            _logger.LogDebug("✅ Parsed query: {FilterCount} filters, {FreeTextCount} free text terms",
                parsed.Filters.Count, parsed.FreeTextTerms.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to parse search query: {Query}", rawQuery);
            // Clear parsed state on error
            SetState(s =>
            {
                s.ParsedQuery = null;
                s.ActiveFilterChips = new List<FilterChip>();
            });
        }
    }

    // [T020] Generate FilterChip list from ParsedQuery
    List<FilterChip> GenerateFilterChips(ParsedQuery parsed)
    {
        var chips = new List<FilterChip>();

        foreach (var filter in parsed.Filters)
        {
            chips.Add(FilterChip.FromToken(filter, _localize));
        }

        return chips;
    }

    // [T023] Remove individual filter chip
    void RemoveFilter(FilterChip chip)
    {
        _logger.LogDebug("🗑️ Removing filter chip: {Type}:{Value}", chip.Type, chip.Value);

        // Convert chip to token and remove from query string
        var tokenToRemove = chip.ToToken();
        var newQuery = _searchParser.RemoveFilter(State.RawSearchQuery, tokenToRemove);

        // Update state with new query and re-apply filters
        SetState(s =>
        {
            s.RawSearchQuery = newQuery;
            s.SearchText = newQuery;
        });
        ParseSearchQuery(newQuery);
        ApplyFilters();
    }

    // [T024] Clear all filters
    void ClearAllFilters()
    {
        _logger.LogDebug("🧹 Clearing all filters");

        SetState(s =>
        {
            s.RawSearchQuery = string.Empty;
            s.SearchText = string.Empty;
            s.ParsedQuery = null;
            s.ActiveFilterChips = new List<FilterChip>();
            s.ShowAutocomplete = false;
            // Reset legacy filter state
            s.SelectedFilter = VocabularyFilter.All;
            s.SelectedResource = null;
            s.SelectedTag = null;
        });

        ApplyFilters();
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
                    .Background(Colors.Gray.WithAlpha(0.3f))
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