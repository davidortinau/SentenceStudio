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
    public Color StatusColor => IsKnown ? ApplicationTheme.Success : 
                                IsLearning ? ApplicationTheme.Warning : 
                                ApplicationTheme.Gray400;
    
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
                ScrollView(
                    VStack(spacing: 16,
                        RenderHeaderStats(),
                        RenderFilters(),
                        RenderVocabularyList()
                    ).Padding(16)
                )
        )
        .OnAppearing(LoadData);
    }

    VisualNode RenderHeaderStats() =>
        Border(
            HStack(spacing: 20,
                RenderStatCard("Total", State.TotalWords, ApplicationTheme.Primary),
                RenderStatCard("Known", State.KnownWords, ApplicationTheme.Success),
                RenderStatCard("Learning", State.LearningWords, ApplicationTheme.Warning),
                RenderStatCard("Unknown", State.UnknownWords, ApplicationTheme.Gray400)
            ).HCenter()
        )
        .Background(Theme.IsLightTheme ? Colors.White : ApplicationTheme.DarkSecondaryBackground)
        .StrokeShape(new RoundRectangle().CornerRadius(12))
        .StrokeThickness(1)
        .Stroke(ApplicationTheme.Gray200)
        .Padding(16);

    VisualNode RenderStatCard(string title, int count, Color color) =>
        VStack(spacing: 4,
            Label(title)
                .FontSize(12)
                .TextColor(ApplicationTheme.Gray600)
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
                    .BackgroundColor(State.SelectedFilter == VocabularyFilterType.All ? ApplicationTheme.Primary : ApplicationTheme.Gray200)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.All ? Colors.White : ApplicationTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.All)),
                Button("Known")
                    .BackgroundColor(State.SelectedFilter == VocabularyFilterType.Known ? ApplicationTheme.Success : ApplicationTheme.Gray200)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Known ? Colors.White : ApplicationTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Known)),
                Button("Learning")
                    .BackgroundColor(State.SelectedFilter == VocabularyFilterType.Learning ? ApplicationTheme.Warning : ApplicationTheme.Gray200)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Learning ? Colors.White : ApplicationTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Learning)),
                Button("Unknown")
                    .BackgroundColor(State.SelectedFilter == VocabularyFilterType.Unknown ? ApplicationTheme.Gray400 : ApplicationTheme.Gray200)
                    .TextColor(State.SelectedFilter == VocabularyFilterType.Unknown ? Colors.White : ApplicationTheme.Gray600)
                    .OnClicked(() => OnFilterChanged(VocabularyFilterType.Unknown))
            ).HCenter(),

            // Search entry
            Entry()
                .Placeholder("Search vocabulary...")
                .Text(State.SearchText)
                .OnTextChanged(OnSearchTextChanged)
        );

    VisualNode RenderVocabularyList() =>
        VStack(spacing: 12,
            Label($"Vocabulary Words ({State.FilteredVocabularyItems.Count})")
                .FontSize(18)
                .FontAttributes(FontAttributes.Bold)
                .TextColor(ApplicationTheme.Primary),
            
            new HWrap()
            {
                State.FilteredVocabularyItems.Select(RenderVocabularyCard).ToArray()
            }.Spacing(12)
        );

    VisualNode RenderVocabularyCard(VocabularyProgressItem item) =>
        Border(
            VStack(spacing: 4,
                Label(item.Word.TargetLanguageTerm ?? "")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(ApplicationTheme.Primary)
                    .Center(),
                Label(item.Word.NativeLanguageTerm ?? "")
                    .FontSize(12)
                    .TextColor(ApplicationTheme.Gray600)
                    .Center(),
                Label(item.StatusText)
                    .FontSize(10)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(item.StatusColor)
                    .Center()
            ).Padding(8)
        )
        .Background(Theme.IsLightTheme ? Colors.White : ApplicationTheme.DarkSecondaryBackground)
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
