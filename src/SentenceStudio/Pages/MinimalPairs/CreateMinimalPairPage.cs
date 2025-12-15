using SentenceStudio.Repositories;

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
    public string SearchQuery { get; set; } = string.Empty;
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

    LocalizationManager _localize => LocalizationManager.Instance;

    protected override void OnMounted()
    {
        LoadVocabulary();
    }

    private async void LoadVocabulary()
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

    private async void OnCreatePair()
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

            // Navigate back with success
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
        );
    }

    private VisualNode RenderForm()
    {
        return ScrollView(
            VStack(spacing: MyTheme.Size160,
                // Word A selection
                VStack(spacing: MyTheme.Size80,
                    Label($"{_localize["MinimalPairsSelectFirstWord"]}")
                        .ThemeKey(MyTheme.Caption1),

                    new SfComboBox()
                        .ItemsSource(State.AvailableWords)
                        .DisplayMemberPath(nameof(VocabularyWord.TargetLanguageTerm))
                        .IsEditable(true)
                        .AllowFiltering(true)
                        .PlaceholderText($"{_localize["Search"]}")
                        .SelectedItem(State.SelectedWordA)
                        .OnSelectionChanged((s, e) => SetState(state => state.SelectedWordA = e.AddedItems?.FirstOrDefault() as VocabularyWord))
                ),

                // Word B selection
                VStack(spacing: MyTheme.Size80,
                    Label($"{_localize["MinimalPairsSelectSecondWord"]}")
                        .ThemeKey(MyTheme.Caption1),

                    new SfComboBox()
                        .ItemsSource(State.AvailableWords)
                        .DisplayMemberPath(nameof(VocabularyWord.TargetLanguageTerm))
                        .IsEditable(true)
                        .AllowFiltering(true)
                        .PlaceholderText($"{_localize["Search"]}")
                        .SelectedItem(State.SelectedWordB)
                        .OnSelectionChanged((s, e) => SetState(state => state.SelectedWordB = e.AddedItems?.FirstOrDefault() as VocabularyWord))
                ),

                // Contrast label input
                VStack(spacing: MyTheme.Size80,
                    Label($"{_localize["MinimalPairsContrastLabel"]}")
                        .ThemeKey(MyTheme.Body1Strong),

                    Entry()
                        .Placeholder("e.g., ㅂ vs ㅃ")
                        .Text(State.ContrastLabel)
                        .OnTextChanged(text => SetState(s => s.ContrastLabel = text))
                ),

                // Error message
                string.IsNullOrEmpty(State.ErrorMessage)
                    ? null
                    : Label(State.ErrorMessage)
                        .ThemeKey(MyTheme.Caption1),

                // Create button
                Button($"{_localize["Create"]}")
                    .OnClicked(() => OnCreatePair())
                    .IsEnabled(!State.IsSaving && State.SelectedWordA != null && State.SelectedWordB != null)
            )
            .Padding(MyTheme.Size160)
        );
    }
}
