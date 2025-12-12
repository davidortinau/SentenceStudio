# 🏴‍☠️ Reading Activity Specification

## Overview

The Reading Activity provides an immersive reading experience for LearningResources with transcripts. It combines visual reading with interactive vocabulary support and synchronized audio playback to enhance language learning.

## Features

### 1. Primary Reading Display
- **Large, legible font** optimized for reading comprehension
- **Clean, distraction-free layout** with proper typography and spacing
- **Responsive design** that adapts to different screen sizes
- **Theme-aware styling** using ApplicationTheme color schemes

### 2. Interactive Vocabulary
- **Underlined vocabulary words** that are tappable
- **Vocabulary popup/tooltip** showing translation on tap
- **Smart vocabulary detection** using existing LearningResource vocabulary mappings
- **Visual feedback** for tapped vocabulary terms

### 3. Audio Playback Integration
- **Text-to-Speech generation** using the same API as "How You Say" feature
- **Play/Pause controls** with standard media player interface
- **Audio caching** for repeat playback performance
- **Speed control** for learner preferences

### 4. Basic Audio Navigation
- **Play/Pause controls** with standard media player interface
- **Previous/Next sentence buttons** for step-by-step audio control
- **Double-tap sentences** to jump audio playbook to that position
- **Visual feedback** showing current playing sentence

## Technical Architecture

### Page Structure
```
Pages/Reading/ReadingPage.cs
├── ReadingPageState
├── ReadingPage : Component<ReadingPageState, ActivityProps>
├── Services integration
└── Audio management
```

### State Management
```csharp
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
```

### Service Dependencies
- **ElevenLabsSpeechService** - Audio generation using same voices as "How You Say"
- **LearningResourceRepository** - Resource and vocabulary data
- **Plugin.Maui.Audio (AudioManager.Current.CreatePlayer)** - Media playback control
- **File caching system** - Persistent audio storage

## UI Components

### 1. Header Controls
```csharp
VisualNode RenderHeader() =>
    Grid("", "Auto,*,Auto,Auto,Auto",
        Button("←")
            .OnClicked(GoBack)
            .GridColumn(0),
        Label(State.Resource?.Title)
            .FontAttributes(FontAttributes.Bold)
            .FontSize(18)
            .GridColumn(1)
            .VCenter(),
        Button()
            .ImageSource(State.IsAudioPlaying ? "pause_icon" : "play_icon")
            .OnClicked(TogglePlayback)
            .GridColumn(2),
        Label($"{State.PlaybackSpeed}x")
            .OnTapped(CyclePlaybackSpeed)
            .GridColumn(3),
        Button("Settings")
            .OnClicked(ShowSettings)
            .GridColumn(4)
    );
```

### 2. Main Reading View

```csharp
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
    .OnDoubleTapped(() => StartPlaybackFromSentence(index))  // Double tap = play from here
    .OnLongPressed(() => ShowSentenceOptions(index));       // Long press = context menu

Color GetSentenceBackground(int index)
{
    if (index == State.CurrentSentenceIndex && State.IsAudioPlaying)
        return ApplicationTheme.ActiveSentenceBackground; // Bright highlight for current playing
    else if (index == State.SelectedSentenceIndex)
        return ApplicationTheme.SelectedSentenceBackground; // Subtle selection
    else
        return Colors.Transparent;
}

async Task SelectSentence(int index)
{
    SetState(s => s.SelectedSentenceIndex = index);
    
    // Show helpful hint for first-time users
    if (!State.HasShownJumpHint)
    {
        await ShowToast("💡 Double-tap to play from here, or use the audio scrubber below");
        SetState(s => s.HasShownJumpHint = true);
        Preferences.Set("ReadingActivity_HasShownJumpHint", true);
    }
}

VisualNode RenderReadingInstructions() =>
    Border()
        .Background(ApplicationTheme.InfoBackground)
        .Stroke(ApplicationTheme.InfoBorder)
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
                        .FontSize(14),
                    Label("• Tap vocabulary words for translations")
                        .FontSize(12),
                    Label("• Double-tap sentences to play from there")
                        .FontSize(12),
                    Label("• Use scrubber bar below for quick navigation")
                        .FontSize(12)
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
```

### 3. Vocabulary Integration
```csharp
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
    }
    
    return Label()
        .FormattedText(FormattedString(spans.ToArray()))
        .FontSize(State.FontSize)
        .LineHeight(1.4);
}
```

### 4. Vocabulary Integration
```csharp
VisualNode RenderVocabularyBottomSheet() =>
    new SfBottomSheet(
        ScrollView(
            VStack(
                Label(State.SelectedVocabulary?.TargetLanguageTerm)
                    .FontSize(24)
                    .FontAttributes(FontAttributes.Bold)
                    .ThemeKey(ApplicationTheme.Title2),
                Label(State.SelectedVocabulary?.NativeLanguageTerm)
                    .FontSize(18)
                    .ThemeKey(ApplicationTheme.Body),
                Button("Close")
                    .OnClicked(CloseVocabularyBottomSheet)
            )
            .Spacing(ApplicationTheme.Size160)
            .Padding(ApplicationTheme.Size240)
        )
    )
    .IsOpen(State.IsVocabularyBottomSheetVisible);
```

### 5. Audio Controls
### 5. Audio Controls

```csharp
VisualNode RenderAudioControls() =>
    Grid("", "Auto,Auto,*,Auto,Auto,Auto",
        Button("⏮️") // Previous sentence
            .OnClicked(PreviousSentence)
            .GridColumn(0),
        Button()
            .ImageSource(State.IsAudioPlaying ? ApplicationTheme.IconPause : ApplicationTheme.IconPlay)
            .OnClicked(TogglePlayback)
            .GridColumn(1),
        ProgressBar()
            .Progress(GetPlaybackProgress())
            .GridColumn(2)
            .VCenter()
            .OnTapped(HandleProgressBarTap), // Tap to jump
        Button("⏭️") // Next sentence
            .OnClicked(NextSentence)
            .GridColumn(3),
        Label($"{State.PlaybackSpeed:F1}x")
            .OnTapped(CyclePlaybackSpeed)
            .GridColumn(4)
            .VCenter(),
        Button()
            .ImageSource(ApplicationTheme.IconSettings)
            .OnClicked(ShowAudioSettings)
            .GridColumn(5)
    )
    .Padding(ApplicationTheme.Size160)
    .GridRow(2);

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

async Task HandleProgressBarTap(TappedEventArgs e)
{
    // Calculate which sentence based on tap position
    var tapX = e.GetPosition(null).X;
    var progressBarWidth = _progressBar.Width;
    var sentenceIndex = (int)(tapX / progressBarWidth * State.Sentences.Count);
    
    await StartPlaybackFromSentence(Math.Max(0, Math.Min(sentenceIndex, State.Sentences.Count - 1)));
}

float GetPlaybackProgress()
{
    if (State.Sentences.Count == 0) return 0f;
    return (float)State.CurrentSentenceIndex / State.Sentences.Count;
}
```

### 6. Audio Scrubber Bar

```csharp
VisualNode RenderAudioScrubber() =>
    Grid("", "*",
        // Background track
        BoxView()
            .Background(ApplicationTheme.AudioTrackBackground)
            .HeightRequest(4)
            .CornerRadius(2)
            .GridColumn(0),
            
        // Sentence markers
        HStack(
            State.Sentences.Select((_, index) => 
                RenderSentenceMarker(index)
            ).ToArray()
        )
        .GridColumn(0)
        .HorizontalOptions(LayoutOptions.Fill)
    )
    .Padding(ApplicationTheme.Size160)
    .GridRow(3); // Below audio controls

VisualNode RenderSentenceMarker(int index) =>
    Button()
        .WidthRequest(8)
        .HeightRequest(8)
        .CornerRadius(4)
        .Background(GetSentenceMarkerColor(index))
        .OnClicked(() => JumpToSentence(index))
        .Margin(2, 0);

Color GetSentenceMarkerColor(int index)
{
    if (index == State.CurrentSentenceIndex)
        return ApplicationTheme.Primary; // Currently playing
    else if (index < State.CurrentSentenceIndex)
        return ApplicationTheme.AudioPlayedMarker; // Already played
    else
        return ApplicationTheme.AudioUnplayedMarker; // Not yet played
}

async Task JumpToSentence(int index)
{
    await StartPlaybackFromSentence(index);
    
    // Scroll to the sentence in the reading view
    ScrollToSentence(index);
}

void ScrollToSentence(int index)
{
    // Implementation would depend on the specific ScrollView setup
    // This could use ScrollToAsync with calculated position
}
```"{State.PlaybackSpeed:F1}x")
            .OnTapped(CyclePlaybackSpeed)
            .GridColumn(2)
            .VCenter(),
        Button()
            .ImageSource(ApplicationTheme.IconSettings)
            .OnClicked(ShowAudioSettings)
            .GridColumn(3)
    )
    .Padding(ApplicationTheme.Size160)
    .GridRow(2);
```

## Audio System

### Audio Generation
```csharp
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
            voiceId: GetSelectedVoice(),
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
        Debug.WriteLine($"🏴‍☠️ ReadingPage: Failed to generate audio for sentence {sentenceIndex}: {ex.Message}");
    }
}
```

### Playback Management
```csharp
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
        
        // Scroll to current sentence
        ScrollToSentence(index);
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
```

## Text Processing

### Sentence Splitting
```csharp
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
```

### Vocabulary Matching
```csharp
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
```

## Settings & Preferences

### Font Size Control
```csharp
void AdjustFontSize(double delta)
{
    var newSize = Math.Max(12.0, Math.Min(32.0, State.FontSize + delta));
    SetState(s => s.FontSize = newSize);
    
    // Save to preferences
    Preferences.Set("ReadingActivity_FontSize", newSize);
}
```

### Voice Selection
```csharp
VisualNode RenderVoiceSelection() =>
    new SfBottomSheet(
        ScrollView(
            VStack(
                Label("Voice Selection")
                    .FontAttributes(FontAttributes.Bold)
                    .FontSize(18),
                _speechService.VoiceDisplayNames.Select(kvp =>
                    CreateVoiceOption(kvp.Key, kvp.Value)
                ).ToArray()
            )
            .Spacing(ApplicationTheme.Size120)
            .Padding(ApplicationTheme.Size240)
        )
    )
    .IsOpen(State.IsVoiceSelectionVisible);
```

## Navigation Integration

### Routes
```csharp
// AppShell registration
Routing.RegisterRoute("reading", typeof(ReadingPage));
```

### ActivityBorder Integration
```csharp
// Add to Dashboard activities
new ActivityBorder()
    .LabelText("📖 Reading")
    .Route("reading")
```

### Props Validation
```csharp
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
    
    LoadContent();
}

async Task LoadContent()
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
```

## Performance Considerations

### Audio Caching Strategy
- **Progressive loading**: Generate audio for current + next 2-3 sentences
- **Persistent cache**: Store audio files in app data directory
- **Cache cleanup**: Remove old audio files periodically
- **Background generation**: Pre-generate audio for smoother experience

### Memory Management
- **Dispose audio players** properly to prevent memory leaks
- **Limit concurrent audio generation** to avoid overwhelming the API
- **Use weak references** for event handlers where appropriate

### UI Performance
- **Virtualization**: Consider CollectionView for very long texts
- **Lazy loading**: Load vocabulary data on-demand
- **Debounced scrolling**: Optimize scroll-to-sentence functionality

## Accessibility

### Screen Reader Support
- **Proper semantic markup** for sentence structure
- **Audio descriptions** for interactive elements
- **Keyboard navigation** support

### Visual Accessibility
- **High contrast themes** support
- **Font scaling** for vision accessibility
- **Color-blind friendly** vocabulary highlighting (not just color-based)

## Error Handling

### Network Issues
```csharp
async Task HandleAudioGenerationError(Exception ex, int sentenceIndex)
{
    Debug.WriteLine($"🏴‍☠️ ReadingPage: Audio generation failed for sentence {sentenceIndex}: {ex.Message}");
    
    // Show user-friendly error
    await Application.Current.MainPage.DisplayAlert(
        "Audio Unavailable", 
        "Could not generate audio for this sentence. Check your internet connection.", 
        "OK");
    
    // Continue with next sentence if auto-playing
    if (State.IsAudioPlaying && sentenceIndex < State.Sentences.Count - 1)
    {
        await PlaySentence(sentenceIndex + 1);
    }
}
```

### Resource Validation
```csharp
bool ValidateResource(LearningResource resource)
{
    if (resource == null)
    {
        SetState(s => s.ErrorMessage = "No learning resource provided");
        return false;
    }
    
    if (string.IsNullOrWhiteSpace(resource.Transcript))
    {
        SetState(s => s.ErrorMessage = "This resource doesn't have a transcript for reading");
        return false;
    }
    
    return true;
}
```

## Testing Strategy

### Unit Tests
- **Text splitting logic** validation
- **Vocabulary matching** accuracy
- **Audio cache management** functionality

### Integration Tests
- **Audio generation** end-to-end
- **Playback synchronization** with highlighting
- **Navigation flow** from dashboard

### User Experience Tests
- **Reading flow** with different content lengths
- **Vocabulary interaction** responsiveness
- **Audio quality** and synchronization accuracy

## Phase 1.5: Paragraph Rendering Implementation

### Current Status
ReadingPage displays each sentence as individual Labels, but users prefer paragraph-style reading flow for better comprehension and natural text flow.

### Validated Solution
Use MauiReactor's fluent FormattedString with Span elements for vocabulary interaction, based on working examples from MauiReactor samples.

### Working Syntax Examples
```csharp
// From FormattedTextTestPage.cs - Interactive spans with gestures
Label(FormattedString(
    Span("Click Me!", Colors.Red, FontAttributes.Bold, TapGestureRecognizer().OnTapped(() => ...)),
    Span(" and me!", Colors.Blue, FontAttributes.Italic, TapGestureRecognizer().OnTapped(() => ...))
))

// From StatisticsPage.cs - Using native FormattedString
Label()
.FormattedText(new MauiControls.FormattedString
{
    Spans = {
        new MauiControls.Span { Text = "text", TextColor = Colors.Black, FontSize = 36 },
        new MauiControls.Span { Text = "more", TextColor = Colors.Gray, FontSize = 20 }
    }
})
```

### Implementation Strategy for Reading Activity
```csharp
// Paragraph rendering with vocabulary interaction
VStack([
    // For each paragraph group
    Label(FormattedString([
        // For each text segment in paragraph
        ..RenderParagraphSegments(paragraph, vocabularyWords, activeSentenceIndex)
    ]))
    .Padding(16, 8)
    .FontSize(State.FontSize)
])

private Span[] RenderParagraphSegments(List<Sentence> sentences, List<VocabularyWord> vocab, int? activeIndex)
{
    var spans = new List<Span>();
    
    foreach (var (sentence, index) in sentences.Select((s, i) => (s, i)))
    {
        var segments = ParseSentenceForVocabulary(sentence.Text, vocab);
        
        foreach (var segment in segments)
        {
            var spanColor = index == activeIndex 
                ? ApplicationTheme.HighlightBackground  // Active sentence
                : ApplicationTheme.DarkOnLightBackground;
                
            if (segment.IsVocabulary)
            {
                spans.Add(Span(
                    segment.Text, 
                    ApplicationTheme.VocabularyTextColor,
                    FontAttributes.None,
                    TapGestureRecognizer().OnTapped(() => ShowVocabularyPopup(segment.VocabularyWord))
                ));
            }
            else
            {
                spans.Add(Span(segment.Text, spanColor));
            }
        }
        
        // Add sentence spacing
        if (index < sentences.Count - 1)
            spans.Add(Span(" "));
    }
    
    return spans.ToArray();
}
```

### Benefits
- ✅ **Confirmed working syntax** from actual MauiReactor examples
- ✅ **Natural reading flow** with paragraph spacing
- ✅ **Individual word tap detection** via TapGestureRecognizer on vocabulary Spans
- ✅ **Sentence-level highlighting** by updating Span colors during audio playback
- ✅ **Vocabulary underlining** through TextDecorations.Underline on vocabulary Spans
- ✅ **Font size responsiveness** through Label.FontSize property

## Future Enhancements

### Phase 2 Features
1. **Reading comprehension questions** generated by AI
2. **Reading speed tracking** and analytics
3. **Bookmark/notes system** for specific sentences
4. **Reading history** and progress tracking
5. **Offline mode** with pre-downloaded audio
6. **Text highlighting** and annotation tools
7. **Multi-language transcript** support (side-by-side)

### Advanced Audio Features
1. **Variable speed playback** with pitch preservation
2. **Sentence repeat mode** for difficult sections
3. **Background music** option for immersive reading
4. **Voice emotion/style** selection based on content type

This specification provides a comprehensive foundation for implementing a robust reading activity that enhances language learning through interactive text, vocabulary support, and synchronized audio playback while maintaining the pirate-themed personality of Captain Copilot! 🏴‍☠️
