using MauiReactor.Shapes;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Controls;
using Plugin.Maui.Audio;
using System.Collections.ObjectModel;

namespace SentenceStudio.Pages.Shadowing;

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
    /// Dictionary to cache audio streams by sentence text for reuse.
    /// </summary>
    private readonly Dictionary<string, Stream> _audioStreamCache = new();

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
        Label()
        .Text("Visualization")
        .HCenter(),
        Border(
            State.WaveformData != null 
                // If we have waveform data, create a component with the data
                ? new WaveformWithData(
                    Theme.IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f),
                    Theme.IsLightTheme ? Colors.Orange : Colors.OrangeRed,
                    State.PlaybackPosition,
                    0.8f,
                    State.WaveformData,
                    80)
                // Otherwise create the standard component without data
                : new Waveform()
                    .WaveColor(Theme.IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f))
                    .PlayedColor(Theme.IsLightTheme ? Colors.Orange : Colors.OrangeRed)
                    .Amplitude(0.8f)
                    .PlaybackPosition(State.PlaybackPosition)
                    .AutoGenerateWaveform(true)
                    .SampleCount(150)
                    .Height(80)
                    .AudioId(State.CurrentSentenceIndex.ToString())
        )        
            .StrokeShape(new RoundRectangle().CornerRadius(8))
            .StrokeThickness(1)
            .Stroke(Theme.IsLightTheme ? Colors.LightGray : Colors.DimGray)
            .HeightRequest(100)
            .IsVisible(true)
        ).Margin(20, 0)
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
            _audioStreamCache.Clear();
            
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
                s.WaveformData = null; // Clear waveform data so it regenerates for new sentence
                s.CurrentAudioStream = null; // Clear current audio stream
            });
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
                s.WaveformData = null; // Clear waveform data so it regenerates for new sentence
                s.CurrentAudioStream = null; // Clear current audio stream
            });
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
            
            // Check if audio is already in cache
            if (_audioStreamCache.TryGetValue(sentenceText, out Stream cachedStream))
            {
                // Use cached audio stream
                cachedStream.Position = 0; // Reset position to beginning
                audioStream = cachedStream;
                Debug.WriteLine($"Using cached audio for: {sentenceText}");
            }
            else
            {
                // Generate new audio stream if not in cache
                audioStream = await _shadowingService.GenerateAudioAsync(sentenceText);
                if (audioStream != null)
                {
                    // Add to cache for future use
                    _audioStreamCache[sentenceText] = audioStream;
                    Debug.WriteLine($"Generated and cached new audio for: {sentenceText}");
                }
                else
                {
                    SetState(s => s.IsBuffering = false);
                    return;
                }
            }
            
            // Analyze the audio stream to extract waveform data
            try
            {
                // Clone the stream so we don't mess with the position of the original
                MemoryStream memStream = new MemoryStream();
                audioStream.Position = 0;
                await audioStream.CopyToAsync(memStream);
                memStream.Position = 0;
                
                // Extract waveform data from the audio stream
                var waveformData = await _audioAnalyzer.AnalyzeAudioStreamAsync(memStream);
                
                // Reset the original stream position
                audioStream.Position = 0;
                
                // Update state with the waveform data
                SetState(s => 
                {
                    s.WaveformData = waveformData;
                    s.CurrentAudioStream = audioStream;
                });
                
                Debug.WriteLine($"Extracted waveform data: {waveformData.Length} samples");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error analyzing audio waveform: {ex.Message}");
                // Continue even if waveform analysis fails
                SetState(s => s.CurrentAudioStream = audioStream);
            }
            
            // Play the audio using AudioManager.Current
            _audioPlayer = AudioManager.Current.CreatePlayer(State.CurrentAudioStream);
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
        if (_audioPlayer == null || !_audioPlayer.IsPlaying)
            return;
        
        // Update the position if we know the duration
        if (_audioPlayer.Duration > 0)
        {
            float position = (float)(_audioPlayer.CurrentPosition / _audioPlayer.Duration);
            SetState(s => s.PlaybackPosition = position);
        }
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
        foreach (var stream in _audioStreamCache.Values)
        {
            stream?.Dispose();
        }
        _audioStreamCache.Clear();

        base.OnWillUnmount();
    }
}