using Plugin.Maui.Audio;
using System.Text.RegularExpressions;

namespace SentenceStudio.Pages.Reading;

class ReadingPageState
{
    // Content
    public LearningResource Resource { get; set; }
    public List<string> Sentences { get; set; } = new();
    public List<VocabularyWord> VocabularyWords { get; set; } = new();
    
    // Audio
    public bool IsAudioPlaying { get; set; } = false;
    public bool IsAudioLoading { get; set; } = false;
    public float PlaybackSpeed { get; set; } = 1.0f;
    public Dictionary<int, string> AudioCache { get; set; } = new(); // sentence index -> audio file path
    
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
    
    private IAudioPlayer _audioPlayer;
    
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
                        .ThemeKey(ApplicationTheme.Body)
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
                        .ThemeKey(ApplicationTheme.Body),
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
            Grid(rows: "Auto,*,Auto", columns: "*",
                RenderHeader(),
                RenderReadingContent(),
                RenderAudioControls(),
                RenderVocabularyBottomSheet()
            )
        ).OnAppearing(LoadContentAsync);
    }
    
    VisualNode RenderHeader() =>
        Grid(columns: "Auto,*,Auto,Auto", 
            Button("←")
                .OnClicked(GoBack)
                .GridColumn(0),
            Label(State.Resource?.Title ?? "Reading")
                .FontAttributes(FontAttributes.Bold)
                .FontSize(18)
                .GridColumn(1)
                .VCenter()
                .ThemeKey(ApplicationTheme.Title3),
            Button()
                .Text(State.IsAudioPlaying ? "⏸️" : "▶️")
                .OnClicked(TogglePlayback)
                .GridColumn(2),
            Label($"{State.PlaybackSpeed:F1}x")
                .OnTapped(CyclePlaybackSpeed)
                .GridColumn(3)
                .VCenter()
                .Padding(ApplicationTheme.Size80)
        )
        .Padding(ApplicationTheme.Size160)
        .GridRow(0);
    
    VisualNode RenderReadingContent() =>
        ScrollView(
            VStack(
                // Optional reading instructions
                RenderReadingInstructions(),
                
                // Main content with sentences
                State.Sentences.Select((sentence, index) => 
                    RenderSentence(sentence, index)
                ).ToArray()
            )
            .Spacing(ApplicationTheme.Size160)
            .Padding(ApplicationTheme.Size240)
        )
        .GridRow(1);
    
    VisualNode RenderSentence(string sentence, int index) =>
        VStack(
            RenderSentenceWithVocabulary(sentence, index)
        )
        .Background(GetSentenceBackground(index))
        .Padding(ApplicationTheme.Size120)
        .OnTapped(() => SelectSentence(index))        // Single tap = select only
        .OnDoubleTapped(() => StartPlaybackFromSentence(index));  // Double tap = play from here
    
    Color GetSentenceBackground(int index)
    {
        if (index == State.CurrentSentenceIndex && State.IsAudioPlaying)
            return ApplicationTheme.Primary.WithAlpha(0.3f); // Bright highlight for current playing
        else if (index == State.SelectedSentenceIndex)
            return ApplicationTheme.Secondary.WithAlpha(0.5f); // Subtle selection
        else
            return Colors.Transparent;
    }
    
    async Task SelectSentence(int index)
    {
        SetState(s => s.SelectedSentenceIndex = index);
        
        // Show helpful hint for first-time users
        if (!State.HasShownJumpHint)
        {
            await AppShell.DisplayToastAsync("💡 Double-tap to play from here!");
            SetState(s => s.HasShownJumpHint = true);
            Preferences.Set("ReadingActivity_HasShownJumpHint", true);
        }
    }
    
    VisualNode RenderSentenceWithVocabulary(string sentence, int index)
    {
        var words = SplitSentenceIntoWords(sentence);
        var spans = new List<VisualNode>();
        
        foreach (var word in words)
        {
            var vocabularyWord = FindVocabularyMatch(word);
            if (vocabularyWord != null)
            {
                spans.Add(
                    Span(word)
                        .TextDecorations(TextDecorations.Underline)
                        .TextColor(ApplicationTheme.Primary)
                        .OnTapped(() => ShowVocabularyBottomSheet(vocabularyWord))
                );
            }
            else
            {
                spans.Add(Span(word));
            }
            
            // Add space between words
            spans.Add(Span(" "));
        }
        
        return Label()
            .FormattedText(FormattedString(spans.ToArray()))
            .FontSize(State.FontSize)
            .LineHeight(1.4)
            .ThemeKey(ApplicationTheme.Body);
    }
    
    VisualNode RenderReadingInstructions() =>
        Border()
            .Background(ApplicationTheme.Secondary.WithAlpha(0.3f))
            .Stroke(ApplicationTheme.Primary.WithAlpha(0.5f))
            .Padding(ApplicationTheme.Size120)
            .Margin(ApplicationTheme.Size160)
            .IsVisible(!State.HasDismissedInstructions)
            .Content(
                HStack(
                    Label("💡")
                        .FontSize(16),
                    VStack(
                        Label("Reading Controls:")
                            .FontAttributes(FontAttributes.Bold)
                            .FontSize(14)
                            .ThemeKey(ApplicationTheme.Caption1),
                        Label("• Tap vocabulary words for translations")
                            .FontSize(12)
                            .ThemeKey(ApplicationTheme.Body),
                        Label("• Double-tap sentences to play from there")
                            .FontSize(12)
                            .ThemeKey(ApplicationTheme.Body)
                    )
                    .Spacing(2),
                    Button("✕")
                        .FontSize(12)
                        .OnClicked(DismissInstructions)
                        .HorizontalOptions(LayoutOptions.End)
                )
                .Spacing(ApplicationTheme.Size120)
            );
    
    void DismissInstructions()
    {
        SetState(s => s.HasDismissedInstructions = true);
        Preferences.Set("ReadingActivity_HasDismissedInstructions", true);
    }
    
    VisualNode RenderAudioControls() =>
        Grid(columns: "Auto,Auto,*,Auto,Auto",
            Button("⏮️") // Previous sentence
                .OnClicked(PreviousSentence)
                .GridColumn(0),
            Button()
                .Text(State.IsAudioPlaying ? "⏸️" : "▶️")
                .OnClicked(TogglePlayback)
                .GridColumn(1),
            Label($"Sentence {State.CurrentSentenceIndex + 1} of {State.Sentences.Count}")
                .GridColumn(2)
                .VCenter()
                .HCenter()
                .ThemeKey(ApplicationTheme.Caption1),
            Button("⏭️") // Next sentence
                .OnClicked(NextSentence)
                .GridColumn(3),
            Label($"{State.PlaybackSpeed:F1}x")
                .OnTapped(CyclePlaybackSpeed)
                .GridColumn(4)
                .VCenter()
                .Padding(ApplicationTheme.Size80)
        )
        .Padding(ApplicationTheme.Size160)
        .GridRow(2)
        .IsVisible(State.Sentences.Any());
    
    VisualNode RenderVocabularyBottomSheet() =>
        new SfBottomSheet(
            ScrollView(
                VStack(
                    Label(State.SelectedVocabulary?.TargetLanguageTerm)
                        .FontSize(24)
                        .FontAttributes(FontAttributes.Bold)
                        .ThemeKey(ApplicationTheme.Title2)
                        .HCenter(),
                    Label(State.SelectedVocabulary?.NativeLanguageTerm)
                        .FontSize(18)
                        .ThemeKey(ApplicationTheme.Body)
                        .HCenter(),
                    Button("Close")
                        .OnClicked(CloseVocabularyBottomSheet)
                        .HCenter()
                )
                .Spacing(ApplicationTheme.Size160)
                .Padding(ApplicationTheme.Size240)
            )
        )
        .IsOpen(State.IsVocabularyBottomSheetVisible);
    
    // Audio Management
    async Task StartPlaybackFromSentence(int startIndex)
    {
        StopCurrentPlayback();
        
        SetState(s => {
            s.CurrentSentenceIndex = startIndex;
            s.IsAudioLoading = true;
        });
        
        // Pre-generate audio for current and next few sentences
        var tasks = new List<Task>();
        for (int i = startIndex; i < Math.Min(startIndex + 3, State.Sentences.Count); i++)
        {
            tasks.Add(GenerateAudioForSentence(i));
        }
        await Task.WhenAll(tasks);
        
        SetState(s => s.IsAudioLoading = false);
        await PlaySentence(startIndex);
    }
    
    async Task PlaySentence(int index)
    {
        if (!State.AudioCache.ContainsKey(index))
        {
            await GenerateAudioForSentence(index);
        }
        
        var audioFilePath = State.AudioCache[index];
        if (File.Exists(audioFilePath))
        {
            _audioPlayer = AudioManager.Current.CreatePlayer(File.OpenRead(audioFilePath));
            _audioPlayer.PlaybackEnded += OnSentencePlaybackEnded;
            _audioPlayer.Play();
            
            SetState(s => {
                s.CurrentSentenceIndex = index;
                s.IsAudioPlaying = true;
            });
        }
    }
    
    private void OnSentencePlaybackEnded(object sender, EventArgs e)
    {
        // Auto-advance to next sentence
        if (State.CurrentSentenceIndex < State.Sentences.Count - 1)
        {
            var nextIndex = State.CurrentSentenceIndex + 1;
            Task.Run(() => PlaySentence(nextIndex));
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
    
    async Task GenerateAudioForSentence(int sentenceIndex)
    {
        var sentence = State.Sentences[sentenceIndex];
        var cacheKey = $"reading_{State.Resource.Id}_{sentenceIndex}";
        var audioFilePath = Path.Combine(FileSystem.AppDataDirectory, $"{cacheKey}.mp3");
        
        // Check cache first
        if (File.Exists(audioFilePath))
        {
            State.AudioCache[sentenceIndex] = audioFilePath;
            return;
        }
        
        try
        {
            // Generate audio using ElevenLabsSpeechService (same as How You Say)
            var audioStream = await _speechService.TextToSpeechAsync(
                text: sentence,
                voiceId: Voices.JiYoung, // Default voice
                speed: State.PlaybackSpeed
            );
            
            // Cache to disk
            using (var fileStream = File.Create(audioFilePath))
            {
                await audioStream.CopyToAsync(fileStream);
            }
            
            State.AudioCache[sentenceIndex] = audioFilePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ ReadingPage: Failed to generate audio for sentence {sentenceIndex}: {ex.Message}");
        }
    }
    
    void StopCurrentPlayback()
    {
        if (_audioPlayer != null)
        {
            _audioPlayer.PlaybackEnded -= OnSentencePlaybackEnded;
            
            if (_audioPlayer.IsPlaying)
            {
                _audioPlayer.Stop();
            }
            
            _audioPlayer.Dispose();
            _audioPlayer = null;
        }
        
        SetState(s => s.IsAudioPlaying = false);
    }
    
    async Task TogglePlayback()
    {
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
            await StartPlaybackFromSentence(State.CurrentSentenceIndex - 1);
        }
    }
    
    async Task NextSentence()
    {
        if (State.CurrentSentenceIndex < State.Sentences.Count - 1)
        {
            await StartPlaybackFromSentence(State.CurrentSentenceIndex + 1);
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
    
    VocabularyWord FindVocabularyMatch(string word)
    {
        var cleanWord = word.Trim().ToLowerInvariant()
            .TrimEnd('.', ',', '!', '?', ';', ':');
        
        return State.VocabularyWords?.FirstOrDefault(v => 
            v.TargetLanguageTerm?.ToLowerInvariant() == cleanWord ||
            v.TargetLanguageTerm?.ToLowerInvariant().Contains(cleanWord) == true
        );
    }
    
    List<string> SplitSentenceIntoWords(string sentence)
    {
        return sentence.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
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
        await MauiControls.Shell.Current.GoToAsync("..");
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