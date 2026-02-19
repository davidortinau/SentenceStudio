using SentenceStudio.Repositories;
using MauiReactor.Shapes;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.MinimalPairs;

/// <summary>
/// Create Minimal Pair Page
/// 
/// Allows users to:
/// - Search and select two vocabulary words
/// - Add an optional contrast label (e.g., "ㅂ vs ㅃ", "ㄱ vs ㅋ")
/// - Create the pair (with automatic normalization and duplicate detection)
/// </summary>
class CreateMinimalPairPageState
{
    public List<VocabularyWord> AvailableWords { get; set; } = new();
    public string SearchQueryA { get; set; } = string.Empty;
    public string SearchQueryB { get; set; } = string.Empty;
    public VocabularyWord? SelectedWordA { get; set; }
    public VocabularyWord? SelectedWordB { get; set; }
    public string ContrastLabel { get; set; } = string.Empty;
    public bool IsLoading { get; set; }
    public bool IsSaving { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

partial class CreateMinimalPairPage : Component<CreateMinimalPairPageState>
{
    [Inject] ILogger<CreateMinimalPairPage> _logger;
    [Inject] LearningResourceRepository _vocabRepo;
    [Inject] MinimalPairRepository _pairRepo;
    [Inject] NativeThemeService _themeService;

    LocalizationManager _localize => LocalizationManager.Instance;

    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        _ = LoadVocabularyAsync();
    }


    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    private async Task LoadVocabularyAsync()
    {
        SetState(s => s.IsLoading = true);

        try
        {
            // Load all vocabulary words
            var words = await _vocabRepo.GetAllVocabularyWordsAsync();
            SetState(s =>
            {
                s.AvailableWords = words;
                s.IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load vocabulary");
            SetState(s =>
            {
                s.IsLoading = false;
                s.ErrorMessage = "Failed to load vocabulary";
            });
        }
    }

    private async Task OnCreatePairAsync()
    {
        if (State.SelectedWordA == null || State.SelectedWordB == null)
        {
            SetState(s => s.ErrorMessage = "Please select both words");
            return;
        }

        if (State.SelectedWordA.Id == State.SelectedWordB.Id)
        {
            SetState(s => s.ErrorMessage = "Please select two different words");
            return;
        }

        SetState(s => s.IsSaving = true);

        try
        {
            var pair = await _pairRepo.CreatePairAsync(
                userId: 1,
                wordAId: State.SelectedWordA.Id,
                wordBId: State.SelectedWordB.Id,
                contrastLabel: string.IsNullOrWhiteSpace(State.ContrastLabel) ? null : State.ContrastLabel
            );

            if (pair == null)
            {
                SetState(s =>
                {
                    s.IsSaving = false;
                    s.ErrorMessage = $"{_localize["MinimalPairsCreateFailed"]}";
                });
                return;
            }

            _logger.LogInformation("Created minimal pair: {WordA} vs {WordB}",
                State.SelectedWordA.TargetLanguageTerm,
                State.SelectedWordB.TargetLanguageTerm);

            // Show success toast and navigate back
            await AppShell.DisplayToastAsync($"{_localize["MinimalPairCreatedSuccess"]}");
            await MauiControls.Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create minimal pair");
            SetState(s =>
            {
                s.IsSaving = false;
                s.ErrorMessage = $"{_localize["MinimalPairsCreateFailed"]}";
            });
        }
    }

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["MinimalPairsCreatePair"]}",
            Grid(rows: "*", columns: "*",
                State.IsLoading
                    ? Grid(
                        Label($"{_localize["Loading"]}")
                            .Center()
                      )
                    : RenderForm()
            )
        ).BackgroundColor(BootstrapTheme.Current.GetBackground());
    }

    private List<VocabularyWord> FilterWords(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return State.AvailableWords;
        return State.AvailableWords
            .Where(w => (w.TargetLanguageTerm ?? "").StartsWith(query, StringComparison.OrdinalIgnoreCase)
                     || (w.NativeLanguageTerm ?? "").StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private VisualNode RenderWordSelector(string label, string searchQuery, VocabularyWord? selectedWord,
        Action<string> onSearchChanged, Action<VocabularyWord?> onWordSelected)
    {
        var theme = BootstrapTheme.Current;
        var filtered = FilterWords(searchQuery);

        return Border(
            VStack(spacing: 8,
                Label(label)
                    .H5(),

                SearchBar()
                    .Class("form-control")
                    .Placeholder($"{_localize["Search"]}")
                    .Text(searchQuery)
                    .OnTextChanged(text => onSearchChanged(text)),

                Picker()
                    .FormSelect()
                    .Title($"{_localize["Search"]}")
                    .ItemsSource(filtered.Select(w => $"{w.TargetLanguageTerm} — {w.NativeLanguageTerm}").ToList())
                    .SelectedIndex(selectedWord != null ? filtered.FindIndex(w => w.Id == selectedWord.Id) : -1)
                    .OnSelectedIndexChanged((index) =>
                    {
                        if (index >= 0 && index < filtered.Count)
                            onWordSelected(filtered[index]);
                        else
                            onWordSelected(null);
                    }),

                selectedWord != null
                    ? Label(selectedWord.TargetLanguageTerm)
                        .FontSize(14)
                        .TextColor(theme.Primary)
                    : null
            )
            .Padding(16)
        )
        .Class("card");
    }

    private VisualNode RenderForm()
    {
        var theme = BootstrapTheme.Current;

        return ScrollView(
            VStack(spacing: 16,
                // Word A selection
                RenderWordSelector(
                    $"{_localize["MinimalPairsSelectFirstWord"]}",
                    State.SearchQueryA,
                    State.SelectedWordA,
                    text => SetState(s => s.SearchQueryA = text),
                    word => SetState(s => s.SelectedWordA = word)
                ),

                // Word B selection
                RenderWordSelector(
                    $"{_localize["MinimalPairsSelectSecondWord"]}",
                    State.SearchQueryB,
                    State.SelectedWordB,
                    text => SetState(s => s.SearchQueryB = text),
                    word => SetState(s => s.SelectedWordB = word)
                ),

                // Contrast label input
                Border(
                    VStack(spacing: 8,
                        Label($"{_localize["MinimalPairsContrastLabel"]}")
                            .H5(),

                        Entry()
                            .Class("form-control")
                            .Placeholder("e.g., ㅂ vs ㅃ")
                            .Text(State.ContrastLabel)
                            .OnTextChanged(text => SetState(s => s.ContrastLabel = text))
                    )
                    .Padding(16)
                )
                .Class("card"),

                // Error message
                string.IsNullOrEmpty(State.ErrorMessage)
                    ? null
                    : Label(State.ErrorMessage)
                        .Small()
                        .TextColor(theme.Danger),

                // Create button
                Button($"{_localize["Create"]}")
                    .Class("btn-primary")
                    .OnClicked(async () => await OnCreatePairAsync())
                    .IsEnabled(!State.IsSaving && State.SelectedWordA != null && State.SelectedWordB != null)
            )
            .Padding(16)
        );
    }
}
