using MauiReactor.Shapes;
using ReactorCustomLayouts;
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
    public string Title { get; set; } = "Vocabulary Progress";
}

public class VocabularyProgressItem
{
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
    
    public string StatusText => IsKnown ? "Known" : 
                                IsLearning ? "Learning" : 
                                "Unknown";
    
    public double MultipleChoiceProgress => Progress?.MultipleChoiceProgress ?? 0.0;
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

    public override VisualNode Render()
    {
        return ContentPage(Props?.Title ?? "Vocabulary Progress",
            State.IsBusy ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "Auto,Auto,*", columns: "*",
                    RenderHeaderStats(),
                    RenderFilters(),
                    RenderVocabularyCollectionView()
                )
        )
        .OnAppearing(LoadData)
        .OnSizeChanged(() => OnPageSizeChanged());
    }

    VisualNode RenderHeaderStats() =>
        VStack(
            Border(
                HStack(spacing: 20,
                    RenderStatCard("Total", State.TotalWords, MyTheme.HighlightDarkest),
                    RenderStatCard("Known", State.KnownWords, MyTheme.Success),
                    RenderStatCard("Learning", State.LearningWords, MyTheme.Warning),
                    RenderStatCard("Unknown", State.UnknownWords, MyTheme.Gray400)
                ).HCenter()
            )
            .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground)
            .StrokeShape(new RoundRectangle().CornerRadius(12))
            .StrokeThickness(1)
            .Stroke(MyTheme.Gray200)
            .Padding(16)
        ).Padding(16, 16, 16, 0).GridRow(0);

    VisualNode RenderStatCard(string title, int count, Color color) =>
        VStack(spacing: 4,
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
        VStack(spacing: 12,
            // Resource picker - simplified for now
            Label("All Resources")
                .FontSize(16)
                .FontAttributes(FontAttributes.Bold)
                .HCenter(),

            // Status filter buttons
            HStack(spacing: 8,
                Button("All")
                    .Background(State.SelectedFilter == VocabularyFilterType.All ? MyTheme.HighlightDarkest : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.All ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.All)),
                Button("Known")
                    .Background(State.SelectedFilter == VocabularyFilterType.Known ? MyTheme.SupportSuccessDark : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Known ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Known)),
                Button("Learning")
                    .Background(State.SelectedFilter == VocabularyFilterType.Learning ? MyTheme.SupportErrorDark : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Learning ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Learning)),
                Button("Unknown")
                    .Background(State.SelectedFilter == VocabularyFilterType.Unknown ? MyTheme.Gray400Brush : MyTheme.Gray200Brush)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Unknown ? Colors.White : MyTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Unknown))
            ).HCenter(),

            // Search entry
            Entry()
                .Placeholder("Search vocabulary...")
                .Text(State.SearchText)
                .OnTextChanged(OnSearchTextChanged)
        ).Padding(16, 8, 16, 8).GridRow(1);

    VisualNode RenderVocabularyCollectionView()
    {
        // Calculate optimal number of columns based on screen width
        var screenWidth = State.ScreenWidth > 0 ? State.ScreenWidth : 
            DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;
        var cardWidth = 120; // From RenderVocabularyCard WidthRequest
        var horizontalSpacing = MyTheme.LayoutSpacing;
        var containerPadding = 32; // 16 left + 16 right padding
        
        // Calculate how many cards can fit: (screenWidth - padding) / (cardWidth + spacing) 
        var availableWidth = screenWidth - containerPadding;
        var itemWidthWithSpacing = cardWidth + horizontalSpacing;
        var calculatedSpan = Math.Max(1, (int)(availableWidth / itemWidthWithSpacing));
        
        // Clamp between reasonable bounds
        var span = Math.Max(2, calculatedSpan);

        var gridLayout = new GridItemsLayout(span, ItemsLayoutOrientation.Vertical)
        {
            VerticalItemSpacing = MyTheme.LayoutSpacing,
            HorizontalItemSpacing = MyTheme.LayoutSpacing
        };

        return CollectionView()
                .ItemsSource(State.FilteredVocabularyItems, RenderVocabularyCard)
                .Set(Microsoft.Maui.Controls.CollectionView.ItemsLayoutProperty, gridLayout)
                .BackgroundColor(Colors.Transparent)
                .ItemSizingStrategy(ItemSizingStrategy.MeasureFirstItem)
                .GridRow(2);
    }

    VisualNode RenderVocabularyCard(VocabularyProgressItem item) =>
        Border(
            VStack(spacing: 4,
                Label(item.Word.TargetLanguageTerm ?? "")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(MyTheme.HighlightDarkest)
                    .Center(),
                Label(item.Word.NativeLanguageTerm ?? "")
                    .FontSize(12)
                    .TextColor(MyTheme.Gray600)
                    .Center(),
                Label(item.StatusText)
                    .FontSize(10)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(item.StatusColor)
                    .Center()
            ).Padding(8)
        )
        .Background(Theme.IsLightTheme ? Colors.White : MyTheme.DarkSecondaryBackground)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .StrokeThickness(2)
        .Stroke(item.StatusColor)
        .WidthRequest(120)
        .HeightRequest(80);

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

            await LoadVocabularyData();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to load data: {ex.Message}", "OK");
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

            // Get progress for all words (this will create progress records for new words)
            var wordIds = allWords.Select(w => w.Id).ToList();
            var progressData = await _progressService.GetProgressForWordsAsync(wordIds);
            
            // Get learning contexts for analytics
            var allContexts = await _contextRepo.ListAsync();
            var relevantContexts = allContexts.Where(c => 
                c.VocabularyProgress?.VocabularyWordId != null && 
                wordIds.Contains(c.VocabularyProgress.VocabularyWordId)).ToList();

            // Build vocabulary items
            var vocabularyItems = new List<VocabularyProgressItem>();
            
            foreach (var word in allWords)
            {
                var progress = progressData.ContainsKey(word.Id) ? progressData[word.Id] : null;
                var contexts = relevantContexts.Where(c => c.VocabularyProgress?.VocabularyWordId == word.Id).ToList();
                
                var item = new VocabularyProgressItem
                {
                    Word = word,
                    Progress = progress,
                    ResourceNames = contexts.Select(c => c.LearningResource?.Title ?? "Unknown").Distinct().ToList(),
                    ActivitiesUsed = contexts.Select(c => c.Activity ?? "Unknown").Distinct().ToList()
                };
                
                vocabularyItems.Add(item);
            }

            SetState(s => s.VocabularyItems = new ObservableCollection<VocabularyProgressItem>(vocabularyItems));
            
            ApplyFilters();
        }
        catch (Exception ex)
        {
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to load vocabulary: {ex.Message}", "OK");
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
