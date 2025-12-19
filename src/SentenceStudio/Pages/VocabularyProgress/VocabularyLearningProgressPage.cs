using MauiReactor.Shapes;
using ReactorCustomLayouts;
using SentenceStudio.Helpers;
using System.Collections.ObjectModel;

namespace SentenceStudio.Pages.VocabularyProgress;

public enum VocabularyFilterType
{
    All,
    Known,
    Learning,
    Unknown
}

class VocabularyProgressProps
{
    public int? ResourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public VocabularyFilterType? InitialFilter { get; set; }
}

public class VocabularyProgressItem
{
    // Constants matching VocabularyProgressService (NEW streak-based system)
    private const float MASTERY_THRESHOLD = 0.85f;  // Status threshold for Known
    private const int MIN_PRODUCTION_FOR_KNOWN = 2;  // Minimum production attempts to be "Known"

    public VocabularyWord Word { get; set; } = null!;
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }
    public List<string> ResourceNames { get; set; } = new();
    public List<string> ActivitiesUsed { get; set; } = new();

    // Status helpers
    public bool IsKnown => Progress?.IsKnown ?? false;
    public bool IsLearning => Progress?.IsLearning ?? false;
    public bool IsUnknown => Progress == null || (!Progress.IsKnown && !Progress.IsLearning);

    // Color coding
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

    // NEW: Streak-based progress properties
    public int CurrentStreak => Progress?.CurrentStreak ?? 0;
    public int ProductionInStreak => Progress?.ProductionInStreak ?? 0;
    public float EffectiveStreak => Progress?.EffectiveStreak ?? 0f;
    public float MasteryScore => Progress?.MasteryScore ?? 0f;

    // Progress to Known calculations (NEW)
    public int StreakToKnown => Progress?.StreakToKnown ?? 6;
    public int ProductionNeededForKnown => Progress?.ProductionNeededForKnown ?? MIN_PRODUCTION_FOR_KNOWN;
    public float PercentageToKnown => Math.Min(1f, MasteryScore / MASTERY_THRESHOLD);

    // SRS Review Date
    public DateTime? NextReviewDate => Progress?.NextReviewDate;
    public int ReviewInterval => Progress?.ReviewInterval ?? 1;
    public bool IsDueForReview => Progress?.IsDueForReview ?? false;

    public string ReviewDateText
    {
        get
        {
            var localize = LocalizationManager.Instance;
            if (NextReviewDate == null)
                return $"{localize["NotScheduled"]}";

            var now = DateTime.Now.Date;
            var reviewDate = NextReviewDate.Value.Date;

            if (reviewDate <= now)
                return $"📅 {localize["DueNow"]}";
            else if (reviewDate == now.AddDays(1))
                return $"📅 {localize["Tomorrow"]}";
            else if (reviewDate <= now.AddDays(7))
                return $"📅 {(reviewDate - now).Days} {localize["DaysAway"]}";
            else
                return $"📅 {reviewDate:MMM d}";
        }
    }

    public string ProgressRequirementsText
    {
        get
        {
            var localize = LocalizationManager.Instance;

            if (IsKnown)
                return $"✅ {localize["Known"]}";

            if (IsUnknown)
                return $"{localize["StartPracticing"]}";

            // Learning status - show streak-based progress
            var parts = new List<string>();

            // Current streak
            parts.Add($"🔥 {localize["Streak"]}: {CurrentStreak}");

            // Production progress toward Known
            if (ProductionNeededForKnown > 0)
                parts.Add($"✍️ {ProductionInStreak}/{MIN_PRODUCTION_FOR_KNOWN} {localize["Production"]}");
            else
                parts.Add($"✍️ ✓ {localize["Production"]}");

            // Mastery percentage
            parts.Add($"📊 {(int)(MasteryScore * 100)}%");

            return string.Join(" | ", parts);
        }
    }

    // LEGACY: Keep for backward compatibility during transition
    [Obsolete("Use CurrentStreak instead")]
    public int RecognitionCorrect => Progress?.RecognitionCorrect ?? 0;
    [Obsolete("Use TotalAttempts instead")]
    public int RecognitionAttempts => Progress?.RecognitionAttempts ?? 0;
    [Obsolete("Use ProductionInStreak instead")]
    public int ProductionCorrect => Progress?.ProductionCorrect ?? 0;
    [Obsolete("Use TotalAttempts instead")]
    public int ProductionAttempts => Progress?.ProductionAttempts ?? 0;
    [Obsolete("Use EffectiveStreak instead")]
    public float RecognitionAccuracy => Progress?.RecognitionAccuracy ?? 0f;
    [Obsolete("Use MasteryScore instead")]
    public float ProductionAccuracy => Progress?.ProductionAccuracy ?? 0f;
    [Obsolete("Use MasteryScore instead")]
    public double MultipleChoiceProgress => Progress?.MultipleChoiceProgress ?? 0.0;
    [Obsolete("Use MasteryScore instead")]
    public double TextEntryProgress => Progress?.TextEntryProgress ?? 0.0;
}

class VocabularyLearningProgressPageState
{
    public bool IsBusy { get; set; }
    public ObservableCollection<VocabularyProgressItem> VocabularyItems { get; set; } = new();
    public ObservableCollection<VocabularyProgressItem> FilteredVocabularyItems { get; set; } = new();
    public ObservableCollection<LearningResource> AvailableResources { get; set; } = new();
    public LearningResource? SelectedResource { get; set; }
    public VocabularyFilterType SelectedFilter { get; set; } = VocabularyFilterType.All;
    public string SearchText { get; set; } = string.Empty;
    public double ScreenWidth { get; set; }

    // Computed stats
    public int TotalWords => VocabularyItems.Count;
    public int KnownWords => VocabularyItems.Count(v => v.IsKnown);
    public int LearningWords => VocabularyItems.Count(v => v.IsLearning);
    public int UnknownWords => VocabularyItems.Count(v => v.IsUnknown);
}

partial class VocabularyLearningProgressPage : Component<VocabularyLearningProgressPageState, VocabularyProgressProps>
{
    [Inject] VocabularyProgressService _progressService;
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] VocabularyLearningContextRepository _contextRepo;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage(Props?.Title ?? $"{_localize["VocabularyProgress"]}",
            State.IsBusy ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "Auto,*", columns: "*",
                    RenderFilterBar(),
                    RenderVocabularyCollectionView()
                ).Padding(MyTheme.LayoutPadding)
        )
        .OnAppearing(LoadData)
        .OnSizeChanged(() => OnPageSizeChanged());
    }

    VisualNode RenderFilterBar()
    {
        var selectedIndex = State.SelectedFilter switch
        {
            VocabularyFilterType.All => 0,
            VocabularyFilterType.Known => 1,
            VocabularyFilterType.Learning => 2,
            VocabularyFilterType.Unknown => 3,
            _ => 0
        };

        // Determine selection indicator color based on current filter
        var selectionColor = State.SelectedFilter switch
        {
            VocabularyFilterType.Known => MyTheme.Success,
            VocabularyFilterType.Learning => MyTheme.Warning,
            VocabularyFilterType.Unknown => MyTheme.Gray400,
            _ => MyTheme.PrimaryButtonBackground // All
        };

        return VStack(spacing: MyTheme.ComponentSpacing,
            // Segmented control with inline counts
            new SfSegmentedControl(
                new SfSegmentItem()
                    .Text($"{_localize["All"]} ({State.TotalWords})")
                    .SelectedSegmentTextColor(Colors.White),
                new SfSegmentItem()
                    .Text($"{_localize["Known"]} ({State.KnownWords})")
                    .SelectedSegmentTextColor(Colors.White),
                new SfSegmentItem()
                    .Text($"{_localize["Learning"]} ({State.LearningWords})")
                    .SelectedSegmentTextColor(Colors.White),
                new SfSegmentItem()
                    .Text($"{_localize["Unknown"]} ({State.UnknownWords})")
                    .SelectedSegmentTextColor(Colors.White)
            )
            .TextStyle(new Syncfusion.Maui.Toolkit.SegmentedControl.SegmentTextStyle()
            {
                TextColor = MyTheme.Gray600
            })
            .SelectionIndicatorSettings(new Syncfusion.Maui.Toolkit.SegmentedControl.SelectionIndicatorSettings()
            {
                Background = selectionColor,
                TextColor = Colors.White
            })
            .SelectedIndex(selectedIndex)
            .SegmentWidth(120)
            .Background(MyTheme.SecondaryButtonBackground)
            .CornerRadius((float)MyTheme.Size80)
            .HeightRequest(44)
            .OnSelectionChanged((s, e) =>
            {
                var filter = e.NewIndex switch
                {
                    0 => VocabularyFilterType.All,
                    1 => VocabularyFilterType.Known,
                    2 => VocabularyFilterType.Learning,
                    3 => VocabularyFilterType.Unknown,
                    _ => VocabularyFilterType.All
                };
                OnFilterChanged(filter);
            }),

            // Search entry
            Entry()
                .Placeholder($"{_localize["SearchVocabulary"]}")
                .Text(State.SearchText)
                .OnTextChanged(OnSearchTextChanged)
        ).Padding(MyTheme.LayoutSpacing, MyTheme.ComponentSpacing, MyTheme.LayoutSpacing, MyTheme.ComponentSpacing).GridRow(0);
    }

    VisualNode RenderVocabularyCollectionView()
    {
        return CollectionView()
                .ItemsSource(State.FilteredVocabularyItems, RenderVocabularyCard)
                .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty,
                        GridLayoutHelper.CalculateResponsiveLayout(
                                        desiredItemWidth: 300,
                                        orientation: ItemsLayoutOrientation.Vertical,
                                        maxColumns: 6))
                .Background(Colors.Transparent)
                // .ItemSizingStrategy(ItemSizingStrategy.MeasureFirstItem)
                .GridRow(1);
    }

    VisualNode RenderVocabularyCard(VocabularyProgressItem item) =>
        Border(
            VStack(spacing: MyTheme.MicroSpacing,
                // Word and translation
                Label(item.Word.TargetLanguageTerm ?? "")
                    .ThemeKey(MyTheme.Title3),
                Label(item.Word.NativeLanguageTerm ?? "")
                    .ThemeKey(MyTheme.Subtitle),

                // Status badge
                Label(item.StatusText)
                    .ThemeKey(MyTheme.Caption1)
                    .TextColor(item.StatusColor),

                // Progress breakdown (streak-based)
                Label(item.ProgressRequirementsText)
                    .ThemeKey(MyTheme.Caption1)
                    .TextColor(MyTheme.Gray600),

                // SRS Review date
                Label(item.ReviewDateText)
                    .ThemeKey(MyTheme.Caption1)
                    .TextColor(item.IsDueForReview ? MyTheme.Warning : MyTheme.Gray500)
            )
            .Padding(MyTheme.ComponentSpacing)
        )
        .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.ComponentSpacing))
        .StrokeThickness(1)
        .Stroke(item.StatusColor);

    async Task LoadData()
    {
        SetState(s => s.IsBusy = true);

        try
        {
            // Initialize screen width
            var screenWidth = DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;
            SetState(s => s.ScreenWidth = screenWidth);

            // Load all learning resources
            var resources = await _resourceRepo.GetAllResourcesAsync();
            SetState(s => s.AvailableResources = new ObservableCollection<LearningResource>(resources));

            // Set initial resource filter
            if (Props?.ResourceId.HasValue == true && Props.ResourceId.Value > 0)
            {
                // Specific resource requested
                var selectedResource = resources.FirstOrDefault(r => r.Id == Props.ResourceId.Value);
                SetState(s => s.SelectedResource = selectedResource);
            }
            else
            {
                // Default to "All Resources" 
                SetState(s => s.SelectedResource = new LearningResource { Id = -1, Title = $"{_localize["AllResources"]}" });
            }

            // Set initial vocabulary filter if provided
            if (Props?.InitialFilter.HasValue == true)
            {
                SetState(s => s.SelectedFilter = Props.InitialFilter.Value);
            }

            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert($"{_localize["Error"]}", string.Format($"{_localize["FailedToLoadData"]}", ex.Message), $"{_localize["OK"]}");
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    void OnPageSizeChanged()
    {
        // Update screen width when page size changes (rotation, window resize)
        var screenWidth = DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;
        SetState(s => s.ScreenWidth = screenWidth);
    }

    async Task LoadVocabularyData()
    {
        try
        {
            var allWords = new List<VocabularyWord>();

            if (State.SelectedResource?.Id == -1 || State.SelectedResource == null)
            {
                // Load ALL vocabulary words from the database directly
                allWords = await _resourceRepo.GetAllVocabularyWordsAsync();
            }
            else
            {
                // Load vocabulary from specific resource
                var resource = await _resourceRepo.GetResourceAsync(State.SelectedResource.Id);
                if (resource?.Vocabulary?.Any() == true)
                {
                    allWords.AddRange(resource.Vocabulary);
                }
            }

            if (!allWords.Any())
            {
                // If no words found, create empty list
                SetState(s => s.VocabularyItems = new ObservableCollection<VocabularyProgressItem>());
                SetState(s => s.FilteredVocabularyItems = new ObservableCollection<VocabularyProgressItem>());
                return;
            }

            // Get progress for all words efficiently
            var wordIds = allWords.Select(w => w.Id).ToList();
            var progressData = await _progressService.GetProgressForWordsAsync(wordIds);

            // Build vocabulary items (simplified without contexts to improve performance)
            var vocabularyItems = new List<VocabularyProgressItem>();

            foreach (var word in allWords)
            {
                var progress = progressData.ContainsKey(word.Id) ? progressData[word.Id] : null;

                var item = new VocabularyProgressItem
                {
                    Word = word,
                    Progress = progress,
                    ResourceNames = new List<string>(), // Simplified - can be loaded on demand if needed
                    ActivitiesUsed = new List<string>() // Simplified - can be loaded on demand if needed
                };

                vocabularyItems.Add(item);
            }

            SetState(s => s.VocabularyItems = new ObservableCollection<VocabularyProgressItem>(vocabularyItems));

            ApplyFilters();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert($"{_localize["Error"]}", string.Format($"{_localize["FailedToLoadVocabulary"]}", ex.Message), $"{_localize["OK"]}");
        }
    }

    void ApplyFilters()
    {
        var filtered = State.VocabularyItems.AsEnumerable();

        // Apply status filter
        filtered = State.SelectedFilter switch
        {
            VocabularyFilterType.Known => filtered.Where(v => v.IsKnown),
            VocabularyFilterType.Learning => filtered.Where(v => v.IsLearning),
            VocabularyFilterType.Unknown => filtered.Where(v => v.IsUnknown),
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

        SetState(s => s.FilteredVocabularyItems = new ObservableCollection<VocabularyProgressItem>(filtered.ToList()));
    }

    async Task OnResourceFilterChanged(object selectedItem)
    {
        if (selectedItem is LearningResource resource)
        {
            SetState(s => s.SelectedResource = resource);
            await LoadVocabularyData();
        }
    }

    async Task OnFilterChanged(VocabularyFilterType filter)
    {
        SetState(s => s.SelectedFilter = filter);
        ApplyFilters();
    }

    async Task OnSearchTextChanged(string searchText)
    {
        SetState(s => s.SearchText = searchText);
        ApplyFilters();
    }
}
