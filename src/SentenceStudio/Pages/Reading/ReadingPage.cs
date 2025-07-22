using Plugin.Maui.Audio;
using SentenceStudio.Pages.Dashboard;
using System.Text.RegularExpressions;
using SentenceStudio.Services;
using SentenceStudio.Models;

namespace SentenceStudio.Pages.Reading;

class TextSegment
{
    public string Text { get; set; } = string.Empty;
    public bool IsVocabulary { get; set; }
    public VocabularyWord VocabularyWord { get; set; }
}

class ReadingPageState
{
    // Content
    public LearningResource Resource { get; set; }
    public List<string> Sentences { get; set; } = new();
    public List<VocabularyWord> VocabularyWords { get; set; } = new();
    
    // Timestamped Audio System (NEW)
    public TimestampedAudio TimestampedAudio { get; set; }
    public bool IsTimestampedAudioLoaded { get; set; } = false;
    public bool IsGeneratingAudio { get; set; } = false;
    public string AudioGenerationStatus { get; set; } = "Ready";
    public double AudioGenerationProgress { get; set; } = 0.0;
    
    // Audio Playback State
    public bool IsAudioPlaying { get; set; } = false;
    public bool IsPlaying { get; set; } = false;  // For compatibility with some code paths
    public double CurrentPlaybackTime { get; set; } = 0.0;
    public double AudioDuration { get; set; } = 0.0;
    public float PlaybackSpeed { get; set; } = 1.0f;
    
    // Reading state
    public int CurrentSentenceIndex { get; set; } = -1;
    public int SelectedSentenceIndex { get; set; } = -1;
    public VocabularyWord SelectedVocabulary { get; set; }
    public bool IsVocabularyBottomSheetVisible { get; set; } = false;
    
    // UI state
    public bool IsBusy { get; set; } = false;
    public double FontSize { get; set; } = 18.0;
    public string ErrorMessage { get; set; }
    public bool HasShownJumpHint { get; set; } = false;
    public bool HasDismissedInstructions { get; set; } = false;
}

partial class ReadingPage : Component<ReadingPageState, ActivityProps>
{
    [Inject] ElevenLabsSpeechService _speechService;
    [Inject] LearningResourceRepository _resourceRepository;
    LocalizationManager _localize => LocalizationManager.Instance;
    
    private TimestampedAudioManager _audioManager;
    private SentenceTimingCalculator _timingCalculator;
    
    public override VisualNode Render()
    {
        if (State.IsBusy)
        {
            return ContentPage($"{_localize["Reading"]}",
                VStack(
                    ActivityIndicator()
                        .IsRunning(true)
                        .VCenter()
                        .HCenter(),
                    Label("Loading content...")
                        .HCenter()
                        .ThemeKey(ApplicationTheme.Body1)
                )
                .VCenter()
                .HCenter()
            );
        }
        
        if (!string.IsNullOrEmpty(State.ErrorMessage))
        {
            return ContentPage($"{_localize["Reading"]}",
                VStack(
                    Label("⚠️")
                        .FontSize(48)
                        .HCenter(),
                    Label(State.ErrorMessage)
                        .HCenter()
                        .ThemeKey(ApplicationTheme.Body1),
                    Button("Go Back")
                        .OnClicked(GoBack)
                        .HCenter()
                )
                .VCenter()
                .HCenter()
                .Spacing(ApplicationTheme.Size160)
            );
        }

        return ContentPage($"{_localize["Reading"]}",
            ToolbarItem()
                .IconImageSource(ApplicationTheme.IconFontDecrease)
                .OnClicked(DecreaseFontSize),
            ToolbarItem()
                .IconImageSource(ApplicationTheme.IconFontIncrease)
                .OnClicked(IncreaseFontSize),
            ToolbarItem()
                .IconImageSource(ApplicationTheme.IconDelete)
                .OnClicked(ClearAudioCache),
            ToolbarItem()
                .IconImageSource(State.IsAudioPlaying ? ApplicationTheme.IconPause : ApplicationTheme.IconPlay)
                .OnClicked(TogglePlayback),
            Grid(rows:"Auto,Auto,*,Auto", columns:"*",
                RenderAudioLoadingBanner(),
                // RenderHeader(),
                RenderReadingContent(),
                RenderAudioControls(),
                RenderVocabularyBottomSheet()
            )
        )
        .OnAppearing(LoadContentAsync);
    }
    
    VisualNode RenderAudioLoadingBanner() =>
        Border(
            HStack(
                ActivityIndicator()
                    .IsRunning(State.IsGeneratingAudio)
                    .Color(ApplicationTheme.Primary),
                Label(State.AudioGenerationStatus)
                    .ThemeKey(ApplicationTheme.Body1)
                    .VCenter(),
                ProgressBar()
                    .Progress(State.AudioGenerationProgress)
                    .ProgressColor(ApplicationTheme.Primary)
                    .HorizontalOptions(LayoutOptions.FillAndExpand)
                    .IsVisible(State.AudioGenerationProgress > 0)
            )
            .Spacing(ApplicationTheme.Size120)
            .Padding(ApplicationTheme.Size160)
        )
        .Background(ApplicationTheme.Secondary.WithAlpha(0.2f))
        .Stroke(ApplicationTheme.Primary.WithAlpha(0.3f))
        .GridRow(0)
        .IsVisible(State.IsGeneratingAudio);

    VisualNode RenderHeader() =>
        Grid(rows: "*", columns: "*,Auto,Auto,Auto",
            ImageButton()
                .Source(ApplicationTheme.IconFontDecrease)
                .OnClicked(DecreaseFontSize)
                .GridColumn(1)
                .Padding(4),
            ImageButton()
                .Source(ApplicationTheme.IconFontIncrease)
                .OnClicked(IncreaseFontSize)
                .GridColumn(2)
                .Padding(4),
            ImageButton()
                .Source(State.IsAudioPlaying ? ApplicationTheme.IconPause : ApplicationTheme.IconPlay)
                .OnClicked(TogglePlayback)
                .GridColumn(3)
            // Label($"{State.PlaybackSpeed:F1}x")
            //     .OnTapped(CyclePlaybackSpeed)
            //     .GridColumn(4)
            //     .VCenter()
            //     .Padding(ApplicationTheme.Size80)
        )
        .ColumnSpacing(ApplicationTheme.LayoutSpacing)
        .Padding(ApplicationTheme.Size160)
        .GridRow(0);
    
    VisualNode RenderReadingContent() =>
        ScrollView(
            VStack(
                new VisualNode[] { Label(State.Resource?.Title ?? "Reading")
                .FontAttributes(FontAttributes.Bold)
                .FontSize(18)
                .GridColumn(0)
                .VCenter()
                .ThemeKey(ApplicationTheme.Title3) }
                    .Concat(RenderParagraphs())
                    .ToArray()
            )
            .Spacing(ApplicationTheme.Size160)
            .Padding(ApplicationTheme.Size240)
        )
        .GridRow(2);
    
    VisualNode[] RenderParagraphs()
    {
        var paragraphs = new List<VisualNode>();
        var paragraphGroups = GroupSentencesIntoParagraphs();
        
        foreach (var (paragraphSentences, paragraphIndex) in paragraphGroups.Select((p, i) => (p, i)))
        {
            var spans = new List<VisualNode>();
            
            foreach (var (sentence, sentenceIndex) in paragraphSentences)
            {
                var segments = ParseSentenceForVocabulary(sentence);
                
                foreach (var segment in segments)
                {
                    var textColor = GetTextColorForSentence(sentenceIndex);
                    
                    if (segment.IsVocabulary)
                    {
                        // Vocabulary word with interaction
                        spans.Add(
                            Span(segment.Text, 
                                ApplicationTheme.Primary, 
                                FontAttributes.None,
                                TapGestureRecognizer().OnTapped(() => ShowVocabularyBottomSheet(segment.VocabularyWord)))
                                .TextDecorations(TextDecorations.Underline)
                        );
                    }
                    else
                    {
                        // Regular text with highlighting if active
                        spans.Add(Span(segment.Text, textColor));
                    }
                }
                
                // Add space between sentences
                if (sentence != paragraphSentences.Last().Item1)
                {
                    spans.Add(Span(" "));
                }
            }
            
            paragraphs.Add(
                VStack(
                    Label(FormattedString(spans.ToArray()))
                        .FontSize(State.FontSize)
                        .LineHeight(1.5)
                )
                .Padding(ApplicationTheme.Size120)
                .OnTapped(() => SelectParagraph(paragraphSentences))
                .OnTapped(() => StartPlaybackFromParagraph(paragraphSentences), 2)
            );
        }
        
        return paragraphs.ToArray();
    }
    
    List<List<(string, int)>> GroupSentencesIntoParagraphs()
    {
        // Simple paragraph grouping - every 3-4 sentences for now
        // In the future, this could be enhanced with ML or natural language processing
        var paragraphs = new List<List<(string, int)>>();
        var currentParagraph = new List<(string, int)>();
        
        for (int i = 0; i < State.Sentences.Count; i++)
        {
            currentParagraph.Add((State.Sentences[i], i));
            
            // Start new paragraph every 3-4 sentences or at natural breaks
            if (currentParagraph.Count >= 3 && (i == State.Sentences.Count - 1 || ShouldBreakParagraph(State.Sentences[i])))
            {
                paragraphs.Add(currentParagraph);
                currentParagraph = new List<(string, int)>();
            }
        }
        
        // Add any remaining sentences
        if (currentParagraph.Any())
        {
            paragraphs.Add(currentParagraph);
        }
        
        return paragraphs;
    }
    
    bool ShouldBreakParagraph(string sentence)
    {
        // Natural paragraph break indicators
        var breakIndicators = new[] { "However", "Meanwhile", "In addition", "Furthermore", "Therefore", "Consequently" };
        return breakIndicators.Any(indicator => sentence.StartsWith(indicator, StringComparison.OrdinalIgnoreCase));
    }
    
    Color GetTextColorForSentence(int sentenceIndex)
    {
        if (sentenceIndex == State.CurrentSentenceIndex && State.IsAudioPlaying)
            return ApplicationTheme.Primary; // Use secondary color for sentence highlighting (different from vocabulary Primary)
        else
            return ApplicationTheme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground;
    }
    
    async Task SelectParagraph(List<(string, int)> paragraphSentences)
    {
        var firstSentenceIndex = paragraphSentences.First().Item2;
        SetState(s => s.SelectedSentenceIndex = firstSentenceIndex);
        
        // Show helpful hint for first-time users
        if (!State.HasShownJumpHint)
        {
            await AppShell.DisplayToastAsync("💡 Double-tap to play from here!");
            SetState(s => s.HasShownJumpHint = true);
            Preferences.Set("ReadingActivity_HasShownJumpHint", true);
        }
    }
    
    Task StartPlaybackFromParagraph(List<(string, int)> paragraphSentences)
    {
        var firstSentenceIndex = paragraphSentences.First().Item2;
        return StartPlaybackFromSentence(firstSentenceIndex);
    }
    
    VisualNode RenderReadingInstructions() =>
        Border(
            HStack(
                Label("💡")
                    .FontSize(16),
                VStack(
                    Label("Reading Controls:")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(14)
                            .ThemeKey(ApplicationTheme.Caption1),
                        Label("• Use A-/A+ buttons to adjust text size")
                            .FontSize(12)
                            .ThemeKey(ApplicationTheme.Body1),
                        Label("• Tap vocabulary words for translations")
                            .FontSize(12)
                            .ThemeKey(ApplicationTheme.Body1),
                        Label("• Double-tap sentences to play from there")
                            .FontSize(12)
                            .ThemeKey(ApplicationTheme.Body1)
                    )
                    .Spacing(2),
                    Button("✕")
                        .FontSize(12)
                        .OnClicked(DismissInstructions)
                        .HorizontalOptions(LayoutOptions.End)
                )
                .Spacing(ApplicationTheme.Size120)
            )
            .Background(ApplicationTheme.Secondary.WithAlpha(0.3f))
            .Stroke(ApplicationTheme.Primary.WithAlpha(0.5f))
            .Padding(ApplicationTheme.Size120)
            .Margin(ApplicationTheme.Size160)
            .IsVisible(!State.HasDismissedInstructions);
    
    void DismissInstructions()
    {
        SetState(s => s.HasDismissedInstructions = true);
        Preferences.Set("ReadingActivity_HasDismissedInstructions", true);
    }
    
    VisualNode RenderAudioControls() =>
        Grid("*", "Auto,*,Auto",
            ImageButton()
                .Source(ApplicationTheme.IconPreviousSm)
                .OnClicked(PreviousSentence)
                .GridColumn(0),
            Label($"Sentence {State.CurrentSentenceIndex + 1} of {State.Sentences.Count}")
                .GridColumn(1)
                .VCenter()
                .HCenter()
                .ThemeKey(ApplicationTheme.Caption1),
            ImageButton()
                .Source(ApplicationTheme.IconNextSm)
                .OnClicked(NextSentence)
                .GridColumn(2)
            // Label($"{State.PlaybackSpeed:F1}x")
            //     .OnTapped(CyclePlaybackSpeed)
            //     .GridColumn(4)
            //     .VCenter()
            //     .Padding(ApplicationTheme.Size80)
        )
        .Padding(ApplicationTheme.Size160)
        .GridRow(3)
        .IsVisible(State.Sentences.Any());
    
    VisualNode RenderVocabularyBottomSheet() =>
        new SfBottomSheet(
            ScrollView(
                VStack(
                    Label(State.SelectedVocabulary?.TargetLanguageTerm)
                        .FontSize(24)
                        .FontAttributes(FontAttributes.Bold)
                        .ThemeKey(ApplicationTheme.Title1)
                        .HCenter(),
                    Label(State.SelectedVocabulary?.NativeLanguageTerm)
                        .FontSize(18)
                        .ThemeKey(ApplicationTheme.Body1)
                        .HCenter(),
                    Button("Close")
                        .OnClicked(CloseVocabularyBottomSheet)
                        .HCenter()
                )
                .Spacing(ApplicationTheme.Size160)
                .Padding(ApplicationTheme.Size240)
            )
        )
        .GridRowSpan(4)
        .IsOpen(State.IsVocabularyBottomSheetVisible);
    
    // Audio Management
    async Task StartPlaybackFromSentence(int startIndex)
    {
        if (_audioManager == null || !State.IsTimestampedAudioLoaded) 
        {
            await AppShell.DisplayToastAsync("🏴‍☠️ Audio not ready yet, Captain!");
            return;
        }
        
        if (startIndex < 0 || startIndex >= State.Sentences.Count)
            return;

        StopCurrentPlayback();
        
        SetState(s => {
            s.CurrentSentenceIndex = startIndex;
            s.IsAudioPlaying = true;
        });
        
        try
        {
            // Use timestamp-based playback
            await _audioManager.PlayFromSentenceAsync(startIndex);
        }
        catch (Exception ex)
        {
            await AppShell.DisplayToastAsync($"🏴‍☠️ Playback error: {ex.Message}");
        }
    }
    
    Task PlaySentenceFromCache(int index, string audioFilePath)
    {
        // This method is now handled by StartPlaybackFromSentence with timestamps
        return StartPlaybackFromSentence(index);
    }
    
    Task PlaySentence(int index)
    {
        // This method is now handled by StartPlaybackFromSentence with timestamps
        return StartPlaybackFromSentence(index);
    }
    
    private void OnSentencePlaybackEnded(object sender, EventArgs e)
    {
        // Auto-advance to next sentence
        if (State.CurrentSentenceIndex < State.Sentences.Count - 1)
        {
            var nextIndex = State.CurrentSentenceIndex + 1;
            Task.Run(() => StartPlaybackFromSentence(nextIndex));
        }
        else
        {
            // End of reading
            SetState(s => {
                s.IsAudioPlaying = false;
                s.CurrentSentenceIndex = -1;
            });
        }
    }
    
    void StopCurrentPlayback()
    {
        if (_audioManager != null)
        {
            _audioManager.Stop();
        }
        
        SetState(s => s.IsAudioPlaying = false);
    }
    
    async Task TogglePlayback()
    {
        // Check if audio is still loading
        if (State.IsGeneratingAudio)
        {
            await AppShell.DisplayToastAsync("🏴‍☠️ Hold yer horses! Audio is still loading, Captain!");
            return;
        }
        
        if (!State.IsTimestampedAudioLoaded)
        {
            await AppShell.DisplayToastAsync("🏴‍☠️ No audio loaded yet, Captain! Try again in a moment.");
            return;
        }

        if (State.IsAudioPlaying)
        {
            StopCurrentPlayback();
        }
        else
        {
            // Resume from current position or start from beginning
            var startIndex = State.CurrentSentenceIndex >= 0 ? 
                State.CurrentSentenceIndex : 0;
            await StartPlaybackFromSentence(startIndex);
        }
    }
    
    async Task PreviousSentence()
    {
        if (State.CurrentSentenceIndex > 0)
        {
            // Use TimestampedAudioManager navigation to maintain playback
            if (_audioManager != null)
            {
                await _audioManager.PreviousSentenceAsync();
            }
            else
            {
                // Fallback if no audio manager
                await StartPlaybackFromSentence(State.CurrentSentenceIndex - 1);
            }
        }
    }
    
    async Task NextSentence()
    {
        if (State.CurrentSentenceIndex < State.Sentences.Count - 1)
        {
            // Use TimestampedAudioManager navigation to maintain playback
            if (_audioManager != null)
            {
                await _audioManager.NextSentenceAsync();
            }
            else
            {
                // Fallback if no audio manager
                await StartPlaybackFromSentence(State.CurrentSentenceIndex + 1);
            }
        }
    }
    
    void CyclePlaybackSpeed()
    {
        var speeds = new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };
        var currentIndex = Array.IndexOf(speeds, State.PlaybackSpeed);
        var nextIndex = (currentIndex + 1) % speeds.Length;
        
        SetState(s => s.PlaybackSpeed = speeds[nextIndex]);
        Preferences.Set("ReadingActivity_PlaybackSpeed", State.PlaybackSpeed);
    }
    
    void IncreaseFontSize()
    {
        var newSize = Math.Min(State.FontSize + 2, 32.0); // Max font size 32
        SetState(s => s.FontSize = newSize);
        Preferences.Set("ReadingActivity_FontSize", State.FontSize);
    }
    
    void DecreaseFontSize()
    {
        var newSize = Math.Max(State.FontSize - 2, 12.0); // Min font size 12
        SetState(s => s.FontSize = newSize);
        Preferences.Set("ReadingActivity_FontSize", State.FontSize);
    }
    
    async Task ClearAudioCache()
    {
        try
        {
            // Stop any current playback
            StopCurrentPlayback();
            
            // Clear timestamped audio (real-time system doesn't use cache files)
            SetState(s => {
                s.TimestampedAudio = null;
                s.IsTimestampedAudioLoaded = false;
            });
            
            await AppShell.DisplayToastAsync("🏴‍☠️ Audio cache cleared! Fresh voices ahead, Captain!");
        }
        catch (Exception ex)
        {
            await AppShell.DisplayToastAsync($"Failed to clear cache: {ex.Message}");
        }
    }
    
    // Content Processing
    List<string> SplitIntoSentences(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new List<string>();
        
        // Handle multiple sentence delimiters and clean whitespace
        var sentences = transcript
            .Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s + (s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? "" : "."))
            .ToList();
        
        return sentences;
    }
    
    List<TextSegment> ParseSentenceForVocabulary(string sentence)
    {
        var segments = new List<TextSegment>();
        var words = sentence.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var word in words)
        {
            var vocabularyMatch = FindVocabularyMatch(word);
            
            if (vocabularyMatch != null)
            {
                segments.Add(new TextSegment
                {
                    Text = word,
                    IsVocabulary = true,
                    VocabularyWord = vocabularyMatch
                });
            }
            else
            {
                segments.Add(new TextSegment
                {
                    Text = word,
                    IsVocabulary = false
                });
            }
            
            // Add space after each word except the last
            if (word != words.Last())
            {
                segments.Add(new TextSegment { Text = " ", IsVocabulary = false });
            }
        }
        
        return segments;
    }
    
    VocabularyWord FindVocabularyMatch(string word)
    {
        var cleanWord = word.Trim().ToLowerInvariant()
            .TrimEnd('.', ',', '!', '?', ';', ':');
        
        return State.VocabularyWords?.FirstOrDefault(v => 
            v.TargetLanguageTerm?.ToLowerInvariant() == cleanWord ||
            v.TargetLanguageTerm?.ToLowerInvariant().Contains(cleanWord) == true
        );
    }
    
    // Vocabulary UI
    void ShowVocabularyBottomSheet(VocabularyWord vocabularyWord)
    {
        SetState(s => {
            s.SelectedVocabulary = vocabularyWord;
            s.IsVocabularyBottomSheetVisible = true;
        });
    }
    
    void CloseVocabularyBottomSheet()
    {
        SetState(s => s.IsVocabularyBottomSheetVisible = false);
    }
    
    // Navigation
    async Task GoBack()
    {
        StopCurrentPlayback();
        MauiControls.Shell.Current.GoToAsync("..");
    }
    
    // Lifecycle
    protected override void OnMounted()
    {
        base.OnMounted();
        
        if (Props?.Resource == null)
        {
            SetState(s => s.ErrorMessage = "No resource provided");
            return;
        }
        
        if (string.IsNullOrWhiteSpace(Props.Resource.Transcript))
        {
            SetState(s => s.ErrorMessage = "Resource has no transcript");
            return;
        }
        
        // Initialize in background
        Task.Run(async () =>
        {
            try
            {
                var fullResource = await _resourceRepository.GetResourceAsync(Props.Resource.Id);
                
                SetState(s =>
                {
                    s.Resource = fullResource;
                    s.Sentences = SplitIntoSentences(fullResource.Transcript ?? "");
                    s.FontSize = Preferences.Get("ReadingActivity_FontSize", 18.0);
                    s.PlaybackSpeed = Preferences.Get("ReadingActivity_PlaybackSpeed", 1.0f);
                    s.HasShownJumpHint = Preferences.Get("ReadingActivity_HasShownJumpHint", false);
                    s.HasDismissedInstructions = Preferences.Get("ReadingActivity_HasDismissedInstructions", false);
                    s.IsBusy = false;
                });
                
                // Initialize timestamped audio system
                await InitializeAudioSystemAsync();
            }
            catch (Exception ex)
            {
                SetState(s => {
                    s.ErrorMessage = $"Failed to load content: {ex.Message}";
                    s.IsBusy = false;
                });
            }
        });
    }
    
    private async Task InitializeAudioSystemAsync()
    {
        if (State.Resource?.Transcript == null) return;

        // Show loading state
        SetState(s => {
            s.IsGeneratingAudio = true;
            s.AudioGenerationStatus = "🏴‍☠️ Initializing audio system...";
            s.AudioGenerationProgress = 0.1;
        });

        System.Diagnostics.Debug.WriteLine("🏴‍☠️ InitializeAudioSystemAsync: Loading state set, should be visible now");
        
        // Add a small delay to ensure loading UI shows
        await Task.Delay(500);

        try
        {
            System.Diagnostics.Debug.WriteLine("🏴‍☠️ InitializeAudioSystemAsync: Starting audio system initialization");

            _timingCalculator = new SentenceTimingCalculator();
            _audioManager = new TimestampedAudioManager(_timingCalculator);
            
            // Update progress
            SetState(s => {
                s.AudioGenerationStatus = "🏴‍☠️ Generating timestamped audio...";
                s.AudioGenerationProgress = 0.3;
            });
            
            // Generate timestamped audio for the entire resource
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ InitializeAudioSystemAsync: Generating audio for transcript length: {State.Resource.Transcript.Length}");
            var timestampedAudioResult = await _speechService.GenerateTimestampedAudioAsync(State.Resource);
            
            if (timestampedAudioResult != null)
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ InitializeAudioSystemAsync: Generated audio with {timestampedAudioResult.Characters.Length} characters, duration: {timestampedAudioResult.Duration.TotalSeconds:F1}s");
                
                // Update progress
                SetState(s => {
                    s.AudioGenerationStatus = "🏴‍☠️ Processing character timestamps...";
                    s.AudioGenerationProgress = 0.7;
                });
                
                // Convert to the new TimestampedAudio model
                var timestampedAudio = new TimestampedAudio
                {
                    AudioData = timestampedAudioResult.AudioData.ToArray(),
                    Characters = timestampedAudioResult.Characters,
                    FullTranscript = State.Resource.Transcript,
                    Duration = timestampedAudioResult.Duration.TotalSeconds
                };
                
                // Update progress
                SetState(s => {
                    s.AudioGenerationStatus = "🏴‍☠️ Loading audio into player...";
                    s.AudioGenerationProgress = 0.9;
                });
                
                // Load audio into manager (no pre-calculated timings needed!)
                await _audioManager.LoadAudioAsync(timestampedAudio);
                _audioManager.SentenceChanged += OnCurrentSentenceChanged;
                _audioManager.PlaybackEnded += OnPlaybackEnded;
                
                // Update state with loaded status
                SetState(s => 
                {
                    s.IsTimestampedAudioLoaded = true;
                    s.TimestampedAudio = timestampedAudio;
                    s.IsGeneratingAudio = false;
                    s.AudioGenerationStatus = "🏴‍☠️ Audio ready for playback!";
                    s.AudioGenerationProgress = 1.0;
                });
                
                System.Diagnostics.Debug.WriteLine("🏴‍☠️ Real-time audio system initialized with character-level timestamps!");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("🏴‍☠️ InitializeAudioSystemAsync: Failed to generate timestamped audio");
                SetState(s => {
                    s.IsGeneratingAudio = false;
                    s.AudioGenerationStatus = "⚠️ Failed to generate audio";
                    s.AudioGenerationProgress = 0.0;
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Error initializing audio: {ex.Message}");
            SetState(s => {
                s.IsGeneratingAudio = false;
                s.AudioGenerationStatus = $"⚠️ Audio error: {ex.Message}";
                s.AudioGenerationProgress = 0.0;
            });
        }
    }

    private void OnCurrentSentenceChanged(object? sender, int sentenceIndex)
    {
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ OnCurrentSentenceChanged: RECEIVED EVENT! New sentence index {sentenceIndex}");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ OnCurrentSentenceChanged: Previous sentence index was {State.CurrentSentenceIndex}");
        
        SetState(s => {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ OnCurrentSentenceChanged: Setting state from {s.CurrentSentenceIndex} to {sentenceIndex}");
            s.CurrentSentenceIndex = sentenceIndex;
        });
        
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ OnCurrentSentenceChanged: State updated, current sentence is now {State.CurrentSentenceIndex}");
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ OnPlaybackEnded: Playback finished");
        SetState(s => s.IsPlaying = false);
    }
    
    protected override void OnWillUnmount()
    {
        base.OnWillUnmount();
        
        // Clean up audio resources
        StopCurrentPlayback();
        
        // Clean up TimestampedAudioManager
        if (_audioManager != null)
        {
            _audioManager.SentenceChanged -= OnCurrentSentenceChanged;
            _audioManager.PlaybackEnded -= OnPlaybackEnded;
            _audioManager.Dispose();
        }
    }
    
    async Task LoadContentAsync()
    {
        SetState(s => s.IsBusy = true);
        
        try
        {
            // Load complete resource with vocabulary
            var fullResource = await _resourceRepository.GetResourceAsync(Props.Resource.Id);
            
            SetState(s => {
                s.Resource = fullResource;
                s.Sentences = SplitIntoSentences(fullResource.Transcript);
                s.VocabularyWords = fullResource.Vocabulary ?? new List<VocabularyWord>();
                s.FontSize = Preferences.Get("ReadingActivity_FontSize", 18.0);
                s.PlaybackSpeed = Preferences.Get("ReadingActivity_PlaybackSpeed", 1.0f);
                s.HasShownJumpHint = Preferences.Get("ReadingActivity_HasShownJumpHint", false);
                s.HasDismissedInstructions = Preferences.Get("ReadingActivity_HasDismissedInstructions", false);
                s.IsBusy = false;
            });
            
            // Initialize timestamped audio system
            await InitializeAudioSystemAsync();
        }
        catch (Exception ex)
        {
            SetState(s => {
                s.ErrorMessage = $"Failed to load content: {ex.Message}";
                s.IsBusy = false;
            });
        }
    }
}