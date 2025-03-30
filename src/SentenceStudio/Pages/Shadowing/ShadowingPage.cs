using MauiReactor.Shapes;
using SentenceStudio.Pages.Dashboard;
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
    
    private IAudioPlayer _audioPlayer;
    private LocalizationManager _localize => LocalizationManager.Instance;
    
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
            Grid(rows: "*, 80", columns: "*",
                SentenceDisplay(),
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
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                    .HCenter()
                    .TextColor(Colors.Gray)
                    .Margin(ApplicationTheme.Size160)
            )
            .Padding(ApplicationTheme.Size160)
        ).GridRow(0);

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
        ).GridRow(1);

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
        .GridRowSpan(2)
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
                // Don't clear the audio stream, it will be retrieved from cache if available
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
                // Don't clear the audio stream, it will be retrieved from cache if available
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
            
            // Check if audio is already in cache
            if (_audioStreamCache.TryGetValue(sentenceText, out Stream cachedStream))
            {
                // Use cached audio stream
                cachedStream.Position = 0; // Reset position to beginning
                SetState(s => s.CurrentAudioStream = cachedStream);
                Debug.WriteLine($"Using cached audio for: {sentenceText}");
            }
            else
            {
                // Generate new audio stream if not in cache
                var stream = await _shadowingService.GenerateAudioAsync(sentenceText);
                if (stream != null)
                {
                    // Add to cache for future use
                    _audioStreamCache[sentenceText] = stream;
                    SetState(s => s.CurrentAudioStream = stream);
                    Debug.WriteLine($"Generated and cached new audio for: {sentenceText}");
                }
                else
                {
                    SetState(s => s.IsBuffering = false);
                    return;
                }
            }
            
            // Play the audio using AudioManager.Current, just like HowDoYouSayPage
            _audioPlayer = AudioManager.Current.CreatePlayer(State.CurrentAudioStream);
            _audioPlayer.PlaybackEnded += OnAudioPlaybackEnded;
            _audioPlayer.Play();
            
            // Update state
            SetState(s => 
            {
                s.IsAudioPlaying = true;
                s.IsBuffering = false;
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
    /// Event handler for when audio playback ends.
    /// </summary>
    private void OnAudioPlaybackEnded(object sender, EventArgs e)
    {
        SetState(s => s.IsAudioPlaying = false);
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
    // protected override async void OnUnmount()
    // {
    //     // Clean up audio resources when navigating away from the page
    //     await StopAudio();
        
    //     // Clean up cached streams
    //     foreach (var stream in _audioStreamCache.Values)
    //     {
    //         stream?.Dispose();
    //     }
    //     _audioStreamCache.Clear();
        
    //     base.OnUnmount();
    // }
}