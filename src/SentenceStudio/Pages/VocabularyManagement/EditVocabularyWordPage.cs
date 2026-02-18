using Microsoft.Extensions.Logging;
using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.VocabularyManagement;

class VocabularyWordProps
{
    public int VocabularyWordId { get; set; }
}

class EditVocabularyWordPageState
{
    public bool IsLoading { get; set; } = true;
    public bool IsSaving { get; set; } = false;
    public VocabularyWord Word { get; set; } = new();
    public List<LearningResource> AvailableResources { get; set; } = new();
    public List<LearningResource> AssociatedResources { get; set; } = new();
    public HashSet<int> SelectedResourceIds { get; set; } = new();

    // Form fields
    public string TargetLanguageTerm { get; set; } = string.Empty;
    public string NativeLanguageTerm { get; set; } = string.Empty;

    // Encoding fields
    public string Lemma { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string MnemonicText { get; set; } = string.Empty;
    public string MnemonicImageUri { get; set; } = string.Empty;

    // UI state
    public string ErrorMessage { get; set; } = string.Empty;

    // Audio playback state
    public bool IsGeneratingAudio { get; set; } = false;

    // Example sentences
    public List<ExampleSentence> ExampleSentences { get; set; } = new();
    public bool IsLoadingExamples { get; set; } = false;
    public bool IsGeneratingSentences { get; set; } = false;
    public bool IsEditingSentence { get; set; } = false;
    public ExampleSentence? EditingSentence { get; set; } = null;

    // Progress tracking
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }

    // Encoding strength
    public double EncodingStrength { get; set; } = 0;
    public string EncodingStrengthLabel { get; set; } = "Basic";
}

partial class EditVocabularyWordPage : Component<EditVocabularyWordPageState, VocabularyWordProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] VocabularyProgressService _progressService;
    [Inject] ILogger<EditVocabularyWordPage> _logger;
    [Inject] ElevenLabsSpeechService _speechService;
    [Inject] StreamHistoryRepository _historyRepo;
    [Inject] UserActivityRepository _activityRepo;
    [Inject] UserProfileRepository _userProfileRepo;
    [Inject] ExampleSentenceRepository _exampleRepo;
    [Inject] VocabularyEncodingRepository _encodingRepo;
    [Inject] VocabularyExampleGenerationService _exampleGenerationService;
    [Inject] SpeechVoicePreferences _speechVoicePreferences;

    LocalizationManager _localize => LocalizationManager.Instance;

    private IAudioPlayer _audioPlayer;

    public override VisualNode Render()
    {
        return ContentPage(Props.VocabularyWordId == 0 ? $"{_localize["AddVocabularyWord"]}" : $"{_localize["EditVocabularyWord"]}",
            State.IsLoading ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "*,Auto", columns: "*",
                    ScrollView(
                        VStack(spacing: 24,
                            RenderWordForm(),
                            RenderEncodingSection(),
                            Props.VocabularyWordId > 0 ? RenderExampleSentencesSection() : null,
                            Props.VocabularyWordId > 0 ? RenderProgressSection() : null,
                            RenderResourceAssociations()
                        ).Padding(16)
                    ),
                    RenderActionButtons()
                ).Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )
        .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        .OnAppearing(LoadData);
    }

    VisualNode RenderWordForm()
    {
        var theme = BootstrapTheme.Current;
        return VStack(spacing: 16,
            Label($"{_localize["VocabularyTerms"]}")
                .H5()
                .FontAttributes(FontAttributes.Bold),

            // Target Language
            VStack(spacing: 8,
                Label($"{_localize["TargetLanguageTerm"]}")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                HStack(spacing: 8,
                    Border(
                        Entry()
                            .Text(State.TargetLanguageTerm)
                            .OnTextChanged(text => SetState(s => s.TargetLanguageTerm = text))
                            .Placeholder($"{_localize["EnterTargetLanguageTerm"]}")
                            .FontSize(16)
                    )
                    .Stroke(theme.GetOutline())
                    .StrokeThickness(1)
                    .StrokeShape(new RoundRectangle().CornerRadius(8))
                    .Padding(16)
                    .HFill(),

                    // Inline audio play button - only show for saved words with text
                    State.Word.Id > 0 && !string.IsNullOrWhiteSpace(State.TargetLanguageTerm) ?
                        ImageButton()
                            .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty,
                                BootstrapIcons.Create(BootstrapIcons.PlayFill, theme.GetOnBackground(), 16))
                            .Background(new SolidColorBrush(theme.GetSurface()))
                            .HeightRequest(44)
                            .WidthRequest(44)
                            .CornerRadius(8)
                            .Padding(10)
                            .VCenter()
                            .IsEnabled(!State.IsGeneratingAudio)
                            .OnClicked(() => _ = PlayWordAudioAsync(State.TargetLanguageTerm)) :
                        null
                ).VCenter()
            ),

            // Native Language  
            VStack(spacing: 8,
                Label($"{_localize["NativeLanguageTerm"]}")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                Border(
                    Entry()
                        .Text(State.NativeLanguageTerm)
                        .OnTextChanged(text => SetState(s => s.NativeLanguageTerm = text))
                        .Placeholder($"{_localize["EnterNativeLanguageTerm"]}")
                        .FontSize(16)
                )
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .Padding(16)
            ),

            // Error message
            !string.IsNullOrEmpty(State.ErrorMessage) ?
                Label(State.ErrorMessage)
                    .TextColor(theme.Danger)
                    .FontSize(12)
                    .HStart() :
                null
        );
    }

    VisualNode RenderEncodingSection()
    {
        var theme = BootstrapTheme.Current;
        return VStack(spacing: 16,
            Label("Encoding & Memory Aids")
                .H5()
                .FontAttributes(FontAttributes.Bold),

            Label($"Encoding Strength: {State.EncodingStrengthLabel}")
                .FontSize(14)
                .TextColor(State.EncodingStrengthLabel switch
                {
                    "Strong" => theme.Success,
                    "Good" => theme.Warning,
                    _ => theme.GetOutline()
                }),

            // Lemma
            VStack(spacing: 8,
                Label("Lemma (Dictionary Form)")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                Border(
                    Entry()
                        .Text(State.Lemma)
                        .OnTextChanged(text => SetState(s => s.Lemma = text))
                        .Placeholder("e.g., 가다 for 갔다, 가요, etc.")
                        .FontSize(16)
                )
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .Padding(16)
            ),

            // Tags
            VStack(spacing: 8,
                Label("Tags (comma-separated)")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                Border(
                    Entry()
                        .Text(State.Tags)
                        .OnTextChanged(text => SetState(s => s.Tags = text))
                        .Placeholder("e.g., nature, season, visual")
                        .FontSize(16)
                )
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .Padding(16)
            ),

            // Mnemonic Text
            VStack(spacing: 8,
                Label("Mnemonic Story")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                Border(
                    Editor()
                        .Text(State.MnemonicText)
                        .OnTextChanged(text => SetState(s => s.MnemonicText = text))
                        .Placeholder("A silly story or memory hook to help recall this word...")
                        .FontSize(16)
                        .HeightRequest(80)
                )
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .Padding(16)
            ),

            // Mnemonic Image URI
            VStack(spacing: 8,
                Label("Mnemonic Image URL")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                Border(
                    Entry()
                        .Text(State.MnemonicImageUri)
                        .OnTextChanged(text => SetState(s => s.MnemonicImageUri = text))
                        .Placeholder("https://example.com/image.jpg")
                        .FontSize(16)
                )
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .Padding(16),

                !string.IsNullOrWhiteSpace(State.MnemonicImageUri) ?
                    Image()
                    .Source(ImageSource.FromUri(new Uri(State.MnemonicImageUri)))
                        .HeightRequest(120)
                        .Aspect(Aspect.AspectFit)
                    : null

        ));
    }

    VisualNode RenderExampleSentencesSection()
    {
        var theme = BootstrapTheme.Current;
        return VStack(spacing: 16,
            HStack(spacing: 10,
                Label($"{_localize["ExampleSentences"]}")
                    .H5()
                    .FontAttributes(FontAttributes.Bold)
                    .VCenter()
                    .HFill(),

                State.IsGeneratingSentences ?
                    ActivityIndicator().IsRunning(true).WidthRequest(24).HeightRequest(24) :
                    HStack(spacing: 8,
                        Button($"{_localize["GenerateWithAI"]}")
                            .Background(new SolidColorBrush(theme.Primary))
                            .TextColor(Colors.White)
                            .OnClicked(() => _ = GenerateExampleSentencesAsync()),

                        Button($"{_localize["AddManually"]}")
                            .Background(new SolidColorBrush(Colors.Transparent))
                            .TextColor(theme.GetOnBackground())
                            .BorderColor(theme.GetOutline())
                            .BorderWidth(1)
                            .OnClicked(() => _ = AddExampleSentenceAsync())
                    )
            ),

            State.IsLoadingExamples ?
                ActivityIndicator().IsRunning(true).Center() :
                State.ExampleSentences.Any() ?
                    VStack(spacing: 12,
                        State.ExampleSentences.Select(sentence =>
                            RenderExampleSentenceItem(sentence)
                        ).ToArray()
                    ) :
                    Label($"{_localize["NoExampleSentencesYet"]}")
                        .FontSize(14)
                        .Muted()
                        .FontAttributes(FontAttributes.Italic)
        );
    }

    VisualNode RenderExampleSentenceItem(ExampleSentence sentence)
    {
        var theme = BootstrapTheme.Current;
        return Border(
            VStack(spacing: 8,
                Label(sentence.TargetSentence)
                    .FontSize(16)
                    .FontAttributes(FontAttributes.Bold),

                !string.IsNullOrWhiteSpace(sentence.NativeSentence) ?
                    Label(sentence.NativeSentence)
                        .FontSize(14)
                        .Muted() :
                    null,

                HStack(spacing: 8,
                    sentence.IsCore ?
                        Label("Core")
                            .FontSize(12)
                            .TextColor(theme.Warning) :
                        null,

                    // Audio play button
                    ImageButton()
                        .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty,
                            BootstrapIcons.Create(BootstrapIcons.PlayFill, theme.GetOnBackground(), 16))
                        .Background(new SolidColorBrush(theme.GetSurface()))
                        .HeightRequest(36)
                        .WidthRequest(36)
                        .CornerRadius(8)
                        .Padding(8)
                        .IsEnabled(!State.IsGeneratingAudio)
                        .OnClicked(() => _ = PlaySentenceAudioAsync(sentence)),

                    Button("Toggle Core")
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .TextColor(theme.GetOnBackground())
                        .BorderColor(theme.GetOutline())
                        .BorderWidth(1)
                        .OnClicked(() => _ = ToggleSentenceCoreAsync(sentence.Id)),

                    Button("Delete")
                        .Background(new SolidColorBrush(theme.Danger))
                        .TextColor(Colors.White)
                        .OnClicked(() => _ = DeleteSentenceAsync(sentence.Id))
                )
            )
            .Padding(16)
        )
        .BackgroundColor(theme.GetSurface())
        .Stroke(theme.GetOutline())
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(12));
    }

    VisualNode RenderProgressSection()
    {
        var theme = BootstrapTheme.Current;
        var progress = State.Progress;
        var isKnown = progress?.IsKnown ?? false;
        var isLearning = progress?.IsLearning ?? false;
        var isUnknown = progress == null || (!isKnown && !isLearning);

        var statusColor = isKnown ? theme.Success :
                         isLearning ? theme.Warning :
                         theme.GetOutline();

        var statusText = isKnown ? $"Status: {_localize["Known"]}" :
                        isLearning ? $"Status: {_localize["Learning"]}" :
                        $"Status: {_localize["Unknown"]}";

        // Build progress details text
        string progressDetails;
        if (isKnown)
        {
            progressDetails = $"{_localize["Known"]}";
        }
        else if (isUnknown)
        {
            progressDetails = $"{_localize["StartPracticing"]}";
        }
        else
        {
            // Learning status - show streak-based progress
            var parts = new List<string>();
            parts.Add($"{_localize["StreakLabel"]}: {progress?.CurrentStreak ?? 0}");

            var productionNeeded = progress?.ProductionNeededForKnown ?? 2;
            if (productionNeeded > 0)
                parts.Add($"Production: {progress?.ProductionInStreak ?? 0}/2 {_localize["Production"]}");
            else
                parts.Add($"{_localize["Production"]}");

            parts.Add($"Mastery: {(int)((progress?.MasteryScore ?? 0f) * 100)}%");
            progressDetails = string.Join(Environment.NewLine, parts);
        }

        // Build review date text
        string reviewDateText;
        if (progress?.NextReviewDate == null)
        {
            reviewDateText = $"{_localize["NotScheduled"]}";
        }
        else
        {
            var now = DateTime.Now.Date;
            var reviewDate = progress.NextReviewDate.Value.Date;

            if (reviewDate <= now)
                reviewDateText = $"{_localize["DueNow"]}";
            else if (reviewDate == now.AddDays(1))
                reviewDateText = $"{_localize["Tomorrow"]}";
            else if (reviewDate <= now.AddDays(7))
                reviewDateText = $"{(reviewDate - now).Days} {_localize["DaysAway"]}";
            else
                reviewDateText = $"{reviewDate:MMM d}";
        }

        var isDueForReview = progress?.IsDueForReview ?? false;

        return VStack(
            Label($"{_localize["LearningProgress"]}")
                .H5()
                .FontAttributes(FontAttributes.Bold),

            Border(
                VStack(spacing: 8,
                    Label(statusText)
                        .FontSize(14),

                    // Progress details
                    Label(progressDetails)
                        .FontSize(14),

                    // Review date
                    Label(reviewDateText)
                        .FontSize(14)
                )
                .Padding(16)
            )
        );
    }

    VisualNode RenderResourceAssociations()
    {
        var theme = BootstrapTheme.Current;
        return VStack(spacing: 16,
            HStack(spacing: 10,
                Label($"{_localize["ResourceAssociations"]}")
                    .H5()
                    .FontAttributes(FontAttributes.Bold)
                    .VCenter()
                    .HFill(),

                Label(string.Format($"{_localize["Selected"]}", State.SelectedResourceIds.Count))
                    .FontSize(12)
                    .Muted()
                    .VCenter()
            ),

            Label($"{_localize["SelectResourceToAssociate"]}")
                .FontSize(14)
                .Muted(),

            State.AvailableResources.Any() ?
                VStack(spacing: 8,
                    State.AvailableResources.Select(resource =>
                        RenderResourceItem(resource)
                    ).ToArray()
                ) :
                Label($"{_localize["NoResourcesAvailable"]}")
                    .FontSize(14)
                    .Muted()
                    .FontAttributes(FontAttributes.Italic)
                    .Center()
        );
    }

    VisualNode RenderResourceItem(LearningResource resource)
    {
        var theme = BootstrapTheme.Current;
        var isSelected = State.SelectedResourceIds.Contains(resource.Id);
        return Border(
            HStack(
                CheckBox()
                    .IsChecked(isSelected)
                    .OnCheckedChanged(isChecked => ToggleResourceSelection(resource.Id, isChecked))
                    .VCenter(),

                VStack(spacing: 4,
                    Label(resource.Title ?? "Unknown Resource")
                        .FontSize(16)
                        .FontAttributes(FontAttributes.Bold),

                    resource.Description != null ?
                        Label(resource.Description)
                            .FontSize(12)
                            .Muted()
                            .MaxLines(2) :
                        null
                ).VCenter().HFill()

            )
        )
        .Stroke(isSelected ? theme.Success : theme.GetOutline())
        .StrokeThickness(1)
        .BackgroundColor(isSelected ? theme.Success.WithAlpha(0.1f) : theme.GetSurface())
        .OnTapped(() => ToggleResourceSelection(resource.Id, !isSelected));
    }

    VisualNode RenderActionButtons()
    {
        var theme = BootstrapTheme.Current;
        return Grid(
            rows: "Auto,Auto",
            columns: Props.VocabularyWordId > 0 ? "*,Auto" : "*",
            // Save/Add button on the left
            Button(Props.VocabularyWordId == 0 ? "Add Vocabulary Word" : "Save Changes")
                .Background(new SolidColorBrush(theme.Primary))
                .TextColor(Colors.White)
                .OnClicked(SaveVocabularyWord)
                .IsEnabled(!State.IsSaving &&
                          !string.IsNullOrWhiteSpace(State.TargetLanguageTerm.Trim()) &&
                          !string.IsNullOrWhiteSpace(State.NativeLanguageTerm.Trim()))
                .FontSize(16)
                .Padding(16, 16)
                .GridRow(0)
                .GridColumn(0),

            // Delete icon button on the right (only for existing words)
            Props.VocabularyWordId > 0 ?
                ImageButton()
                    .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty,
                        BootstrapIcons.Create(BootstrapIcons.Trash, theme.Danger, 20))
                    .Background(new SolidColorBrush(theme.GetSurface()))
                    .HeightRequest(36)
                    .WidthRequest(36)
                    .CornerRadius(18)
                    .Padding(4)
                    .OnClicked(DeleteVocabularyWord)
                    .IsEnabled(!State.IsSaving)
                    .GridRow(0)
                    .GridColumn(1) :
                null,

            // Loading indicator row
            State.IsSaving ?
                HStack(spacing: 8,
                    ActivityIndicator()
                        .IsRunning(true)
                        .Scale(0.8),
                    Label("Saving...")
                        .FontSize(14)
                        .Muted()
                        .VCenter()
                )
                .HCenter()
                .GridRow(1)
                .GridColumnSpan(Props.VocabularyWordId > 0 ? 2 : 1) :
                null
        )
        .BackgroundColor(theme.GetSurface())
        .GridRow(1);
    }

    async Task LoadData()
    {
        SetState(s => s.IsLoading = true);

        try
        {
            VocabularyWord? word = null;
            SentenceStudio.Shared.Models.VocabularyProgress? progress = null;
            List<ExampleSentence> examples = new();

            // Load existing word or create new one
            if (Props.VocabularyWordId > 0)
            {
                word = await _resourceRepo.GetVocabularyWordByIdAsync(Props.VocabularyWordId);
                if (word == null)
                {
                    await IPopupService.Current.PushAsync(new SimpleActionPopup
                    {
                        Title = $"{_localize["Error"]}",
                        Text = $"{_localize["VocabularyWordNotFound"]}",
                        ActionButtonText = $"{_localize["OK"]}",
                        ShowSecondaryActionButton = false
                    });
                    await NavigateBack();
                    return;
                }

                // Load progress for this word
                progress = await _progressService.GetProgressAsync(Props.VocabularyWordId);

                // Load example sentences
                examples = await _exampleRepo.GetByVocabularyWordIdAsync(Props.VocabularyWordId);
            }
            else
            {
                // Create new word for adding
                word = new VocabularyWord
                {
                    Id = 0,
                    TargetLanguageTerm = string.Empty,
                    NativeLanguageTerm = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
            }

            // Load all available resources
            var allResources = await _resourceRepo.GetAllResourcesAsync();

            // Load associated resources for this word
            var associatedResources = await _resourceRepo.GetResourcesForVocabularyWordAsync(Props.VocabularyWordId);

            // Calculate encoding strength
            var (score, label) = EncodingScoreHelper.CalculateWithLabel(word, examples.Count);

            SetState(s =>
            {
                s.Word = word;
                s.TargetLanguageTerm = word.TargetLanguageTerm ?? string.Empty;
                s.NativeLanguageTerm = word.NativeLanguageTerm ?? string.Empty;
                s.Lemma = word.Lemma ?? string.Empty;
                s.Tags = word.Tags ?? string.Empty;
                s.MnemonicText = word.MnemonicText ?? string.Empty;
                s.MnemonicImageUri = word.MnemonicImageUri ?? string.Empty;
                s.AvailableResources = allResources?.ToList() ?? new List<LearningResource>();
                s.AssociatedResources = associatedResources?.ToList() ?? new List<LearningResource>();
                s.SelectedResourceIds = new HashSet<int>(associatedResources?.Select(r => r.Id) ?? Enumerable.Empty<int>());
                s.Progress = progress;
                s.ExampleSentences = examples;
                s.EncodingStrength = score;
                s.EncodingStrengthLabel = label;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load vocabulary word data");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = string.Format($"{_localize["FailedToLoadVocabulary"]}", ex.Message),
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
            await NavigateBack();
        }
        finally
        {
            SetState(s => s.IsLoading = false);
        }
    }

    void ToggleResourceSelection(int resourceId, bool isSelected)
    {
        SetState(s =>
        {
            if (isSelected)
            {
                s.SelectedResourceIds.Add(resourceId);
            }
            else
            {
                s.SelectedResourceIds.Remove(resourceId);
            }
        });
    }

    async Task SaveVocabularyWord()
    {
        SetState(s =>
        {
            s.IsSaving = true;
            s.ErrorMessage = string.Empty;
        });

        try
        {
            var targetTerm = State.TargetLanguageTerm.Trim();
            var nativeTerm = State.NativeLanguageTerm.Trim();

            if (string.IsNullOrEmpty(targetTerm) || string.IsNullOrEmpty(nativeTerm))
            {
                SetState(s => s.ErrorMessage = "Both target and native language terms are required");
                return;
            }

            // Check for duplicates (excluding current word)
            var existingWord = await _resourceRepo.FindDuplicateVocabularyWordAsync(targetTerm, nativeTerm);
            if (existingWord != null && existingWord.Id != State.Word.Id)
            {
                SetState(s => s.ErrorMessage = "A vocabulary word with these terms already exists");
                return;
            }

            // Update the word with all fields
            State.Word.TargetLanguageTerm = targetTerm;
            State.Word.NativeLanguageTerm = nativeTerm;
            State.Word.Lemma = State.Lemma.Trim();
            State.Word.Tags = State.Tags.Trim();
            State.Word.MnemonicText = State.MnemonicText.Trim();
            State.Word.MnemonicImageUri = State.MnemonicImageUri.Trim();
            State.Word.UpdatedAt = DateTime.UtcNow;

            await _resourceRepo.SaveWordAsync(State.Word);

            // Handle resource associations (only for words with valid IDs)
            if (State.Word.Id > 0)
            {
                var currentResourceIds = State.AssociatedResources.Select(r => r.Id).ToHashSet();
                var newResourceIds = State.SelectedResourceIds;

                // Remove associations
                foreach (var resourceId in currentResourceIds.Except(newResourceIds))
                {
                    await _resourceRepo.RemoveVocabularyFromResourceAsync(resourceId, State.Word.Id);
                }

                // Add associations
                foreach (var resourceId in newResourceIds.Except(currentResourceIds))
                {
                    await _resourceRepo.AddVocabularyToResourceAsync(resourceId, State.Word.Id);
                }
            }
            else
            {
                // For new words, just add the selected associations
                foreach (var resourceId in State.SelectedResourceIds)
                {
                    await _resourceRepo.AddVocabularyToResourceAsync(resourceId, State.Word.Id);
                }
            }


        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save vocabulary word");
            SetState(s => s.ErrorMessage = $"Failed to save: {ex.Message}");
        }
        finally
        {
            SetState(s => s.IsSaving = false);
            await AppShell.DisplayToastAsync(Props.VocabularyWordId == 0 ?
                "Vocabulary word added successfully!" :
                "Vocabulary word updated successfully!");
            await NavigateBack();
        }
    }

    async Task DeleteVocabularyWord()
    {
        var deleteTcs = new TaskCompletionSource<bool>();
        var deleteConfirmPopup = new SimpleActionPopup
        {
            Title = "Confirm Delete",
            Text = $"Are you sure you want to delete '{State.Word.TargetLanguageTerm}'?\n\nThis action cannot be undone.",
            ActionButtonText = "Delete",
            SecondaryActionButtonText = "Cancel",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                deleteTcs.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                deleteTcs.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(deleteConfirmPopup);
        bool confirm = await deleteTcs.Task;

        if (!confirm) return;

        SetState(s => s.IsSaving = true);

        try
        {
            await _resourceRepo.DeleteVocabularyWordAsync(State.Word.Id);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete vocabulary word");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to delete vocabulary word: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
        finally
        {
            SetState(s => s.IsSaving = false);
            await AppShell.DisplayToastAsync("Vocabulary word deleted successfully!");
            await NavigateBack();
        }
    }

    Task NavigateBack()
    {
        return MauiControls.Shell.Current.GoToAsync("..");
    }

    /// <summary>
    /// Plays isolated word pronunciation using ElevenLabs TTS.
    /// Learning Science: Provides pronunciation model for articulatory practice (shadowing/imitation).
    /// Multimodal input (visual + auditory) strengthens phonological encoding.
    /// </summary>
    async Task PlayWordAudioAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        SetState(s => s.IsGeneratingAudio = true);

        try
        {
            // Stop any existing player
            if (_audioPlayer != null)
            {
                _audioPlayer.PlaybackEnded -= OnAudioPlaybackEnded;
                if (_audioPlayer.IsPlaying)
                {
                    _audioPlayer.Stop();
                }
            }

            Stream audioStream;
            bool fromCache = false;

            // Check if we have cached audio for this word
            var cachedAudio = await _historyRepo.GetStreamHistoryByPhraseAndVoiceAsync(word, Voices.JiYoung);

            if (cachedAudio != null && !string.IsNullOrEmpty(cachedAudio.AudioFilePath) && File.Exists(cachedAudio.AudioFilePath))
            {
                // Use cached audio file
                _logger.LogInformation("🎧 Using cached audio for word: {Word}", word);
                audioStream = File.OpenRead(cachedAudio.AudioFilePath);
                fromCache = true;
            }
            else
            {
                // Generate audio using ElevenLabs with user's preferred voice
                _logger.LogInformation("🎧 Generating audio from API for word: {Word}", word);
                audioStream = await _speechService.TextToSpeechAsync(
                    text: word,
                    voiceId: _speechVoicePreferences.VoiceId,
                    stability: 0.5f,
                    similarityBoost: 0.75f
                );

                // Save to cache for future use
                await SaveToHistory(word, audioStream);
            }

            // Reset stream position to beginning
            audioStream.Position = 0;

            // Create audio player from stream and play immediately
            _audioPlayer = AudioManager.Current.CreatePlayer(audioStream);
            _audioPlayer.PlaybackEnded += OnAudioPlaybackEnded;
            _audioPlayer.Play();

            _logger.LogInformation("✅ Successfully playing audio for: {Word} (from {Source})",
                word, fromCache ? "cache" : "API");

            // Track listening activity for progress analytics
            await RecordListeningActivity(State.Word.Id, "isolated_word", _audioPlayer.Duration);

            _logger.LogInformation("✅ Successfully played audio for: {Word}", word);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to generate audio for word: {Word}", word);

            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Audio Error",
                Text = $"Failed to generate audio: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
        finally
        {
            SetState(s => s.IsGeneratingAudio = false);
        }
    }

    /// <summary>
    /// Called when audio playback finishes naturally.
    /// </summary>
    private void OnAudioPlaybackEnded(object sender, EventArgs e)
    {
        _logger.LogDebug("🎵 Audio playback ended");

        if (_audioPlayer != null)
        {
            _audioPlayer.PlaybackEnded -= OnAudioPlaybackEnded;
            // Don't dispose immediately - can cause crashes on some platforms
        }

        SetState(s => s.IsGeneratingAudio = false);
    }

    /// <summary>
    /// Opens HowDoYouSayPage with pre-filled phrase for sentence-context audio.
    /// Learning Science: Hearing words in sentences shows prosody, collocations, and usage patterns.
    /// Supports comprehensible input via contextualized vocabulary exposure.
    /// </summary>
    async Task OpenAudioStudio()
    {
        if (string.IsNullOrWhiteSpace(State.TargetLanguageTerm))
            return;

        _logger.LogInformation("📝 Navigating to audio studio with phrase: {Phrase}", State.TargetLanguageTerm);

        // Navigate to HowDoYouSayPage with pre-filled phrase
        var escapedPhrase = Uri.EscapeDataString(State.TargetLanguageTerm);
        await MauiControls.Shell.Current.GoToAsync(
            $"//howdoyousay?phrase={escapedPhrase}&returnToVocab=true"
        );
    }

    /// <summary>
    /// Saves audio to StreamHistory for persistence and later review.
    /// Learning Science: Audio history enables listening-based spaced repetition reviews.
    /// </summary>
    async Task SaveToHistory(string phrase, Stream audioStream)
    {
        try
        {
            // Check if we already have this phrase cached
            var existing = await _historyRepo.GetStreamHistoryByPhraseAndVoiceAsync(phrase, Voices.JiYoung);
            if (existing != null && !string.IsNullOrEmpty(existing.AudioFilePath) && File.Exists(existing.AudioFilePath))
            {
                _logger.LogDebug("💾 Audio already cached for: {Phrase}", phrase);
                return;
            }

            // Save audio to disk for offline access
            var fileName = $"vocab_{State.Word.Id}_{DateTime.Now:yyyyMMddHHmmss}.mp3";
            var audioCacheDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "AudioCache");
            var filePath = System.IO.Path.Combine(audioCacheDir, fileName);

            // Ensure directory exists
            if (!Directory.Exists(audioCacheDir))
                Directory.CreateDirectory(audioCacheDir);

            // Write audio stream to file
            using (var fileStream = File.Create(filePath))
            {
                audioStream.Position = 0;
                await audioStream.CopyToAsync(fileStream);
            }

            // Save to database
            var historyItem = new StreamHistory
            {
                Phrase = phrase,
                AudioFilePath = filePath,
                VoiceId = Voices.JiYoung,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                Source = "VocabularyManagement",
                Title = $"{phrase} ({State.NativeLanguageTerm})"
            };

            await _historyRepo.SaveStreamHistoryAsync(historyItem);

            _logger.LogInformation("💾 Saved audio to cache: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            // Non-critical error - log but don't disrupt user flow
            _logger.LogWarning(ex, "⚠️ Failed to save audio to history for: {Phrase}", phrase);
        }
    }

    /// <summary>
    /// Records listening activity for progress tracking and analytics.
    /// Learning Science: Tracks listening minutes and exposure counts to balance skill development.
    /// Supports can-do reporting (e.g., "Listened to 50 words this week").
    /// </summary>
    async Task RecordListeningActivity(int vocabularyWordId, string activityType, double durationSeconds)
    {
        try
        {
            // Use the existing UserActivity model structure
            var activity = new UserActivity
            {
                Activity = $"VocabularyAudioPlayback_{activityType}",
                Input = State.TargetLanguageTerm,
                Accuracy = 100, // Listening is passive - mark as completed
                Fluency = 100,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await _activityRepo.SaveAsync(activity);

            _logger.LogDebug("📊 Recorded listening activity: {ActivityType} for word {WordId}, duration {Duration}s",
                activityType, vocabularyWordId, durationSeconds);
        }
        catch (Exception ex)
        {
            // Non-critical error - log but don't disrupt user flow
            _logger.LogWarning(ex, "⚠️ Failed to record listening activity for word: {WordId}", vocabularyWordId);
        }
    }

    async Task GenerateExampleSentencesAsync()
    {
        if (Connectivity.NetworkAccess != NetworkAccess.Internet)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = $"{_localize["NoInternetConnection"]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
            return;
        }

        SetState(s => s.IsGeneratingSentences = true);

        try
        {
            _logger.LogInformation("🤖 Generating example sentences for word: {Word}", State.Word.TargetLanguageTerm);

            // Get user profile to determine languages
            var userProfile = await _userProfileRepo.GetAsync();
            var nativeLanguage = userProfile?.NativeLanguage ?? "English";
            var targetLanguage = userProfile?.TargetLanguage ?? "Korean";

            // Generate sentences using AI
            var generatedSentences = await _exampleGenerationService.GenerateExampleSentencesAsync(
                State.Word,
                nativeLanguage,
                targetLanguage,
                count: 3);

            if (!generatedSentences.Any())
            {
                await IPopupService.Current.PushAsync(new SimpleActionPopup
                {
                    Title = $"{_localize["Error"]}",
                    Text = $"{_localize["FailedToGenerateSentences"]}",
                    ActionButtonText = $"{_localize["OK"]}",
                    ShowSecondaryActionButton = false
                });
                return;
            }

            // Save generated sentences to database
            foreach (var generated in generatedSentences)
            {
                var sentence = new ExampleSentence
                {
                    VocabularyWordId = State.Word.Id,
                    TargetSentence = generated.TargetSentence,
                    NativeSentence = generated.NativeSentence,
                    IsCore = generated.IsCore,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _exampleRepo.CreateAsync(sentence);
            }

            // Reload sentences
            await ReloadExampleSentencesAsync();

            await AppShell.DisplayToastAsync($"{generatedSentences.Count} example sentences generated!");
            _logger.LogInformation("✅ Successfully saved {Count} generated sentences", generatedSentences.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate example sentences");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = $"{_localize["FailedToGenerateSentences"]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
        }
        finally
        {
            SetState(s => s.IsGeneratingSentences = false);
        }
    }

    async Task AddExampleSentenceAsync()
    {
        var targetFields = new List<FormField> { new FormField { Placeholder = "Enter the sentence in the target language" } };
        var targetFormPopup = new FormPopup
        {
            Title = "Add Example Sentence",
            Text = "Enter the sentence in the target language:",
            Items = targetFields,
            ActionButtonText = "Add",
            SecondaryActionText = "Cancel"
        };
        List<string?>? targetFormResult = await IPopupService.Current.PushAsync(targetFormPopup);
        var targetSentence = targetFormResult?.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(targetSentence))
            return;

        var nativeFields = new List<FormField> { new FormField { Placeholder = "Enter the translation" } };
        var nativeFormPopup = new FormPopup
        {
            Title = "Add Translation",
            Text = "Enter the translation (optional):",
            Items = nativeFields,
            ActionButtonText = "Add",
            SecondaryActionText = "Skip"
        };
        List<string?>? nativeFormResult = await IPopupService.Current.PushAsync(nativeFormPopup);
        var nativeSentence = nativeFormResult?.FirstOrDefault();

        try
        {
            var sentence = new ExampleSentence
            {
                VocabularyWordId = State.Word.Id,
                TargetSentence = targetSentence.Trim(),
                NativeSentence = nativeSentence?.Trim(),
                IsCore = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _exampleRepo.CreateAsync(sentence);

            // Reload sentences
            await ReloadExampleSentencesAsync();

            await AppShell.DisplayToastAsync("Example sentence added!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add example sentence");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = "Failed to add example sentence",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    async Task ToggleSentenceCoreAsync(int sentenceId)
    {
        try
        {
            var sentence = State.ExampleSentences.FirstOrDefault(s => s.Id == sentenceId);
            if (sentence == null) return;

            await _exampleRepo.SetCoreAsync(sentenceId, !sentence.IsCore);

            // Reload sentences
            await ReloadExampleSentencesAsync();

            await AppShell.DisplayToastAsync(sentence.IsCore ? "Marked as core sentence!" : "Removed core status");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle sentence core status");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = "Failed to update sentence",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    async Task DeleteSentenceAsync(int sentenceId)
    {
        var sentenceDeleteTcs = new TaskCompletionSource<bool>();
        var sentenceDeletePopup = new SimpleActionPopup
        {
            Title = "Confirm Delete",
            Text = "Delete this example sentence?",
            ActionButtonText = "Delete",
            SecondaryActionButtonText = "Cancel",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                sentenceDeleteTcs.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                sentenceDeleteTcs.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(sentenceDeletePopup);
        var confirm = await sentenceDeleteTcs.Task;

        if (!confirm) return;

        try
        {
            await _exampleRepo.DeleteAsync(sentenceId);

            // Reload sentences
            await ReloadExampleSentencesAsync();

            await AppShell.DisplayToastAsync("Example sentence deleted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete example sentence");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = "Failed to delete sentence",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    async Task ReloadExampleSentencesAsync()
    {
        SetState(s => s.IsLoadingExamples = true);

        try
        {
            var examples = await _exampleRepo.GetByVocabularyWordIdAsync(State.Word.Id);
            var (score, label) = EncodingScoreHelper.CalculateWithLabel(State.Word, examples.Count);

            SetState(s =>
            {
                s.ExampleSentences = examples;
                s.EncodingStrength = score;
                s.EncodingStrengthLabel = label;
            });
        }
        finally
        {
            SetState(s => s.IsLoadingExamples = false);
        }
    }

    async Task PlaySentenceAudioAsync(ExampleSentence sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence.TargetSentence))
            return;

        SetState(s => s.IsGeneratingAudio = true);

        try
        {
            // Stop any existing player
            if (_audioPlayer != null)
            {
                _audioPlayer.PlaybackEnded -= OnAudioPlaybackEnded;
                if (_audioPlayer.IsPlaying)
                {
                    _audioPlayer.Stop();
                }
            }

            Stream audioStream;
            bool fromCache = false;

            // Check if we have cached audio for this sentence using the same pattern as word audio
            var cachedAudio = await _historyRepo.GetStreamHistoryByPhraseAndVoiceAsync(sentence.TargetSentence, _speechVoicePreferences.VoiceId);

            if (cachedAudio != null && !string.IsNullOrEmpty(cachedAudio.AudioFilePath) && File.Exists(cachedAudio.AudioFilePath))
            {
                // Use cached audio file
                _logger.LogInformation("🎧 Using cached audio for sentence: {Sentence}", sentence.TargetSentence);
                audioStream = File.OpenRead(cachedAudio.AudioFilePath);
                fromCache = true;
            }
            else
            {
                // Generate new audio using ElevenLabs with user's preferred voice
                _logger.LogInformation("🎵 Generating audio for sentence: {Sentence}", sentence.TargetSentence);

                audioStream = await _speechService.TextToSpeechAsync(sentence.TargetSentence, _speechVoicePreferences.VoiceId);

                // Save to cache for future use
                var audioCacheDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "AudioCache");
                Directory.CreateDirectory(audioCacheDir);

                var fileName = $"sentence_{Guid.NewGuid()}.mp3";
                var filePath = System.IO.Path.Combine(audioCacheDir, fileName);

                // Save to file
                using (var fileStream = File.Create(filePath))
                {
                    await audioStream.CopyToAsync(fileStream);
                }

                // Create stream history entry for caching (use SaveStreamHistoryAsync, not CreateAsync)
                var streamHistory = new StreamHistory
                {
                    Phrase = sentence.TargetSentence,
                    VoiceId = _speechVoicePreferences.VoiceId,
                    AudioFilePath = filePath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _historyRepo.SaveStreamHistoryAsync(streamHistory);

                _logger.LogInformation("✅ Audio generated and cached for sentence");

                // Open the file again for playback
                audioStream = File.OpenRead(filePath);
            }

            // Create and play audio
            _audioPlayer = AudioManager.Current.CreatePlayer(audioStream);
            _audioPlayer.PlaybackEnded += OnAudioPlaybackEnded;
            _audioPlayer.Play();

            _logger.LogInformation("▶️ {Source} sentence audio playback started", fromCache ? "Cached" : "Generated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to play/generate sentence audio");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = $"Failed to play audio: {ex.Message}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
        }
        finally
        {
            SetState(s => s.IsGeneratingAudio = false);
        }
    }
}
