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
    // Constants matching VocabularyProgressService
    private const float MASTERY_THRESHOLD = 0.80f;  // Status threshold (0.8 for Known)
    private const int MIN_CORRECT_RECOGNITION = 3;
    private const int MIN_CORRECT_PRODUCTION = 2;

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

    public double MultipleChoiceProgress => Progress?.MultipleChoiceProgress ?? 0.0;
    public double TextEntryProgress => Progress?.TextEntryProgress ?? 0.0;

    // Progress breakdown properties
    public int RecognitionCorrect => Progress?.RecognitionCorrect ?? 0;
    public int RecognitionAttempts => Progress?.RecognitionAttempts ?? 0;
    public int ProductionCorrect => Progress?.ProductionCorrect ?? 0;
    public int ProductionAttempts => Progress?.ProductionAttempts ?? 0;
    public float MasteryScore => Progress?.MasteryScore ?? 0f;
    public float RecognitionAccuracy => Progress?.RecognitionAccuracy ?? 0f;
    public float ProductionAccuracy => Progress?.ProductionAccuracy ?? 0f;

    // Progress to next level calculations
    public int RecognitionNeeded => Math.Max(0, MIN_CORRECT_RECOGNITION - RecognitionCorrect);
    public int ProductionNeeded => Math.Max(0, MIN_CORRECT_PRODUCTION - ProductionCorrect);
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
                return $"✅ {localize["Mastered"]}";

            if (IsUnknown)
                return $"{localize["StartPracticing"]}";

            // Learning status - show what's needed
            var parts = new List<string>();

            // Recognition progress
            if (RecognitionNeeded > 0)
                parts.Add($"🎯 {RecognitionCorrect}/{MIN_CORRECT_RECOGNITION} {localize["Recognition"]}");
            else
                parts.Add($"🎯 ✓ {localize["Recognition"]}");

            // Production progress
            if (ProductionNeeded > 0)
                parts.Add($"✍️ {ProductionCorrect}/{MIN_CORRECT_PRODUCTION} {localize["Production"]}");
            else
                parts.Add($"✍️ ✓ {localize["Production"]}");

            return string.Join(" | ", parts);
        }
    }

    public string CurrentPhaseText
    {
        get
        {
            var localize = LocalizationManager.Instance;
            var phase = Progress?.CurrentPhase ?? LearningPhase.Recognition;
            return phase switch
            {
                LearningPhase.Recognition => $"{localize["PhaseRecognition"]}",
                LearningPhase.Production => $"{localize["PhaseProduction"]}",
                LearningPhase.Application => $"{localize["PhaseApplication"]}",
                _ => $"{localize["PhaseRecognition"]}"
            };
        }
    }
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
                Grid(rows: "Auto,Auto,*", columns: "*",
                    RenderHeaderStats(),
                    RenderFilters(),
                    RenderVocabularyCollectionView()
                ).Padding(MyTheme.LayoutPadding)
        )
        .OnAppearing(LoadData)
        .OnSizeChanged(() => OnPageSizeChanged());
    }

    VisualNode RenderHeaderStats() =>
        VStack(
            Border(
                HStack(spacing: MyTheme.SectionSpacing,
                    RenderStatCard($"{_localize["Total"]}", State.TotalWords, MyTheme.HighlightDarkest),
                    RenderStatCard($"{_localize["Known"]}", State.KnownWords, MyTheme.Success),
                    RenderStatCard($"{_localize["Learning"]}", State.LearningWords, MyTheme.Warning),
                    RenderStatCard($"{_localize["Unknown"]}", State.UnknownWords, MyTheme.Gray400)
                ).HCenter()
            )
            .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground)
            .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.CardPadding))
            .StrokeThickness(1)
            .Stroke(MyTheme.Gray200)
            .Padding(MyTheme.LayoutSpacing)
        ).Padding(MyTheme.LayoutSpacing, MyTheme.LayoutSpacing, MyTheme.LayoutSpacing, 0).GridRow(0);

    VisualNode RenderStatCard(string title, int count, Color color) =>
        VStack(spacing: MyTheme.MicroSpacing,
            Label(title)
                .FontSize(12)
                .TextColor(MyTheme.Gray600)
                .Center(),
            Label(count.ToString())
                .FontSize(24)
                .FontAttributes(FontAttributes.Bold)
                .TextColor(color)
                .Center()
        );

    VisualNode RenderFilters() =>
        VStack(spacing: MyTheme.CardPadding,
            // Resource picker - simplified for now
            Label($"{_localize["AllResources"]}")
                .FontSize(16)
                .FontAttributes(FontAttributes.Bold)
                .HCenter(),

            // Status filter buttons
            HStack(spacing: MyTheme.ComponentSpacing,
                Button($"{_localize["All"]}")
                    .Background(State.SelectedFilter == VocabularyFilterType.All ? MyTheme.HighlightDarkest : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.All ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.All)),
                Button($"{_localize["Known"]}")
                    .Background(State.SelectedFilter == VocabularyFilterType.Known ? MyTheme.SupportSuccessDark : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Known ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Known)),
                Button($"{_localize["Learning"]}")
                    .Background(State.SelectedFilter == VocabularyFilterType.Learning ? MyTheme.SupportErrorDark : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Learning ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Learning)),
                Button($"{_localize["Unknown"]}")
                    .Background(State.SelectedFilter == VocabularyFilterType.Unknown ? MyTheme.Gray400Brush : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Unknown ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Unknown))
            ).HCenter(),

            // Search entry
            Entry()
                .Placeholder($"{_localize["SearchVocabulary"]}")
                .Text(State.SearchText)
                .OnTextChanged(OnSearchTextChanged)
        ).Padding(MyTheme.LayoutSpacing, MyTheme.ComponentSpacing, MyTheme.LayoutSpacing, MyTheme.ComponentSpacing).GridRow(1);

    VisualNode RenderVocabularyCollectionView()
    {
        return CollectionView()
                .ItemsSource(State.FilteredVocabularyItems, RenderVocabularyCard)
                .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty,
                        GridLayoutHelper.CalculateResponsiveLayout(
                                        desiredItemWidth: 300,
                                        orientation: ItemsLayoutOrientation.Vertical,
                                        maxColumns: 6))
                .BackgroundColor(Colors.Transparent)
                // .ItemSizingStrategy(ItemSizingStrategy.MeasureFirstItem)
                .GridRow(2);
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

                // Progress breakdown
                Label(item.ProgressRequirementsText)
                    .ThemeKey(MyTheme.Caption1)
                    .TextColor(MyTheme.Gray600),

                // Current phase (for Learning words)
                item.IsLearning ?
                    Label($"📚 {item.CurrentPhaseText}")
                        .ThemeKey(MyTheme.Caption1)
                        .TextColor(MyTheme.Gray500) : null,

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
                SetState(s => s.SelectedResource = new LearningResource { Id = -1, Title = "All Resources" });
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
