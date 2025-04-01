using MauiReactor.Shapes;
using SentenceStudio.Pages.Dashboard;
using Plugin.Maui.Audio;
using MauiReactor.Compatibility;

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

    private IAudioPlayer _audioPlayer;
    private LocalizationManager _localize => LocalizationManager.Instance;
    private IDispatcherTimer _playbackTimer;

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
                LoadingOverlay()
            )
            .RowSpacing(12)
        ).OnAppearing(LoadSentences);
    }

    /// <summary>
    /// Creates a visual node for the sentence display area.
    /// </summary>
    /// <returns>A visual node for displaying the current sentence.</returns>
    private VisualNode SentenceDisplay() =>
        ScrollView(
            VStack(spacing: ApplicationTheme.Size160,
                Label(State.CurrentSentenceText)
                    .Style((Style)Application.Current.Resources["Title2"])
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 24 : 32)
                    .HCenter()
                    .Margin(ApplicationTheme.Size160),
                    
                Label(State.CurrentSentenceTranslation)
                    .Style((Style)Application.Current.Resources["Title3"])
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                    .HCenter()
                    .TextColor(Colors.Gray)
                    .Margin(ApplicationTheme.Size160),

                Label(State.CurrentSentencePronunciationNotes)
                    .Style((Style)Application.Current.Resources["Title3"])
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 16 : 20)
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
                .Text("Visualization")
                .HStart()
                .GridColumn(0),
            
            Label()
                .Text($"{State.CurrentTimeDisplay} / {State.DurationDisplay}")
                .FontAttributes(FontAttributes.Bold)
                .FontSize(14)
                .HEnd()
                .GridColumn(2)
        ),
        Border(
            HScrollView(

                Grid("100", "*", // Row definitions: 30px for TimeScale, 70px for Waveform
                                   // Time scale showing 1/10 second increments, half seconds, and full seconds
                    new TimeScale()
                        .TickColor(Theme.IsLightTheme ? Colors.Gray : Colors.Silver)
                        .TextColor(Theme.IsLightTheme ? Colors.DarkSlateGray : Colors.Silver)
                        .AudioDuration(State.AudioDuration)
                        .PixelsPerSecond(120),

                    // Use the Waveform component with proper AudioId for caching
                    new Waveform()
                        .WaveColor(Theme.IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f))
                        .PlayedColor(Theme.IsLightTheme ? Colors.Orange : Colors.OrangeRed)
                        .Amplitude(0.8f)
                        .PlaybackPosition(State.PlaybackPosition)
                        .AutoGenerateWaveform(true) // Enable random waveform when no cached data exists
                        .SampleCount(400)
                        .AudioId(State.CurrentSentenceIndex.ToString()) // Use the current index as the audio ID
                        .AudioDuration(State.AudioDuration)
                        .PixelsPerSecond(120)
                )
                // .WidthRequest(State.AudioDuration > 0 ? Math.Max((float)(State.AudioDuration * 120), 300) : 300)

            )
            .Padding(0)
            .VerticalScrollBarVisibility(ScrollBarVisibility.Never)
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

    /// <summary>
    /// Creates a visual node for the navigation footer containing controls for audio and navigation.
    /// </summary>
    /// <returns>A visual node representing the navigation footer.</returns>
    private VisualNode NavigationFooter() =>
        Grid(rows: "*", columns: "60,1,*,1,60,1,60",
            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(SegoeFluentIcons.Previous.ToImageSource())
                .GridRow(0).GridColumn(0)
                .OnClicked(PreviousSentence),

            Button()
                .ImageSource(State.IsAudioPlaying ?
                    SegoeFluentIcons.Pause.ToImageSource() :
                    SegoeFluentIcons.Play.ToImageSource())
                .Background(Colors.Transparent)
                .GridRow(0).GridColumn(2)
                .HCenter()
                .OnClicked(ToggleAudioPlayback),

            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(SegoeFluentIcons.Next.ToImageSource())
                .GridRow(0).GridColumn(4)
                .OnClicked(NextSentence),

            BoxView()
                .Color(Colors.Black)
                .HeightRequest(1)
                .GridColumnSpan(7)
                .VStart(),

            BoxView()
                .Color(Colors.Black)
                .WidthRequest(1)
                .GridRow(0).GridColumn(1),

            BoxView()
                .Color(Colors.Black)
                .WidthRequest(1)
                .GridRow(0).GridColumn(3),

            BoxView()
                .Color(Colors.Black)
                .WidthRequest(1)
                .GridRow(0).GridColumn(5)
        ).GridRow(2);

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
            await StopAudio();
        }
        else
        {
            await PlayAudio();
        }
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
            
            Stream audioStream = null;
            AudioCacheEntry cacheEntry = null;
            
            // Check if audio is already in cache
            if (_audioCache.TryGetValue(sentenceText, out cacheEntry))
            {
                // Use cached audio stream
                cacheEntry.AudioStream.Position = 0; // Reset position to beginning
                audioStream = cacheEntry.AudioStream;
                
                // Immediately display cached duration info
                SetState(s => {
                    s.AudioDuration = cacheEntry.Duration;
                    s.DurationDisplay = cacheEntry.DurationDisplay;
                });
                
                Debug.WriteLine($"Using cached audio for: {sentenceText}");
            }
            else
            {
                // Generate new audio stream if not in cache
                audioStream = await _shadowingService.GenerateAudioAsync(sentenceText);
                if (audioStream == null)
                {
                    SetState(s => s.IsBuffering = false);
                    return;
                }
                
                Debug.WriteLine($"Generated new audio for: {sentenceText}");
            }
            
            // Create the audio player
            _audioPlayer = AudioManager.Current.CreatePlayer(audioStream);
            
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
                _audioCache[sentenceText] = cacheEntry;
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
            s.PlaybackPosition = 1.0f; // Show as fully played
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
        
        SetState(s => s.IsAudioPlaying = false);
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