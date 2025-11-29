using Microsoft.Extensions.Logging;
using MauiReactor.Shapes;
using Plugin.Maui.Audio;

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

    // UI state
    public string ErrorMessage { get; set; } = string.Empty;

    // Audio playback state
    public bool IsGeneratingAudio { get; set; } = false;

    // Progress tracking
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }
}

partial class EditVocabularyWordPage : Component<EditVocabularyWordPageState, VocabularyWordProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] VocabularyProgressService _progressService;
    [Inject] ILogger<EditVocabularyWordPage> _logger;
    [Inject] ElevenLabsSpeechService _speechService;
    [Inject] StreamHistoryRepository _historyRepo;
    [Inject] UserActivityRepository _activityRepo;

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
                        VStack(spacing: MyTheme.SectionSpacing,
                            RenderWordForm(),
                            Props.VocabularyWordId > 0 ? RenderProgressSection() : null,
                            RenderResourceAssociations()
                        ).Padding(MyTheme.LayoutSpacing)
                    ),
                    RenderActionButtons()
                ).Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )
        .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        .OnAppearing(LoadData);
    }

    VisualNode RenderWordForm() =>
        VStack(spacing: 16,
            Label($"{_localize["VocabularyTerms"]}")
                .FontSize(20)
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
                    .ThemeKey(MyTheme.InputWrapper)
                    .Padding(MyTheme.CardPadding)
                    .HorizontalOptions(LayoutOptions.FillAndExpand),

                    // Inline audio play button - only show for saved words with text
                    State.Word.Id > 0 && !string.IsNullOrWhiteSpace(State.TargetLanguageTerm) ?
                        ImageButton()
                            .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconPlay)
                            .BackgroundColor(MyTheme.SecondaryButtonBackground)
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
                .ThemeKey(MyTheme.InputWrapper)
                .Padding(MyTheme.CardPadding)
            ),

            // Error message
            !string.IsNullOrEmpty(State.ErrorMessage) ?
                Label(State.ErrorMessage)
                    .TextColor(MyTheme.SupportErrorDark)
                    .FontSize(12)
                    .HStart() :
                null
        );

    VisualNode RenderProgressSection()
    {
        var progress = State.Progress;
        var isKnown = progress?.IsKnown ?? false;
        var isLearning = progress?.IsLearning ?? false;
        var isUnknown = progress == null || (!isKnown && !isLearning);

        var statusColor = isKnown ? MyTheme.Success :
                         isLearning ? MyTheme.Warning :
                         MyTheme.Gray400;

        var statusText = isKnown ? $"Status: {_localize["Known"]}" :
                        isLearning ? $"Status: {_localize["Learning"]}" :
                        $"Status: {_localize["Unknown"]}";

        // Build progress details text
        string progressDetails;
        if (isKnown)
        {
            progressDetails = $"✅ {_localize["Known"]}";
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
                reviewDateText = $"📅 {_localize["DueNow"]}";
            else if (reviewDate == now.AddDays(1))
                reviewDateText = $"📅 {_localize["Tomorrow"]}";
            else if (reviewDate <= now.AddDays(7))
                reviewDateText = $"📅 {(reviewDate - now).Days} {_localize["DaysAway"]}";
            else
                reviewDateText = $"📅 {reviewDate:MMM d}";
        }

        var isDueForReview = progress?.IsDueForReview ?? false;

        return VStack(
            Label($"{_localize["LearningProgress"]}")
                .FontSize(20)
                .FontAttributes(FontAttributes.Bold),

            Border(
                VStack(spacing: MyTheme.ComponentSpacing,
                    Label(statusText)
                        .ThemeKey(MyTheme.Body2),

                    // Progress details
                    Label(progressDetails)
                        .ThemeKey(MyTheme.Body2),

                    // Review date
                    Label(reviewDateText)
                        .ThemeKey(MyTheme.Body2)
                )
                .Padding(MyTheme.CardPadding)
            )
        );
    }

    VisualNode RenderResourceAssociations() =>
        VStack(spacing: 16,
            HStack(spacing: 10,
                Label($"{_localize["ResourceAssociations"]}")
                    .FontSize(20)
                    .FontAttributes(FontAttributes.Bold)
                    .VCenter()
                    .HorizontalOptions(LayoutOptions.FillAndExpand),

                Label(string.Format($"{_localize["Selected"]}", State.SelectedResourceIds.Count))
                    .FontSize(12)
                    .TextColor(MyTheme.Gray600)
                    .VCenter()
            ),

            Label($"{_localize["SelectResourceToAssociate"]}")
                .FontSize(14)
                .TextColor(MyTheme.Gray600),

            State.AvailableResources.Any() ?
                VStack(spacing: 8,
                    State.AvailableResources.Select(resource =>
                        RenderResourceItem(resource)
                    ).ToArray()
                ) :
                Label($"{_localize["NoResourcesAvailable"]}")
                    .FontSize(14)
                    .TextColor(MyTheme.Gray500)
                    .FontAttributes(FontAttributes.Italic)
                    .Center()
        );

    VisualNode RenderResourceItem(LearningResource resource) =>
        Border(
            HStack(
                CheckBox()
                    .IsChecked(State.SelectedResourceIds.Contains(resource.Id))
                    .OnCheckedChanged(isChecked => ToggleResourceSelection(resource.Id, isChecked))
                    .VCenter(),

                VStack(spacing: 4,
                    Label(resource.Title ?? "Unknown Resource")
                        .FontSize(16)
                        .FontAttributes(FontAttributes.Bold),

                    resource.Description != null ?
                        Label(resource.Description)
                            .FontSize(12)
                            .TextColor(MyTheme.Gray600)
                            .MaxLines(2) :
                        null
                ).VCenter().HorizontalOptions(LayoutOptions.FillAndExpand)

            )
        )
        .Stroke(State.SelectedResourceIds.Contains(resource.Id) ? MyTheme.Success : MyTheme.Gray300)
        .StrokeThickness(1)
        .Background(State.SelectedResourceIds.Contains(resource.Id) ?
            (Theme.IsLightTheme ? MyTheme.SupportSuccessLight : MyTheme.DarkSecondaryBackground) :
            (Theme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground))  // Let the theme handle the default background
        .OnTapped(() => ToggleResourceSelection(resource.Id, !State.SelectedResourceIds.Contains(resource.Id)));

    VisualNode RenderActionButtons() =>
        Grid(
            rows: "Auto,Auto",
            columns: Props.VocabularyWordId > 0 ? "*,Auto" : "*",
            // Save/Add button on the left
            Button(Props.VocabularyWordId == 0 ? "Add Vocabulary Word" : "Save Changes")
                .ThemeKey("Primary")
                .OnClicked(SaveVocabularyWord)
                .IsEnabled(!State.IsSaving &&
                          !string.IsNullOrWhiteSpace(State.TargetLanguageTerm.Trim()) &&
                          !string.IsNullOrWhiteSpace(State.NativeLanguageTerm.Trim()))
                .FontSize(16)
                .Padding(MyTheme.LayoutSpacing, MyTheme.CardPadding)
                .GridRow(0)
                .GridColumn(0),

            // Delete icon button on the right (only for existing words)
            Props.VocabularyWordId > 0 ?
                ImageButton()
                    .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconDelete)
                    .BackgroundColor(MyTheme.LightSecondaryBackground)
                    .HeightRequest(36)
                    .WidthRequest(36)
                    .CornerRadius(18)
                    .Padding(MyTheme.MicroSpacing)
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
                        .TextColor(MyTheme.Gray600)
                        .VCenter()
                )
                .HCenter()
                .GridRow(1)
                .GridColumnSpan(Props.VocabularyWordId > 0 ? 2 : 1) :
                null
        )
        .ThemeKey(MyTheme.Surface1)
        .GridRow(1);

    async Task LoadData()
    {
        SetState(s => s.IsLoading = true);

        try
        {
            VocabularyWord? word = null;
            SentenceStudio.Shared.Models.VocabularyProgress? progress = null;

            // Load existing word or create new one
            if (Props.VocabularyWordId > 0)
            {
                word = await _resourceRepo.GetVocabularyWordByIdAsync(Props.VocabularyWordId);
                if (word == null)
                {
                    await Application.Current.MainPage.DisplayAlert($"{_localize["Error"]}", $"{_localize["VocabularyWordNotFound"]}", $"{_localize["OK"]}");
                    await NavigateBack();
                    return;
                }

                // Load progress for this word
                progress = await _progressService.GetProgressAsync(Props.VocabularyWordId);
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

            SetState(s =>
            {
                s.Word = word;
                s.TargetLanguageTerm = word.TargetLanguageTerm ?? string.Empty;
                s.NativeLanguageTerm = word.NativeLanguageTerm ?? string.Empty;
                s.AvailableResources = allResources?.ToList() ?? new List<LearningResource>();
                s.AssociatedResources = associatedResources?.ToList() ?? new List<LearningResource>();
                s.SelectedResourceIds = new HashSet<int>(associatedResources?.Select(r => r.Id) ?? Enumerable.Empty<int>());
                s.Progress = progress;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load vocabulary word data");
            await Application.Current.MainPage.DisplayAlert($"{_localize["Error"]}", string.Format($"{_localize["FailedToLoadVocabulary"]}", ex.Message), $"{_localize["OK"]}");
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

            // Update the word
            State.Word.TargetLanguageTerm = targetTerm;
            State.Word.NativeLanguageTerm = nativeTerm;
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
                "✅ Vocabulary word added successfully!" :
                "✅ Vocabulary word updated successfully!");
            await NavigateBack();
        }
    }

    async Task DeleteVocabularyWord()
    {
        bool confirm = await Application.Current.MainPage.DisplayAlert(
            "Confirm Delete",
            $"Are you sure you want to delete '{State.Word.TargetLanguageTerm}'?\n\nThis action cannot be undone.",
            "Delete", "Cancel");

        if (!confirm) return;

        SetState(s => s.IsSaving = true);

        try
        {
            await _resourceRepo.DeleteVocabularyWordAsync(State.Word.Id);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete vocabulary word");
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to delete vocabulary word: {ex.Message}", "OK");
        }
        finally
        {
            SetState(s => s.IsSaving = false);
            await AppShell.DisplayToastAsync("🗑️ Vocabulary word deleted successfully!");
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
                // Generate audio using ElevenLabs with Korean voice
                _logger.LogInformation("🎧 Generating audio from API for word: {Word}", word);
                audioStream = await _speechService.TextToSpeechAsync(
                    text: word,
                    voiceId: Voices.JiYoung, // Default Korean voice - could pull from user preferences
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

            await Application.Current.MainPage.DisplayAlert(
                "Audio Error",
                $"Failed to generate audio: {ex.Message}",
                "OK"
            );
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
}