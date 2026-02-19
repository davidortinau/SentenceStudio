using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Helpers;
using Microsoft.Extensions.Logging;
using SentenceStudio.Models;
using SentenceStudio.Services;
using UXDivers.Popups;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

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

    public Color StatusColor => IsKnown ? BootstrapTheme.Current.Success :
                                IsLearning ? BootstrapTheme.Current.Warning :
                                BootstrapTheme.Current.GetOutline();

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

}

public class VocabManagementProps
{
    public string? ResourceName { get; set; }
}

partial class VocabularyManagementPage : Component<VocabularyManagementPageState, VocabManagementProps>, IDisposable
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] UserProfileRepository _userProfileRepo;
    [Inject] VocabularyProgressService _progressService;
    [Inject] ILogger<VocabularyManagementPage> _logger;
    [Inject] ISearchQueryParser _searchParser;  // [T012] GitHub-style search parser
    [Inject] VocabularyEncodingRepository _encodingRepo;
    [Inject] EncodingStrengthCalculator _encodingCalculator;
    [Inject] NativeThemeService _themeService;
    private System.Threading.Timer? _searchTimer;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["VocabularyManagement"]}",
            ToolbarItem().Order(ToolbarItemOrder.Primary).Text($"{_localize["Add"]}")
                .IconImageSource(BootstrapIcons.Create(BootstrapIcons.PlusLg, BootstrapTheme.Current.GetOnBackground(), 20))
                .OnClicked(async () => await ToggleQuickAdd()),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text($"{_localize["Select"]}")
                // .IconImageSource(
                //     State.IsMultiSelectMode ? MyTheme.IconDismiss : MyTheme.IconSelectAll
                // )
                .OnClicked(State.IsMultiSelectMode ? ExitMultiSelectMode : EnterMultiSelectMode),
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text($"{_localize["Cleanup"]}")
                // .IconImageSource(MyTheme.IconCleanup)
                .OnClicked(async () => await ShowCleanupOptions()),
            State.IsLoading ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "Auto,*,Auto", columns: "*",
                    RenderTopSection().GridRow(0),
                    RenderVocabularyList().GridRow(1),
                    RenderBottomBar().GridRow(2)
                )
                .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(() =>
        {
            // PERF: Defer data loading to allow page transition to complete first
            // Using BeginInvokeOnMainThread ensures the page renders before heavy I/O starts
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Use Task.Run to avoid async void lambda warning
                _ = LoadDataWithDelayAsync();
            });
        });
    }

    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
        // Cache device idiom check for performance optimization
        SetState(s =>
        {
            s.IsPhoneIdiom = DeviceInfo.Idiom == DeviceIdiom.Phone;

            // Pre-apply resource filter from navigation props
            if (!string.IsNullOrEmpty(Props.ResourceName))
            {
                var filterValue = Props.ResourceName.Contains(' ')
                    ? $"resource:\"{Props.ResourceName}\""
                    : $"resource:{Props.ResourceName}";
                s.RawSearchQuery = filterValue;
                s.SearchText = filterValue;
            }
        });

        // Parse pre-applied filter so it takes effect on first load
        if (!string.IsNullOrEmpty(Props.ResourceName))
        {
            ParseSearchQuery(State.RawSearchQuery);
        }
    }


    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    // Top section: stats bar, search input, and filter toggle buttons
    VisualNode RenderTopSection()
    {
        var theme = BootstrapTheme.Current;
        var hasActiveFilters = !string.IsNullOrWhiteSpace(State.RawSearchQuery) ||
                               State.SelectedFilter != VocabularyFilter.All ||
                               State.ParsedQuery != null;

        return VStack(spacing: 8,
            // Stats badges
            HStack(spacing: 8,
                RenderStatsBadge($"{_localize["Total"]}: {State.Stats.TotalWords}", theme.Secondary),
                RenderStatsBadge($"{_localize["Associated"]}: {State.Stats.AssociatedWords}", theme.Success),
                RenderStatsBadge($"{_localize["Orphaned"]}: {State.Stats.OrphanedWords}", theme.Warning)
            ),

            // Search bar (full width, matching Blazor)
            Grid(rows: "Auto", columns: "*,Auto",
                Entry()
                    .Placeholder($"{_localize["SearchVocabulary"]}")
                    .Text(State.RawSearchQuery)
                    .OnTextChanged(OnSearchTextUpdated)
                    .OnCompleted(OnSearchSubmitted)
                    .Class("form-control")
                    .HeightRequest(44)
                    .HFill()
                    .GridColumn(0),
                hasActiveFilters
                    ? Button("✕")
                        .Class("btn-outline-secondary").Class("btn-sm")
                        .OnClicked(ClearAllFilters)
                        .HeightRequest(44)
                        .GridColumn(1)
                    : (VisualNode)ContentView().GridColumn(1)
            ).ColumnSpacing(8),

            // Filter toggle buttons matching Blazor: All | Associated | Orphaned
            HStack(spacing: 0,
                RenderFilterToggle($"{_localize["All"]}", VocabularyFilter.All, isFirst: true, isLast: false),
                RenderFilterToggle($"{_localize["Associated"]}", VocabularyFilter.Associated, isFirst: false, isLast: false),
                RenderFilterToggle($"{_localize["Orphaned"]}", VocabularyFilter.Orphaned, isFirst: false, isLast: true)
            )
        ).Padding(16, 8);
    }

    VisualNode RenderFilterToggle(string label, VocabularyFilter filter, bool isFirst, bool isLast)
    {
        var isActive = State.SelectedFilter == filter;
        var theme = BootstrapTheme.Current;

        // Segmented button: active = btn-primary, inactive = btn-outline-secondary
        var topLeft = isFirst ? 6 : 0;
        var topRight = isLast ? 6 : 0;
        var bottomLeft = isFirst ? 6 : 0;
        var bottomRight = isLast ? 6 : 0;

        return Border(
            Label(label)
                .Class("small")
                .TextColor(isActive ? theme.OnPrimary : theme.GetOnBackground())
                .HCenter().VCenter()
        )
        .BackgroundColor(isActive ? theme.Primary : Colors.Transparent)
        .Stroke(theme.GetOutline())
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(topLeft, topRight, bottomLeft, bottomRight))
        .Padding(12, 6)
        .OnTapped(() =>
        {
            SetState(s => s.SelectedFilter = filter);
            ApplyFilters();
        });
    }

    VisualNode RenderStatsBadge(string text, Color bgColor)
    {
        return Border(
            Label(text)
                .Class("small")
                .TextColor(BootstrapTheme.Current.OnPrimary)
                .Padding(8, 4)
        )
        .Class("badge")
        .Background(bgColor);
    }

    // Bottom bar with filter icons (matching Blazor) or bulk actions in multi-select mode
    VisualNode RenderBottomBar()
    {
        if (State.IsMultiSelectMode)
            return RenderBulkActionsBar();

        var theme = BootstrapTheme.Current;
        var bgColor = theme.GetSurface();
        return Border(
            HStack(spacing: 12,
                // Filter icon buttons matching Blazor's bottom bar
                State.AvailableTags.Any() ?
                    ImageButton()
                        .Source(BootstrapIcons.Create(BootstrapIcons.Tag, theme.GetOnBackground(), 18))
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .HeightRequest(36)
                        .WidthRequest(36)
                        .OnClicked(() => OpenFilterSheet("tag")) :
                    null,

                State.AvailableResources.Any() ?
                    ImageButton()
                        .Source(BootstrapIcons.Create(BootstrapIcons.Book, theme.GetOnBackground(), 18))
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .HeightRequest(36)
                        .WidthRequest(36)
                        .OnClicked(() => OpenFilterSheet("resource")) :
                    null,

                State.AvailableLemmas.Any() ?
                    ImageButton()
                        .Source(BootstrapIcons.Create(BootstrapIcons.Braces, theme.GetOnBackground(), 18))
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .HeightRequest(36)
                        .WidthRequest(36)
                        .OnClicked(() => OpenFilterSheet("lemma")) :
                    null,

                ImageButton()
                    .Source(BootstrapIcons.Create(BootstrapIcons.Funnel, theme.GetOnBackground(), 18))
                    .Background(new SolidColorBrush(Colors.Transparent))
                    .HeightRequest(36)
                    .WidthRequest(36)
                    .OnClicked(() => OpenFilterSheet("status"))
            ).HEnd().VCenter()
        )
        .BackgroundColor(bgColor)
        .Padding(8, 4);
    }

    VisualNode RenderBulkActionsBar()
    {
        var theme = BootstrapTheme.Current;
        return Border(
                HStack(spacing: 16,
                    Label(string.Format($"{_localize["Selected"]}", State.SelectedWordIds.Count))
                        .FontSize(14)
                        .VCenter()
                        .HFill(),
                    Button($"{_localize["Delete"]}")
                        .Background(new SolidColorBrush(theme.Danger))
                        .TextColor(theme.OnPrimary)
                        .OnClicked(BulkDeleteSelected)
                        .IsEnabled(State.SelectedWordIds.Any()),
                    Button($"{_localize["Associate"]}")
                        .Background(new SolidColorBrush(theme.Primary))
                        .TextColor(theme.OnPrimary)
                        .OnClicked(BulkAssociateSelected)
                        .IsEnabled(State.SelectedWordIds.Any())
                )
            )
            .Class("card")
            .Padding(16, 8)
            .Margin(new Thickness(8, 4, 8, 8));
    }


    VisualNode RenderVocabularyList()
    {
        if (!State.FilteredVocabularyItems.Any())
        {
            var theme = BootstrapTheme.Current;
            return VStack(
                Label(State.AllVocabularyItems.Any() ?
                    $"{_localize["NoMatchFilter"]}" :
                    $"{_localize["NoVocabularyWords"]}")
                    .FontSize(14)
                    .Center(),

                !State.AllVocabularyItems.Any() ?
                    Button($"{_localize["GetStarted"]}")
                        .Background(new SolidColorBrush(theme.Primary))
                        .TextColor(theme.OnPrimary)
                        .OnClicked(async () => await ToggleQuickAdd())
                        .HCenter()
                        .Margin(0, 20, 0, 0) :
                    null
            )
            .VCenter()
            .HCenter();
        }

        return CollectionView()
            .ItemsSource(State.FilteredVocabularyItems,
                State.IsPhoneIdiom
                    ? RenderVocabularyCardMobile
                    : RenderVocabularyCard)
            .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty,
                State.IsPhoneIdiom
                    ? new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 16 }
                    : GridLayoutHelper.CalculateResponsiveLayout(desiredItemWidth: 500, maxColumns: 3))
            .Background(Colors.Transparent)
            .ItemSizingStrategy(ItemSizingStrategy.MeasureFirstItem)
            .Margin(16);
    }

    VisualNode RenderVocabularyCardMobile(VocabularyCardViewModel item)
    {
        var theme = BootstrapTheme.Current;
        return Border(
            HStack(spacing: 16,
                State.IsMultiSelectMode
                    ? CheckBox()
                            .IsChecked(item.IsSelected)
                            .OnCheckedChanged(isChecked => ToggleItemSelection(item.Word.Id, isChecked))
                    : null,

                VStack(spacing: 4,
                    Label(item.Word.TargetLanguageTerm ?? "")
                        .FontSize(14)
                        .FontAttributes(FontAttributes.Bold),
                    Label($"{item.Word.NativeLanguageTerm ?? ""} · {item.StatusText}")
                        .Small()
                        .TextColor(item.StatusColor),
                    RenderTagBadges(item.Word)
                )
            ).Padding(4)
        )
        .Padding(8, 4)
        .StrokeShape(new Rectangle())
        .StrokeThickness(1)
        .Stroke(item.StatusColor)
        .BackgroundColor(theme.GetSurface())
        .OnTapped(State.IsMultiSelectMode ?
            () => ToggleItemSelection(item.Word.Id, !item.IsSelected) :
            () => NavigateToEditPage(item.Word.Id));
    }

    VisualNode RenderVocabularyCard(VocabularyCardViewModel item)
    {
        var theme = BootstrapTheme.Current;
        return Border(
            State.IsMultiSelectMode ?
                VStack(
                    HStack(
                        CheckBox()
                            .IsChecked(item.IsSelected)
                            .OnCheckedChanged(isChecked => ToggleItemSelection(item.Word.Id, isChecked)),
                        Label($"{_localize["Select"]}")
                            .Small()
                            .VCenter()
                    ).HStart(),
                    RenderVocabularyCardViewMode(item)
                ) :
                RenderVocabularyCardViewMode(item)
        )
        .Class("card")
        .PaddingLevel(3)
        .OnTapped(State.IsMultiSelectMode ?
            () => ToggleItemSelection(item.Word.Id, !item.IsSelected) :
            () => NavigateToEditPage(item.Word.Id));
    }

    // [US3-T044] Render encoding strength badge for vocabulary card
    VisualNode RenderEncodingBadge(VocabularyWord word)
    {
        var theme = BootstrapTheme.Current;
        // Calculate encoding strength
        var exampleCount = word.ExampleSentences?.Count ?? 0;
        var score = _encodingCalculator.Calculate(word, exampleCount);
        var label = _encodingCalculator.GetLabel(score);

        // Determine badge color based on strength
        var badgeColor = label switch
        {
            "Basic" => theme.Warning,
            "Good" => theme.Secondary,
            "Strong" => theme.Success,
            _ => theme.Secondary
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
                .Class("small")
                .TextColor(BootstrapTheme.Current.OnPrimary)
                .Padding(4, 2)
        )
        .VStart()
        .Class("badge")
        .Background(badgeColor);
    }

    VisualNode RenderVocabularyCardViewMode(VocabularyCardViewModel item)
    {
        var theme = BootstrapTheme.Current;

        // Status color matching Blazor GetStatusBadgeClass
        var statusColor = item.IsKnown ? theme.Success :
                          item.IsLearning ? theme.Warning :
                          theme.Secondary;

        var statusLabel = item.IsOrphaned ? $"{_localize["Orphaned"]}" : item.StatusText;
        var resourceCount = item.AssociatedResources?.Count ?? 0;

        return VStack(spacing: 4,
            // Top row: Korean term + status badge (matches Blazor layout)
            Grid("Auto", "*,Auto",
                Label(item.Word.TargetLanguageTerm ?? "")
                    .H6()
                    .GridColumn(0),
                Border(
                    Label(statusLabel)
                        .Class("small")
                        .TextColor(theme.OnPrimary)
                        .Padding(6, 2)
                )
                .Class("badge")
                .Background(item.IsOrphaned ? theme.Warning : statusColor)
                .VStart()
                .HEnd()
                .GridColumn(1)
            ),
            Label(item.Word.NativeLanguageTerm ?? "")
                .Class("small")
                .Muted(),
            // Resource count + tags row (matches Blazor)
            HStack(spacing: 6,
                Label($"📚 {string.Format($"{_localize["ResourceCount"]}", resourceCount)}")
                    .Class("small")
                    .Muted()
                    .VCenter(),
                RenderTagBadges(item.Word)
            ).Margin(0, 4, 0, 0)
        );
    }

    VisualNode RenderTagBadges(VocabularyWord word)
    {
        if (string.IsNullOrWhiteSpace(word.Tags))
            return null;

        var tags = word.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tags.Length == 0) return null;

        var theme = BootstrapTheme.Current;
        var displayTags = tags.Take(3);

        // Color palette for tags (matches Blazor's distinct pill colors)
        var tagColors = new[] { theme.Primary, theme.Success, theme.Info, theme.Warning, theme.Danger, theme.Secondary };

        return HStack(spacing: 4,
            displayTags.Select(tag =>
            {
                var colorIndex = Math.Abs(tag.GetHashCode()) % tagColors.Length;
                return (VisualNode)Border(
                    Label(tag)
                        .Class("small")
                        .TextColor(theme.OnPrimary)
                )
                .Class("badge")
                .BackgroundColor(tagColors[colorIndex])
                .Padding(6, 2);
            }).ToArray()
        );
    }

    // Helper method to load data with initial delay for smooth page transitions
    private async Task LoadDataWithDelayAsync()
    {
        try
        {
            // Small delay to ensure page transition animation completes
            await Task.Delay(50);
            await LoadData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during deferred data loading");
        }
    }

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
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = $"{_localize["FailedToLoadVocabularyData"]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });

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

        // Always apply top-level Associated/Orphaned filter
        filtered = State.SelectedFilter switch
        {
            VocabularyFilter.Associated => filtered.Where(v => !v.IsOrphaned),
            VocabularyFilter.Orphaned => filtered.Where(v => v.IsOrphaned),
            VocabularyFilter.SpecificResource => State.SelectedResource != null ?
                filtered.Where(v => v.AssociatedResources.Any(r => r.Id == State.SelectedResource.Id)) :
                filtered,
            _ => filtered
        };

        // [T018/T019] Apply GitHub-style parsed query filters
        if (State.ParsedQuery?.HasContent == true)
        {
            filtered = ApplyParsedQueryFilters(filtered, State.ParsedQuery);
        }
        else if (!string.IsNullOrWhiteSpace(State.SearchText))
        {
            // Apply legacy search filter
            var searchLower = State.SearchText.ToLower();
            filtered = filtered.Where(v =>
                (v.Word.TargetLanguageTerm?.ToLower().Contains(searchLower) == true) ||
                (v.Word.NativeLanguageTerm?.ToLower().Contains(searchLower) == true));
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
        // Launch the OptionSheetPopup directly instead of using SfBottomSheet
        _ = ShowFilterPopup(filterType);
    }

    async Task ShowFilterPopup(string filterType)
    {
        var title = filterType switch
        {
            "tag" => $"{_localize["FilterByTag"]}",
            "resource" => $"{_localize["FilterByResource"]}",
            "lemma" => $"{_localize["FilterByLemma"]}",
            "status" => $"{_localize["FilterByStatus"]}",
            _ => $"{_localize["Filter"]}"
        };

        var items = GetFilterItemsForType(filterType);
        if (!items.Any()) return;

        // Get currently active filters of this type
        var selected = new HashSet<string>();
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
                selected.Add(f);
        }

        var tcs = new TaskCompletionSource();

        var popup = new OptionSheetPopup
        {
            Title = title,
            CloseWhenBackgroundIsClicked = true
        };

        void BuildItems()
        {
            var optionItems = new List<OptionSheetItem>();
            foreach (var (value, displayName) in items)
            {
                var capturedValue = value;
                var capturedDisplay = displayName;
                var isSelected = selected.Contains(capturedValue);
                optionItems.Add(new OptionSheetItem
                {
                    Text = isSelected ? $"\u2713  {capturedDisplay}" : $"    {capturedDisplay}",
                    Command = new Command(() =>
                    {
                        if (!selected.Remove(capturedValue))
                            selected.Add(capturedValue);
                        BuildItems();
                    })
                });
            }
            popup.Items = optionItems;
        }

        BuildItems();

        IPopupService.Current.PopupPopped += handler;
        void handler(object s, PopupEventArgs e)
        {
            if (e.PopupPage == popup)
            {
                tcs.TrySetResult();
                IPopupService.Current.PopupPopped -= handler;
            }
        }
        ;
        await IPopupService.Current.PushAsync(popup);
        await tcs.Task;

        // Apply selections: remove existing filters of this type, add new ones
        RemoveFiltersOfType(filterType);

        if (selected.Any())
        {
            var filterTexts = selected.Select(value =>
            {
                var formattedValue = value.Contains(' ') ? $"\"{value}\"" : value;
                return $"{filterType}:{formattedValue}";
            });

            var newFilterText = string.Join(" ", filterTexts);
            var newQuery = string.IsNullOrWhiteSpace(State.RawSearchQuery)
                ? newFilterText
                : $"{State.RawSearchQuery} {newFilterText}";

            SetState(s =>
            {
                s.RawSearchQuery = newQuery;
                s.SearchText = newQuery;
            });

            ParseSearchQuery(newQuery);
        }

        ApplyFilters();
    }

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
                ("learning", $"~ {_localize["StatusLearning"]}"),
                ("unknown", $"? {_localize["StatusUnknown"]}")
            },

            _ => new List<(string, string)>()
        };
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

        var bulkDeleteTcs = new TaskCompletionSource<bool>();
        var bulkDeletePopup = new SimpleActionPopup
        {
            Title = $"{_localize["ConfirmDelete"]}",
            Text = string.Format($"{_localize["ConfirmDeleteMultiple"]}", State.SelectedWordIds.Count),
            ActionButtonText = $"{_localize["Yes"]}",
            SecondaryActionButtonText = $"{_localize["No"]}",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                bulkDeleteTcs.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                bulkDeleteTcs.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(bulkDeletePopup);
        bool confirm = await bulkDeleteTcs.Task;

        if (!confirm) return;

        try
        {
            await _resourceRepo.BulkDeleteVocabularyWordsAsync(State.SelectedWordIds.ToList());

            await AppShell.DisplayToastAsync($"{State.SelectedWordIds.Count} vocabulary word(s) deleted!");
            ExitMultiSelectMode();
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = string.Format($"{_localize["FailedToDeleteVocabulary"]}", ex.Message),
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
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

            await AppShell.DisplayToastAsync($"{State.SelectedWordIds.Count} vocabulary word(s) associated with '{selectedResource.Title}'!");
            ExitMultiSelectMode();
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = string.Format($"{_localize["FailedToAssociateVocabulary"]}", ex.Message),
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
        }
    }

    async Task<LearningResource?> ShowResourceSelectionDialog()
    {
        if (!State.AvailableResources.Any())
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["NoResources"]}",
                Text = "No learning resources available for association.",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
            return null;
        }

        var resourceNames = State.AvailableResources.Select(r => r.Title ?? "Unknown").ToArray();
        var selResourceTcs = new TaskCompletionSource<string>();
        var selResourceOptionItems = new List<OptionSheetItem>();
        foreach (var name in resourceNames)
        {
            var capturedName = name;
            selResourceOptionItems.Add(new OptionSheetItem
            {
                Text = capturedName,
                Command = new Command(async () =>
                {
                    selResourceTcs.TrySetResult(capturedName);
                    await IPopupService.Current.PopAsync();
                })
            });
        }
        var selResourcePopup = new OptionSheetPopup
        {
            Title = "Select Learning Resource",
            Items = selResourceOptionItems,
            CloseWhenBackgroundIsClicked = true
        };
        IPopupService.Current.PopupPopped += selResourceHandler;
        void selResourceHandler(object s, PopupEventArgs e)
        {
            if (e.PopupPage == selResourcePopup)
            {
                selResourceTcs.TrySetResult(null);
                IPopupService.Current.PopupPopped -= selResourceHandler;
            }
        }
        ;
        await IPopupService.Current.PushAsync(selResourcePopup);
        var selectedName = await selResourceTcs.Task;

        if (string.IsNullOrEmpty(selectedName))
            return null;

        return State.AvailableResources.FirstOrDefault(r => r.Title == selectedName);
    }

    Task NavigateToEditPage(int vocabularyWordId)
    {
        return MauiControls.Shell.Current.GoToAsync<VocabularyWordProps>(
            nameof(EditVocabularyWordPage),
            props => props.VocabularyWordId = vocabularyWordId);
    }

    async Task ShowCleanupOptions()
    {
        var popup = new OptionSheetPopup
        {
            Title = $"{_localize["VocabularyCleanup"]}",
            CloseWhenBackgroundIsClicked = true,
            Items = new List<OptionSheetItem>
            {
                new OptionSheetItem
                {
                    Text = $"{_localize["FixSwappedLanguages"]}",
                    Command = new Command(async () =>
                    {
                        await IPopupService.Current.PopAsync();
                        await RunLanguageSwapCleanup();
                    })
                },
                new OptionSheetItem
                {
                    Text = $"{_localize["AssignOrphanedWords"]}",
                    Command = new Command(async () =>
                    {
                        await IPopupService.Current.PopAsync();
                        await RunOrphanAssignment();
                    })
                }
            }
        };

        await IPopupService.Current.PushAsync(popup);
    }

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

            await AppShell.DisplayToastAsync($"Swapped {swappedCount} word(s), merged {mergedCount} duplicate(s)!");
            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Cleanup failed: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
        finally
        {
            SetState(s => s.IsCleanupRunning = false);
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

                await AppShell.DisplayToastAsync($"Assigned {orphanedWords.Count} orphaned word(s) to 'General Vocabulary'!");
                await LoadVocabularyData();
            }
            else
            {
                await AppShell.DisplayToastAsync("No orphaned words found!");
            }
        }
        catch (Exception ex)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Cleanup failed: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
        finally
        {
            SetState(s => s.IsCleanupRunning = false);
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
