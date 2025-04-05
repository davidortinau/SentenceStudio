using MauiReactor.Shapes;
using SentenceStudio.Pages.Dashboard;
using Plugin.Maui.Audio;
using MauiReactor.Compatibility;
using SentenceStudio.Pages.Controls;

namespace SentenceStudio.Pages.Shadowing;

/// <summary>
/// Container class for cached audio data and its metadata.
/// </summary>
class AudioCacheEntry
{
    /// <summary>
    /// Gets or sets the audio stream data.
    /// </summary>
    public Stream AudioStream { get; set; }
    
    /// <summary>
    /// Gets or sets the total audio duration in seconds.
    /// </summary>
    public double Duration { get; set; }
    
    /// <summary>
    /// Gets or sets the formatted duration display string.
    /// </summary>
    public string DurationDisplay { get; set; }
    
    /// <summary>
    /// Gets or sets the waveform data for visualization.
    /// </summary>
    public float[] WaveformData { get; set; }
}

/// <summary>
/// Page for the Shadowing activity where users can listen to spoken sentences and practice pronunciation.
/// </summary>
partial class ShadowingPage : Component<ShadowingPageState, ActivityProps>
{
    [Inject] ShadowingService _shadowingService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] AudioAnalyzer _audioAnalyzer;
    [Inject] ElevenLabsSpeechService _speechService;

    private IAudioPlayer _audioPlayer;
    private LocalizationManager _localize => LocalizationManager.Instance;
    private IDispatcherTimer _playbackTimer;
    private MauiControls.ScrollView _waveformScrollView;

    /// <summary>
    /// Dictionary to cache audio data by sentence text for reuse.
    /// </summary>
    private readonly Dictionary<string, AudioCacheEntry> _audioCache = new();

    /// <summary>
    /// Renders the ShadowingPage component.
    /// </summary>
    /// <returns>A visual node representing the page.</returns>
    public override VisualNode Render()
    {
        return ContentPage($"{_localize["Shadowing"]}",
            ToolbarItem($"{_localize["Refresh"]}").OnClicked(LoadSentences),
            Grid(rows: "*, Auto, 80", columns: "*",
                SentenceDisplay(),
                WaveformDisplay(),
                NavigationFooter(),
                LoadingOverlay(),
                RenderVoiceSelectionBottomSheet()
            )
            .RowSpacing(12)
        ).OnAppearing(OnPageAppearing);
    }
    
    /// <summary>
    /// Initializes the page and loads content when appearing
    /// </summary>
    private async void OnPageAppearing()
    {
        // Initialize voice display names from the service
        SetState(s => s.VoiceDisplayNames = _speechService.VoiceDisplayNames);
        
        // Load sentences if needed
        await Task.Delay(100); // Small delay to ensure UI is ready
        if (State.Sentences.Count == 0)
        {
            LoadSentences();
        }
    }

    /// <summary>
    /// Renders the voice selection bottom sheet.
    /// </summary>
    private VisualNode RenderVoiceSelectionBottomSheet() =>
        new SfBottomSheet(
                Grid("*", "*",
                    ScrollView(
                        VStack(
                            Label("Korean Voices")
                                .FontAttributes(FontAttributes.Bold)
                                .FontSize(18)
                                .TextColor(Theme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground)
                                .HCenter()
                                .Margin(0, 0, 0, 10),
                            CreateVoiceOption("yuna", "Yuna", "Female - Young, cheerful"),
                            CreateVoiceOption("jiyoung", "Ji-Young", "Female - Warm, clear"),
                            CreateVoiceOption("jina", "Jina", "Female - Mid-aged, news broadcaster"),
                            CreateVoiceOption("jennie", "Jennie", "Female - Youthful, professional"),
                            CreateVoiceOption("hyunbin", "Hyun-Bin", "Male - Cool, professional"),
                            CreateVoiceOption("dohyeon", "Do-Hyeon", "Male - Older, mature"),
                            CreateVoiceOption("yohankoo", "Yohan Koo", "Male - Confident, authoritative")
                        )
                        .Spacing(15)
                        .Padding(20, 10)
                    )
                )

        )
            .IsOpen(State.IsVoiceSelectionVisible);

    /// <summary>
    /// Creates a voice option item for the bottom sheet.
    /// </summary>
    private VisualNode CreateVoiceOption(string voiceId, string displayName, string description) =>
        Grid("*", "Auto,*",
            RadioButton()
                .IsChecked(State.SelectedVoiceId == voiceId)
                .GroupName("VoiceOptions")
                .OnCheckedChanged((sender, args) =>
                {
                    if (args.Value)
                    {
                        SelectVoice(voiceId);
                    }
                })
                .GridColumn(0),
            VStack(spacing: 0,
                Label(displayName)
                    .FontAttributes(FontAttributes.Bold)
                    .FontSize(16),
                Label(description)
                    .FontSize(14)
                    .TextColor(Colors.Gray)
            )
            .HStart()
            .GridColumn(1)
        )
        .OnTapped(() => SelectVoice(voiceId))
        ;
    
    /// <summary>
    /// Handles voice selection.
    /// </summary>
    private void SelectVoice(string voiceId)
    {
        // Update the selected voice
        SetState(s => {
            s.SelectedVoiceId = voiceId;
            
            // Reset audio cache when voice changes
            _audioCache.Clear();
            
            // Close the bottom sheet after selection
            s.IsVoiceSelectionVisible = false;
        });
        
        Debug.WriteLine($"Selected voice: {voiceId}");
    }
    
    /// <summary>
    /// Handles the bottom sheet closing event.
    /// </summary>
    private void OnVoiceSelectionBottomSheetClosing(object sender, EventArgs e)
    {
        SetState(s => s.IsVoiceSelectionVisible = false);
    }

    /// <summary>
    /// Creates a visual node for the sentence display area.
    /// </summary>
    /// <returns>A visual node for displaying the current sentence.</returns>
    private VisualNode SentenceDisplay() =>
        ScrollView(
            VStack(spacing: ApplicationTheme.Size160,
                Label(State.CurrentSentenceTranslation)
                    .ThemeKey("LargeTitle")
                    .HCenter()
                    .Margin(ApplicationTheme.Size160),
                    
                Label(State.CurrentSentenceText)
                    .ThemeKey("Title3")
                    .HCenter()
                    .TextColor(Colors.Gray)
                    .Margin(ApplicationTheme.Size160),

                Label(State.CurrentSentencePronunciationNotes)
                    .ThemeKey("Title3")
                    .HCenter()
                    .TextColor(Colors.DarkGray)
                    .Margin(ApplicationTheme.Size160)
            )
            .Padding(ApplicationTheme.Size160)
        ).GridRow(0);

    /// <summary>
    /// Creates a visual node for the waveform visualization display.
    /// </summary>
    /// <returns>A visual node for displaying the audio waveform.</returns>
    private VisualNode WaveformDisplay() =>
    VStack(
        Grid("Auto", "Auto,*,Auto",
            Label()
                .Text($"{State.CurrentTimeDisplay} / {State.DurationDisplay}")
                .FontAttributes(FontAttributes.Bold)
                .FontSize(14)
                .HStart()
                .GridColumn(2)
        ),
        Border(
            HScrollView(hscroll => _waveformScrollView = hscroll,

                Grid("100", "*",

                    new WaveformView()
                        .WaveColor(Theme.IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f))
                        .PlayedColor(Theme.IsLightTheme ? Colors.Orange : Colors.OrangeRed)
                        .Amplitude(0.8f)
                        .PlaybackPosition(State.PlaybackPosition)
                        .AudioId($"{State.CurrentSentenceIndex}_{State.CurrentSentenceText?.GetHashCode() ?? 0}") // Create a unique ID that combines sentence index and text to force refresh when either changes
                        .AudioDuration(State.AudioDuration)
                        .ShowTimeScale(true)
                        .WaveformData(State.WaveformData)
                        .OnInteractionStarted(OnWaveformInteractionStarted)
                        .OnPositionSelected(OnWaveformPositionSelected)
                )
            // .WidthRequest(State.AudioDuration > 0 ? Math.Max((float)(State.AudioDuration * 120), 300) : 300)

            )
            .Padding(0)
            .VerticalScrollBarVisibility(ScrollBarVisibility.Never)
            .HorizontalScrollBarVisibility(ScrollBarVisibility.Never)
            .Orientation(ScrollOrientation.Horizontal)
        )
            .StrokeShape(new RoundRectangle().CornerRadius(8))
            .StrokeThickness(1)
            .Stroke(Theme.IsLightTheme ? Colors.LightGray : Colors.DimGray)
            .HeightRequest(100)
            .Padding(ApplicationTheme.Size160, 0)
            .IsVisible(true)
        )
            .Spacing(ApplicationTheme.Size160)
            .Margin(20, 0)
            .GridRow(1);

    private void OnWaveformInteractionStarted()
    {
        PauseAudio();
    }

    /// <summary>
    /// Handles when the user selects a position on the waveform.
    /// </summary>
    /// <param name="normalizedPosition">The position as a value from 0 to 1.</param>
    private async void OnWaveformPositionSelected(float normalizedPosition)
    {
        Debug.WriteLine($"Waveform position selected: {normalizedPosition:F2}");
        
        if (State.IsAudioPlaying)
        {
            // If audio is already playing, pause it first
            await PauseAudio();
            
            // Then seek to the new position and resume
            await SeekAndResumeAudio(normalizedPosition);
        }
        else // if (State.IsPaused)
        {
            // If audio is paused, seek to new position and resume
            SeekAudio(normalizedPosition);
        }
        // else
        // {
        //     // If audio is not playing at all, start playback from the selected position
        //     await PlayAudioFromPosition(normalizedPosition);
        // }
    }
    
    /// <summary>
    /// Seeks to a specific position in the audio and resumes playback.
    /// </summary>
    /// <param name="normalizedPosition">The position to seek to (0-1).</param>
    private async Task SeekAndResumeAudio(float normalizedPosition)
    {
        if (_audioPlayer == null)
        {
            // If there's no player, we need to start fresh
            await PlayAudioFromPosition(normalizedPosition);
            return;
        }
        
        SeekAudio(normalizedPosition);
        
        // Resume playback
        _audioPlayer.Play();
        
        // Start the timer to track progress
        StartPlaybackTimer();
        
        // Update state
        SetState(s => 
        {
            s.IsAudioPlaying = true;
            s.IsPaused = false;
        });
        
        Debug.WriteLine($"Seeked to position {normalizedPosition:F2} and resumed playback");
    }
    
    /// <summary>
    /// Seeks the audio to a specific position without resuming playback.
    /// </summary>
    /// <param name="normalizedPosition">The position to seek to (0-1).</param>
    private void SeekAudio(float normalizedPosition)
    {
        if (_audioPlayer == null || _audioPlayer.Duration <= 0)
            return;

        // Seek to the requested position
        double seekPosition = _audioPlayer.Duration * normalizedPosition;
        _audioPlayer.Seek(seekPosition);

        // Update the position in the state
        SetState(s =>
        {
            s.PlaybackPosition = normalizedPosition;
            s.CurrentTimeDisplay = FormatTimeDisplay(seekPosition);
        });

        Debug.WriteLine($"Audio seeked to position {normalizedPosition:F2}");
    }
    
    /// <summary>
    /// Starts playing the audio from a specific position.
    /// </summary>
    /// <param name="normalizedPosition">The position to start from (0-1).</param>
    private async Task PlayAudioFromPosition(float normalizedPosition)
    {
        // First ensure we have audio loaded
        if (_audioPlayer == null)
        {
            // If no audio player exists, we need to create it first
            await PlayAudio();

            // If we still don't have a player after trying to create one, exit
            if (_audioPlayer == null)
                return;
        }

        // Now seek to the position and play
        double seekPosition = _audioPlayer.Duration * normalizedPosition;
        _audioPlayer.Seek(seekPosition);

        // Update the position in the state
        SetState(s => s.PlaybackPosition = normalizedPosition);

        // Resume playback
        _audioPlayer.Play();

        // Start the timer to track progress
        StartPlaybackTimer();

        // Update state
        SetState(s =>
        {
            s.IsAudioPlaying = true;
            s.IsPaused = false;
        });

        Debug.WriteLine($"Started playback from position {normalizedPosition:F2}");
    }

    /// <summary>
    /// Creates a visual node for the navigation footer containing controls for audio and navigation.
    /// </summary>
    /// <returns>A visual node representing the navigation footer.</returns>
    private VisualNode NavigationFooter() =>
            Grid(rows: "*", columns: "60,1,*,1,60",
                ImageButton()
                    .Background(Colors.Transparent)
                    .Aspect(Aspect.Center)
                    .Source(SegoeFluentIcons.Previous.ToImageSource())
                    .GridRow(0).GridColumn(0)
                    .OnClicked(PreviousSentence),
            State.IsAudioPlaying ?
                ImageButton()
                    .Source(ApplicationTheme.IconPause)
                    .Aspect(Aspect.Center)
                    .Background(Colors.Transparent)
                    .GridRow(0).GridColumn(2)
                    .HCenter()
                    .OnClicked(ToggleAudioPlayback)
                :
                Button()
                    .ImageSource(ApplicationTheme.IconPlay)
                    .Background(Colors.Transparent)
                    .GridRow(0).GridColumn(2)
                    .HCenter()
                    .OnClicked(ToggleAudioPlayback)
                ,
                ImageButton()
                    .Background(Colors.Transparent)
                    .Aspect(Aspect.Center)
                    .Source(SegoeFluentIcons.Next.ToImageSource())
                    .GridRow(0).GridColumn(4)
                    .OnClicked(NextSentence),

                BoxView()
                    .Color(Colors.Black)
                    .HeightRequest(1)
                    .GridColumnSpan(9)
                    .VStart(),

                BoxView()
                    .Color(Colors.Black)
                    .WidthRequest(1)
                    .GridRow(0).GridColumn(1),

                BoxView()
                    .Color(Colors.Black)
                    .WidthRequest(1)
                    .GridRow(0).GridColumn(3),

                

                HStack(
                    new SfSegmentedControl(
                        new SfSegmentItem()
                                .ImageSource(ApplicationTheme.IconSpeedVerySlow),
                            new SfSegmentItem()
                                .ImageSource(ApplicationTheme.IconSpeedSlow),
                            new SfSegmentItem()
                                .ImageSource(ApplicationTheme.IconSpeedNormal)
                    )
                        .Background(Colors.Transparent)
                        .ShowSeparator(true)
                        .SegmentCornerRadius(0)
                        .Stroke(ApplicationTheme.Gray300)
                        .SegmentWidth(40)
                        .SegmentHeight(44)
                        .Margin(0, 0, 12, 0)
                        .SelectionIndicatorSettings(
                            new Syncfusion.Maui.Toolkit.SegmentedControl.SelectionIndicatorSettings
                            {
                                SelectionIndicatorPlacement = Syncfusion.Maui.Toolkit.SegmentedControl.SelectionIndicatorPlacement.BottomBorder
                            }
                        )
                        .SelectedIndex(State.SelectedSpeedIndex)
                        .OnSelectionChanged((s, e) =>
                        {
                            State.SelectedSpeedIndex = e.NewIndex;
                            switch (e.NewIndex)
                            {
                                case 0:
                                    SetState(s => s.PlaybackSpeed = 0.6f);
                                    break;
                                case 1:
                                    SetState(s => s.PlaybackSpeed = 0.8f);
                                    break;
                                case 2:
                                    SetState(s => s.PlaybackSpeed = 1.0f);
                                    break;
                            }

                        }),
                        Button(State.SelectedVoiceDisplayName)
                            .ThemeKey("Secondary")
                            // .FontSize(14)
                            // .CornerRadius(15)
                            // .BackgroundColor(Theme.IsLightTheme ? Colors.LightGray : Colors.DimGray)
                            // .TextColor(Theme.IsLightTheme ? Colors.Black : Colors.White)
                            // .Padding(10, 5)
                            .VCenter()
                            // .HeightRequest(30)
                            .OnClicked(ShowVoiceSelection)

                )
                .Margin(0,0,10,0)
                .GridColumn(2).HEnd()

                
            )

            
        
        .GridRow(2);

    /// <summary>
    /// Shows the voice selection bottom sheet.
    /// </summary>
    private void ShowVoiceSelection()
    {
        SetState(s => s.IsVoiceSelectionVisible = true);
    }

    /// <summary>
    /// Creates a visual node for the loading overlay displayed during busy operations.
    /// </summary>
    /// <returns>A visual node representing the loading overlay.</returns>
    private VisualNode LoadingOverlay() =>
        Grid(
            Label("Thinking.....")
                .FontSize(64)
                .TextColor(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .Center()
        )
        .Background(Color.FromArgb("#80000000"))
        .GridRowSpan(3)
        .IsVisible(State.IsBusy);

    /// <summary>
    /// Loads sentences for shadowing practice using the selected vocabulary and skill.
    /// </summary>
    async void LoadSentences()
    {
        if (State.IsBusy)
            return;
            
        try
        {
            await StopAudio();
            
            SetState(s => 
            {
                s.IsBusy = true;
                s.Sentences.Clear();
                s.CurrentSentenceIndex = 0;
                s.CurrentAudioStream = null;
                s.PlaybackPosition = 0;
            });
            
            // Clear audio cache when loading new sentences
            _audioCache.Clear();
            
            var sentences = await _shadowingService.GenerateSentencesAsync(
                Props.Vocabulary.ID, 
                10,
                Props.Skill?.ID ?? 0);
                
            SetState(s => 
            {
                s.Sentences = sentences;
                s.IsBusy = false;
            });
            
            // Record this activity
            if (Props.Vocabulary != null && Props.Skill != null)
            {
                await _userActivityRepository.SaveAsync(new UserActivity
                {
                    Activity = Models.Activity.Shadowing.ToString(),
                    Input = sentences.Any() ? sentences[0].TargetLanguageText : string.Empty,
                    Accuracy = 100, // Default to 100 for shadowing as it's practice-based
                    Fluency = 100,  // Default to 100 for shadowing as it's practice-based
                    // VocabularyID = Props.Vocabulary.ID,
                    // SkillID = Props.Skill?.ID ?? 0,
                    CreatedAt = DateTime.Now
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading sentences: {ex.Message}");
            SetState(s => s.IsBusy = false);
        }
    }

    /// <summary>
    /// Moves to the previous sentence in the list.
    /// </summary>
    async void PreviousSentence()
    {
        if (State.CurrentSentenceIndex > 0)
        {
            await StopAudio();
            SetState(s => 
            {
                s.CurrentSentenceIndex--;
                s.IsAudioPlaying = false;
                s.PlaybackPosition = 0;
                
                // Default time displays
                s.CurrentTimeDisplay = "00:00.000";
                s.DurationDisplay = "--:--.---";
                
                s.CurrentAudioStream = null; // Clear current audio stream
                s.WaveformData = null; // Clear waveform data to force regeneration
            });
            
            // Check if this sentence has cached audio info we can display right away
            var currentSentence = State.Sentences[State.CurrentSentenceIndex];
            if (_audioCache.TryGetValue(currentSentence.TargetLanguageText, out var cacheEntry))
            {
                // Restore cached duration display
                SetState(s => {
                    s.DurationDisplay = cacheEntry.DurationDisplay;
                    s.AudioDuration = cacheEntry.Duration;
                    s.WaveformData = cacheEntry.WaveformData;
                });
                
                Debug.WriteLine($"Restored cached time display: {cacheEntry.DurationDisplay}");
            }
        }
    }

    /// <summary>
    /// Moves to the next sentence in the list.
    /// </summary>
    async void NextSentence()
    {
        if (State.CurrentSentenceIndex < State.Sentences.Count - 1)
        {
            await StopAudio();
            SetState(s => 
            {
                s.CurrentSentenceIndex++;
                s.IsAudioPlaying = false;
                s.PlaybackPosition = 0;
                
                // Default time displays
                s.CurrentTimeDisplay = "00:00.000";
                s.DurationDisplay = "--:--.---";
                
                s.CurrentAudioStream = null; // Clear current audio stream
                s.WaveformData = null; // Clear waveform data to force regeneration
            });
            
            // Check if this sentence has cached audio info we can display right away
            var currentSentence = State.Sentences[State.CurrentSentenceIndex];
            if (_audioCache.TryGetValue(currentSentence.TargetLanguageText, out var cacheEntry))
            {
                // Restore cached duration display
                SetState(s => {
                    s.DurationDisplay = cacheEntry.DurationDisplay;
                    s.AudioDuration = cacheEntry.Duration;
                    s.WaveformData = cacheEntry.WaveformData;
                });
                
                Debug.WriteLine($"Restored cached time display: {cacheEntry.DurationDisplay}");
            }
        }
    }

    /// <summary>
    /// Toggles audio playback between playing and paused states.
    /// </summary>
    async void ToggleAudioPlayback()
    {
        if (State.IsAudioPlaying)
        {
            // Pause instead of stopping if we're currently playing
            await PauseAudio();
        }
        else if (State.IsPaused)
        {
            // Resume if we're paused
            await ResumeAudio();
        }
        else
        {
            // Start fresh playback if neither playing nor paused
            await PlayAudio();
        }
    }
    
    /// <summary>
    /// Pauses the currently playing audio.
    /// </summary>
    async Task PauseAudio()
    {
        if (_audioPlayer != null && _audioPlayer.IsPlaying)
        {
            _playbackTimer?.Stop();
            _audioPlayer.Pause();
            
            SetState(s => 
            {
                s.IsAudioPlaying = false;
                s.IsPaused = true;
            });
            
            Debug.WriteLine("Audio playback paused");
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Resumes playback from the paused position.
    /// </summary>
    async Task ResumeAudio()
    {
        if (_audioPlayer != null && !_audioPlayer.IsPlaying)
        {
            _audioPlayer.Play();
            
            // Restart the playback timer
            StartPlaybackTimer();
            
            SetState(s => 
            {
                s.IsAudioPlaying = true;
                s.IsPaused = false;
            });
            
            Debug.WriteLine("Audio playback resumed");
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets the selected index for the playback speed control based on the current PlayMode
    /// </summary>
    /// <returns>The index of the currently selected playback speed</returns>
    private int GetPlaybackSpeedIndex()
    {
        return State.PlayMode switch
        {
            ShadowingPlayMode.VerySlow => 0, // Very Slow (0.5f)
            ShadowingPlayMode.Slow => 1,     // Slow (0.75f)
            ShadowingPlayMode.Normal => 2,   // Normal (1.0f)
            _ => 2                           // Default to Normal
        };
    }

    /// <summary>
    /// Plays the audio for the current sentence.
    /// </summary>
    async Task PlayAudio()
    {
        if (State.Sentences.Count == 0 || 
            State.CurrentSentenceIndex >= State.Sentences.Count)
            return;
            
        try
        {
            SetState(s => s.IsBuffering = true);
            
            var sentence = State.Sentences[State.CurrentSentenceIndex];
            string sentenceText = sentence.TargetLanguageText;
            
            // Create a cache key that includes the sentence and the selected voice
            string cacheKey = $"{sentenceText}_{State.SelectedVoiceId}";
            
            Stream audioStream = null;
            AudioCacheEntry cacheEntry = null;
            
            // Check if audio is already in cache
            if (_audioCache.TryGetValue(cacheKey, out cacheEntry))
            {
                // Use cached audio stream
                cacheEntry.AudioStream.Position = 0; // Reset position to beginning
                audioStream = cacheEntry.AudioStream;
                
                // Immediately display cached duration info
                SetState(s => {
                    s.AudioDuration = cacheEntry.Duration;
                    s.DurationDisplay = cacheEntry.DurationDisplay;
                });
                
                Debug.WriteLine($"Using cached audio for: {cacheKey}");
            }
            else
            {
                // Generate new audio stream if not in cache, passing the selected voice ID and speed
                audioStream = await _shadowingService.GenerateAudioAsync(
                    sentenceText, 
                    State.SelectedVoiceId,  // Use the selected voice ID
                    State.PlaybackSpeed); 
                
                if (audioStream == null)
                {
                    SetState(s => s.IsBuffering = false);
                    return;
                }
            }
            
            // Create the audio player
            _audioPlayer = AudioManager.Current.CreatePlayer(audioStream);
            _audioPlayer.Speed = State.PlaybackSpeed; // Set the playback speed based on the selected speed
            
            // Capture audio duration for waveform scaling
            double audioDuration = _audioPlayer.Duration;
            string durationFormatted = FormatTimeDisplay(audioDuration);
            Debug.WriteLine($"Audio duration: {audioDuration} seconds");
            
            // If this is a new audio stream, create a new cache entry
            if (cacheEntry == null)
            {
                cacheEntry = new AudioCacheEntry
                {
                    AudioStream = audioStream,
                    Duration = audioDuration,
                    DurationDisplay = durationFormatted
                };
                _audioCache[cacheKey] = cacheEntry;
            }
            
            // Analyze the audio stream to extract waveform data if not already cached
            if (cacheEntry.WaveformData == null)
            {
                try
                {
                    // Clone the stream so we don't mess with the position of the original
                    MemoryStream memStream = new MemoryStream();
                    audioStream.Position = 0;
                    await audioStream.CopyToAsync(memStream);
                    memStream.Position = 0;
                    
                    // Extract waveform data from the audio stream
                    var waveformData = await _audioAnalyzer.AnalyzeAudioStreamAsync(memStream);
                    
                    // Store waveform in cache
                    cacheEntry.WaveformData = waveformData;
                    
                    // Reset the original stream position
                    audioStream.Position = 0;
                    
                    Debug.WriteLine($"Extracted waveform data: {waveformData.Length} samples with duration {audioDuration}s");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error analyzing audio waveform: {ex.Message}");
                    // Continue even if waveform analysis fails
                }
            }
            
            // Update state with audio info
            SetState(s => 
            {
                s.WaveformData = cacheEntry.WaveformData;
                s.CurrentAudioStream = audioStream;
                s.AudioDuration = audioDuration;
                s.DurationDisplay = durationFormatted;
                s.CurrentTimeDisplay = "00:00.000"; // Reset current time display
            });
            
            // Setup event handler and start playback
            _audioPlayer.PlaybackEnded += OnAudioPlaybackEnded;
            _audioPlayer.Play();
            
            // Start playback tracking timer
            StartPlaybackTimer();
            
            // Update state
            SetState(s => 
            {
                s.IsAudioPlaying = true;
                s.IsBuffering = false;
                s.PlaybackPosition = 0;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error playing audio: {ex.Message}");
            SetState(s => 
            {
                s.IsAudioPlaying = false;
                s.IsBuffering = false;
            });
        }
    }
    
    /// <summary>
    /// Starts the timer to track audio playback position.
    /// </summary>
    private void StartPlaybackTimer()
    {
        // Stop any existing timer
        _playbackTimer?.Stop();
        
        // Create a new timer that ticks 10 times per second
        _playbackTimer = Application.Current.Dispatcher.CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(100);
        _playbackTimer.Tick += (s, e) => UpdatePlaybackPosition();
        _playbackTimer.Start();
    }
    
    /// <summary>
    /// Updates the playback position for the waveform display.
    /// </summary>
    private void UpdatePlaybackPosition()
    {
        if (_audioPlayer == null)
            return;
        
        // Update the position if we know the duration
        if (_audioPlayer.Duration > 0)
        {
            // Calculate the position as a float between 0-1
            float position = (float)(_audioPlayer.CurrentPosition / _audioPlayer.Duration);
            
            // Format time displays
            string currentTime = FormatTimeDisplay(_audioPlayer.CurrentPosition);
            string totalDuration = FormatTimeDisplay(_audioPlayer.Duration);
            
            SetState(s => {
                s.PlaybackPosition = position;
                s.CurrentTimeDisplay = currentTime;
                s.DurationDisplay = totalDuration;
            });
            
            // Auto-scroll the waveform to keep the playhead visible
            EnsurePlayheadIsVisible(position);
        }
    }
    
    /// <summary>
    /// Ensures the playhead is visible by scrolling the waveform view if necessary.
    /// </summary>
    private void EnsurePlayheadIsVisible(float position)
    {
        // Only scroll if we have a valid duration
        if (_audioPlayer == null || _audioPlayer.Duration <= 0)
            return;
            
            
        // Calculate the total width of the waveform based on duration and pixels per second
        double pixelsPerSecond = 100; // Should match the value used in WaveformView
        double totalWidth = _audioPlayer.Duration * pixelsPerSecond;
        
        // Calculate the target X position of the playhead
        double playheadX = totalWidth * position;
        
        // Get the current visible region
        double scrollViewWidth = _waveformScrollView.Width;
        double currentScrollX = _waveformScrollView.ScrollX;
        
        // Define margins to keep the playhead within visible area (% of view width)
        double leadingMargin = 0.1;  // 10% from the left edge
        double trailingMargin = 0.8; // 80% from the left edge (20% from right edge)
        
        // Calculate visible range
        double visibleStart = currentScrollX;
        double visibleEnd = currentScrollX + scrollViewWidth;
        
        // Check if playhead is outside the desired visible region
        if (playheadX < visibleStart + (scrollViewWidth * leadingMargin) ||
            playheadX > visibleStart + (scrollViewWidth * trailingMargin))
        {
            // Calculate new scroll position to center playhead with slight lead
            double newScrollX = playheadX - (scrollViewWidth * 0.3); // 30% from the left
            
            // Ensure we don't scroll past the edges
            newScrollX = Math.Max(0, Math.Min(newScrollX, totalWidth - scrollViewWidth));
            
            // Scroll to the new position
            _waveformScrollView.ScrollToAsync(newScrollX, 0, false);
        }
    }
    
    
    
    /// <summary>
    /// Formats a time value in seconds to a mm:ss.ms display format.
    /// </summary>
    /// <param name="timeInSeconds">The time value in seconds.</param>
    /// <returns>A formatted time string in mm:ss.ms format.</returns>
    private string FormatTimeDisplay(double timeInSeconds)
    {
        if (timeInSeconds < 0)
            return "--:--";
            
        TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
        return $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    /// <summary>
    /// Event handler for when audio playback ends.
    /// </summary>
    private void OnAudioPlaybackEnded(object sender, EventArgs e)
    {
        _playbackTimer?.Stop();
        
        SetState(s => {
            s.IsAudioPlaying = false;
            s.IsPaused = true; // Reset the pause state
            s.PlaybackPosition = 1.0f; // Show as fully played
            s.CurrentTimeDisplay = s.DurationDisplay; // Set current time to total duration
        });
        
        RewindAudioStream();
    }

    /// <summary>
    /// Rewinds the audio stream to the beginning for replay.
    /// </summary>
    private void RewindAudioStream()
    {
        if (State.CurrentAudioStream != null && State.CurrentAudioStream.CanSeek)
        {
            State.CurrentAudioStream.Position = 0;
        }
    }

    /// <summary>
    /// Stops the currently playing audio.
    /// </summary>
    async Task StopAudio()
    {
        _playbackTimer?.Stop();
        
        if (_audioPlayer != null)
        {
            if (_audioPlayer.IsPlaying)
            {
                _audioPlayer.Stop();
            }
            
            _audioPlayer.PlaybackEnded -= OnAudioPlaybackEnded;
            _audioPlayer.Dispose();
            _audioPlayer = null;
        }
        
        SetState(s => 
        {
            s.IsAudioPlaying = false;
            s.IsPaused = false; // Reset the pause state as well
            s.PlaybackPosition = 0; // Reset position to beginning
        });
        
        RewindAudioStream();
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Performs cleanup when the component is unmounted.
    /// </summary>
    protected override async void OnWillUnmount()
    {
        // Stop and dispose the playback timer
        _playbackTimer?.Stop();
        _playbackTimer = null;

        // Clean up audio resources when navigating away from the page
        await StopAudio();

        // Clean up cached streams
        foreach (var entry in _audioCache.Values)
        {
            entry.AudioStream?.Dispose();
        }
        _audioCache.Clear();

        base.OnWillUnmount();
    }
}