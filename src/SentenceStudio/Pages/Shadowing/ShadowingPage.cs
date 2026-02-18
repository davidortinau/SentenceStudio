using MauiReactor.Shapes;
using SentenceStudio.Pages.Dashboard;
using Plugin.Maui.Audio;
using MauiReactor.Compatibility;
using SentenceStudio.Pages.Controls;
using System.Text.RegularExpressions;
using CommunityToolkit.Maui.Storage;

using Microsoft.Maui.Dispatching;
using SentenceStudio.Components;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Speech;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Shadowing;

/// <summary>
/// Shadowing Activity Page - Audio Repetition and Pronunciation Practice
/// 
/// USAGE CONTEXTS (CRITICAL - This page serves multiple purposes!):
/// 
/// 1. FROM DAILY PLAN (Structured Learning):
///    - Entry: Dashboard → Today's Plan → Click "Shadowing" activity
///    - Props.FromTodaysPlan = true, Props.PlanItemId = set
///    - Content: Pre-selected resource by DeterministicPlanBuilder
///    - Timer: ActivityTimerBar visible in Shell.TitleView
///    - Completion: Updates plan progress, returns to dashboard
///    - User Expectation: "I'm completing my daily shadowing practice"
/// 
/// 2. MANUAL RESOURCE SELECTION (Free Practice):
///    - Entry: Resources → Browse → Select resource → Start Shadowing
///    - Props.FromTodaysPlan = false, Props.PlanItemId = null
///    - Content: User-selected resource
///    - Timer: No timer displayed
///    - Completion: Shows summary, offers continue/return options
///    - User Expectation: "I'm practicing pronunciation with this specific resource"
/// 
/// 3. FUTURE CONTEXTS (Update this section as new uses are added!):
///    - Recording Mode: Record and compare pronunciation
///    - Speed Variation: Practice at different speeds
///    - Pronunciation Scoring: AI-based pronunciation feedback
/// 
/// IMPORTANT: When modifying this page, ensure changes work correctly for ALL contexts!
/// Test both daily plan flow AND manual resource selection before committing.
/// </summary>

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
    [Inject] IFileSaver _fileSaver;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    [Inject] IVoiceDiscoveryService _voiceDiscoveryService;
    [Inject] SpeechVoicePreferences _speechVoicePreferences;
    [Inject] ILogger<ShadowingPage> _logger;
    [Inject] NativeThemeService _themeService;

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
        return ContentPage(pageRef => _pageRef = pageRef,
            // ToolbarItem($"{_localize["Refresh"]}").OnClicked(LoadSentences),
            Grid(rows: "*, Auto, 80", columns: "*",
                Props?.FromTodaysPlan == true ? RenderTitleView() : null,
                SentenceDisplay(),
                WaveformDisplay(),
                NavigationFooter(),
                LoadingOverlay(),
                RenderExportBottomSheet(),
                RenderNarrowScreenMenu()
            )
            .RowSpacing(16)
        )
        .Title($"{_localize["Shadowing"]}")
        .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new ActivityTimerBar() : null)
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(OnPageAppearing)
        .OnSizeChanged((size) =>
        {
            double width = size.Width;
            bool isNarrow = width < 500; // Threshold for narrow screens
            // Only update state if the layout mode changes
            if (State.IsNarrowScreen != isNarrow)
            {
                SetState(s => s.IsNarrowScreen = isNarrow);
            }
        });
    }

    private MauiControls.ContentPage? _pageRef;
    private MauiControls.Grid? _mainGridRef;

    private VisualNode RenderTitleView()
    {
        return Grid(mainGridRef => _mainGridRef = mainGridRef, new ActivityTimerBar()).HEnd().VCenter();
    }

    private void TrySetShellTitleView()
    {
        if (_pageRef != null && _mainGridRef != null)
        {
            _pageRef.Dispatcher.Dispatch(() =>
            {
                MauiControls.Shell.SetTitleView(_pageRef, Props?.FromTodaysPlan == true ? _mainGridRef : null);
            });
        }
    }

    /// <summary>
    /// Initializes the page and loads content when appearing
    /// </summary>
    private async Task OnPageAppearing()
    {
        // Start activity timer if launched from Today's Plan
        if (Props?.FromTodaysPlan == true)
        {
            _timerService.StartSession("Shadowing", Props.PlanItemId);
        }

        // Get target language from resource
        var targetLanguage = Props.Resource?.Language ?? "Korean";
        SetState(s => s.TargetLanguage = targetLanguage);

        // Load voices for the target language
        await LoadVoicesForLanguageAsync(targetLanguage);

        // Load sentences if needed
        await Task.Delay(100); // Small delay to ensure UI is ready
        if (State.Sentences.Count == 0)
        {
            LoadSentences();
        }

        TrySetShellTitleView();
    }

    /// <summary>
    /// Loads available voices for the specified language from VoiceDiscoveryService.
    /// </summary>
    private async Task LoadVoicesForLanguageAsync(string language)
    {
        SetState(s => s.IsLoadingVoices = true);

        try
        {
            var voices = await _voiceDiscoveryService.GetVoicesForLanguageAsync(language);
            
            // Get the saved voice preference for this language
            var savedVoiceId = _speechVoicePreferences.GetVoiceForLanguage(language);
            
            SetState(s =>
            {
                s.AvailableVoices = voices;
                s.IsLoadingVoices = false;
                
                // Set selected voice: use saved preference if it exists in the list, otherwise use first available
                if (!string.IsNullOrEmpty(savedVoiceId) && voices.Any(v => v.VoiceId == savedVoiceId))
                {
                    s.SelectedVoiceId = savedVoiceId;
                }
                else if (voices.Any())
                {
                    s.SelectedVoiceId = voices.First().VoiceId;
                }
            });
            
            _logger.LogInformation("🎙️ Loaded {Count} voices for {Language}", voices.Count, language);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load voices for {Language}", language);
            SetState(s => s.IsLoadingVoices = false);
        }
    }

    /// <summary>
    /// Handles voice selection and saves to per-language preferences.
    /// </summary>
    private void SelectVoice(string voiceId)
    {
        // Save to per-language preference
        _speechVoicePreferences.SetVoiceForLanguage(State.TargetLanguage, voiceId);
        
        // Update the selected voice
        SetState(s =>
        {
            s.SelectedVoiceId = voiceId;

            // Reset audio cache when voice changes
            _audioCache.Clear();
        });

        _logger.LogInformation("🎙️ Selected voice {VoiceId} for {Language}", voiceId, State.TargetLanguage);
    }

    /// <summary>
    /// Creates a visual node for the sentence display area.
    /// Pure shadowing: Shows only target language text (no translation).
    /// </summary>
    /// <returns>A visual node for displaying the current sentence.</returns>
    private VisualNode SentenceDisplay() =>
        ScrollView(
            VStack(spacing: 16,
                // Only show target language text (Korean/Spanish/etc.)
                Label(State.CurrentSentenceText)
                    .H2()
                    .HCenter()
                    .Margin(16),

                // Show translation if toggled on
                State.ShowTranslation && !string.IsNullOrEmpty(State.CurrentSentenceTranslation)
                    ? Label(State.CurrentSentenceTranslation)
                        .FontSize(16)
                        .HCenter()
                        .Muted()
                    : null,

                // Show pronunciation notes if available (from AI-generated sentences)
                !string.IsNullOrWhiteSpace(State.CurrentSentencePronunciationNotes)
                    ? Label(State.CurrentSentencePronunciationNotes)
                        .H5()
                        .HCenter()
                        .Muted()
                        .Margin(16)
                    : null,

                // Show/Hide translation toggle
                Button(State.ShowTranslation
                        ? $"{_localize["HideTranslation"]}"
                        : $"{_localize["ShowTranslation"]}")
                    .Background(Colors.Transparent)
                    .TextColor(BootstrapTheme.Current.GetOnBackground())
                    .FontSize(14)
                    .HCenter()
                    .OnClicked(() => SetState(s => s.ShowTranslation = !s.ShowTranslation))
            )
            .Padding(16)
        ).GridRow(0);

    /// <summary>
    /// Creates a visual node for the waveform visualization display.
    /// </summary>
    /// <returns>A visual node for displaying the audio waveform.</returns>
    private VisualNode WaveformDisplay() =>
    VStack(
        Grid("Auto", "Auto,*,Auto",
            Label()
                .Text($"{_localize["AudioTime"]}: {State.CurrentTimeDisplay} / {State.DurationDisplay}")
                .FontAttributes(FontAttributes.Bold)
                .FontSize(14)
                .HStart()
                .GridColumn(2)
        ),
        Border(
            HScrollView(hscroll => _waveformScrollView = hscroll,
                Grid("100", "*",
                    new WaveformView()
                        .WaveColor(BootstrapTheme.Current.GetOutline())
                        .PlayedColor(BootstrapTheme.Current.Primary)
                        .Amplitude(Constants.Amplitude)
                        .PlaybackPosition(State.PlaybackPosition)
                        .AudioId($"{State.CurrentSentenceIndex}_{State.CurrentSentenceText?.GetHashCode() ?? 0}")
                        .AudioDuration(State.AudioDuration)
                        .ShowTimeScale(true)
                        .WaveformData(State.WaveformData)
                        .OnInteractionStarted(OnWaveformInteractionStarted)
                        .OnPositionSelected(OnWaveformPositionSelected)
                )
            )
            .Padding(0)
            .VerticalScrollBarVisibility(ScrollBarVisibility.Never)
            .HorizontalScrollBarVisibility(ScrollBarVisibility.Never)
            .Orientation(ScrollOrientation.Horizontal)
        )
            .StrokeShape(new RoundRectangle().CornerRadius(8))
            .StrokeThickness(1)
            .Stroke(BootstrapTheme.Current.GetOutline())
            .HeightRequest(100)
            .Padding(16, 0)
            .IsVisible(true)
        )
            .Spacing(16)
            .Margin(24, 0)
            .GridRow(1);

    private void OnWaveformInteractionStarted()
    {
        PauseAudio();
    }

    /// <summary>
    /// Handles when the user selects a position on the waveform.
    /// </summary>
    /// <param name="normalizedPosition">The position as a value from 0 to 1.</param>
    private void OnWaveformPositionSelected(float normalizedPosition)
    {
        _logger.LogDebug("ShadowingPage: Waveform position selected: {Position:F2}", normalizedPosition);

        if (State.IsAudioPlaying)
        {
            // If audio is already playing, pause it first
            PauseAudio().ContinueWith(async t =>
            {
                // Then seek to the new position and resume
                await SeekAndResumeAudio(normalizedPosition);
            }, TaskScheduler.FromCurrentSynchronizationContext());
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

        _logger.LogDebug("ShadowingPage: Seeked to position {Position:F2} and resumed playback", normalizedPosition);
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

        _logger.LogDebug("ShadowingPage: Audio seeked to position {Position:F2}", normalizedPosition);
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

        _logger.LogDebug("ShadowingPage: Started playback from position {Position:F2}", normalizedPosition);
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
                    .Source(BootstrapIcons.Create(BootstrapIcons.SkipStartFill, BootstrapTheme.Current.GetOnBackground(), 24))
                    .GridRow(0).GridColumn(0)
                    .OnClicked(PreviousSentence),

                ImageButton()
                    .Source(State.IsAudioPlaying
                        ? BootstrapIcons.Create(BootstrapIcons.PauseFill, BootstrapTheme.Current.GetOnBackground(), 24)
                        : BootstrapIcons.Create(BootstrapIcons.PlayFill, BootstrapTheme.Current.GetOnBackground(), 24))
                    .Aspect(Aspect.Center)
                    .Background(Colors.Transparent)
                    .GridRow(0).GridColumn(2)
                    .HCenter()
                    .OnClicked(ToggleAudioPlayback),

                ImageButton()
                    .Background(Colors.Transparent)
                    .Aspect(Aspect.Center)
                    .Source(BootstrapIcons.Create(BootstrapIcons.SkipEndFill, BootstrapTheme.Current.GetOnBackground(), 24))
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



                // Different layout based on screen width
                State.IsNarrowScreen ?
                // Narrow layout - just show a menu button that opens bottom sheet with all controls
                Button()
                    .ImageSource(BootstrapIcons.Create(BootstrapIcons.ThreeDotsVertical, BootstrapTheme.Current.GetOnBackground(), 16))
                    .Background(new SolidColorBrush(Colors.Transparent))
                    .TextColor(BootstrapTheme.Current.GetOnBackground())
                    .BorderColor(BootstrapTheme.Current.GetOutline())
                    .BorderWidth(1)
                    .HeightRequest(35)
                    .WidthRequest(35)
                    .Padding(0)
                    .GridColumn(2)
                    .HEnd()
                    .Margin(0, 0, 8, 0)
                    .OnClicked(ShowNarrowScreenMenu)
                :
                // Wide layout - show all controls
                HStack(
                    Button()
                        .ImageSource(BootstrapIcons.Create(BootstrapIcons.Download, BootstrapTheme.Current.GetOnBackground(), 16))
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .TextColor(BootstrapTheme.Current.GetOnBackground())
                        .BorderColor(BootstrapTheme.Current.GetOutline())
                        .BorderWidth(1)
                        .HeightRequest(35)
                        .WidthRequest(35)
                        .Padding(0)
                        .Margin(0, 0, 16, 0)
                        .OnClicked(SaveAudioAsMp3),

                    HStack(spacing: 0,
                        Button("0.6x")
                            .Background(new SolidColorBrush(State.SelectedSpeedIndex == 0 ? BootstrapTheme.Current.Primary : Colors.Transparent))
                            .TextColor(State.SelectedSpeedIndex == 0 ? Colors.White : BootstrapTheme.Current.GetOnBackground())
                            .BorderColor(BootstrapTheme.Current.GetOutline())
                            .BorderWidth(1)
                            .HeightRequest(35)
                            .Padding(8, 0)
                            .OnClicked(() =>
                            {
                                SetState(s => { s.SelectedSpeedIndex = 0; s.PlaybackSpeed = 0.6f; });
                            }),
                        Button("0.8x")
                            .Background(new SolidColorBrush(State.SelectedSpeedIndex == 1 ? BootstrapTheme.Current.Primary : Colors.Transparent))
                            .TextColor(State.SelectedSpeedIndex == 1 ? Colors.White : BootstrapTheme.Current.GetOnBackground())
                            .BorderColor(BootstrapTheme.Current.GetOutline())
                            .BorderWidth(1)
                            .HeightRequest(35)
                            .Padding(8, 0)
                            .OnClicked(() =>
                            {
                                SetState(s => { s.SelectedSpeedIndex = 1; s.PlaybackSpeed = 0.8f; });
                            }),
                        Button("1x")
                            .Background(new SolidColorBrush(State.SelectedSpeedIndex == 2 ? BootstrapTheme.Current.Primary : Colors.Transparent))
                            .TextColor(State.SelectedSpeedIndex == 2 ? Colors.White : BootstrapTheme.Current.GetOnBackground())
                            .BorderColor(BootstrapTheme.Current.GetOutline())
                            .BorderWidth(1)
                            .HeightRequest(35)
                            .Padding(8, 0)
                            .OnClicked(() =>
                            {
                                SetState(s => { s.SelectedSpeedIndex = 2; s.PlaybackSpeed = 1.0f; });
                            })
                    )
                    .Margin(0, 0, 16, 0),

                        Button(State.SelectedVoiceDisplayName)
                            .Background(new SolidColorBrush(Colors.Transparent))
                            .TextColor(BootstrapTheme.Current.GetOnBackground())
                            .BorderColor(BootstrapTheme.Current.GetOutline())
                            .BorderWidth(1)
                            .VCenter()
                            .OnClicked(ShowVoiceSelection)

                )
                .Margin(0, 0, 8, 0)
                .GridColumn(2).HEnd()


            )

                  .GridRow(2);

    /// <summary>
    /// Shows the narrow screen menu bottom sheet with additional controls.
    /// </summary>
    private void ShowNarrowScreenMenu()
    {
        SetState(s => s.IsNarrowScreenMenuVisible = true);
    }

    /// <summary>
    /// Shows the voice selection bottom sheet.
    /// </summary>
    private async void ShowVoiceSelection()
    {
        await VoiceSelectionPopup.ShowAsync(
            $"{State.TargetLanguage} Voices",
            State.AvailableVoices,
            State.SelectedVoiceId,
            SelectVoice
        );
    }

    /// <summary>
    /// Shows the export menu bottom sheet.
    /// </summary>
    private void ShowExportMenu()
    {
        SetState(s => s.IsExportMenuVisible = true);
    }

    /// <summary>
    /// Renders the bottom sheet menu for narrow screens.
    /// </summary>
    private VisualNode RenderNarrowScreenMenu() =>
        State.IsNarrowScreenMenuVisible ?
        Grid("*", "*",
            // Semi-transparent overlay
            BoxView()
                .Background(Color.FromArgb("#80000000"))
                .OnTapped(() => SetState(s => s.IsNarrowScreenMenuVisible = false)),
            Border(
                VStack(
                    Label("Shadowing Options")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(20)
                        .TextColor(BootstrapTheme.Current.GetOnBackground())
                        .HCenter()
                        .Margin(0, 0, 0, 24),

                    // Playback Speed Section
                    VStack(
                        Label("Playback Speed")
                            .FontAttributes(FontAttributes.Bold)
                            .FontSize(16)
                            .HStart(),

                        HStack(spacing: 0,
                            Button("0.6x")
                                .Background(new SolidColorBrush(State.SelectedSpeedIndex == 0 ? BootstrapTheme.Current.Primary : Colors.Transparent))
                                .TextColor(State.SelectedSpeedIndex == 0 ? Colors.White : BootstrapTheme.Current.GetOnBackground())
                                .BorderColor(BootstrapTheme.Current.GetOutline())
                                .BorderWidth(1)
                                .HeightRequest(44)
                                .Padding(12, 0)
                                .OnClicked(() =>
                                {
                                    SetState(s => { s.SelectedSpeedIndex = 0; s.PlaybackSpeed = 0.6f; });
                                }),
                            Button("0.8x")
                                .Background(new SolidColorBrush(State.SelectedSpeedIndex == 1 ? BootstrapTheme.Current.Primary : Colors.Transparent))
                                .TextColor(State.SelectedSpeedIndex == 1 ? Colors.White : BootstrapTheme.Current.GetOnBackground())
                                .BorderColor(BootstrapTheme.Current.GetOutline())
                                .BorderWidth(1)
                                .HeightRequest(44)
                                .Padding(12, 0)
                                .OnClicked(() =>
                                {
                                    SetState(s => { s.SelectedSpeedIndex = 1; s.PlaybackSpeed = 0.8f; });
                                }),
                            Button("1x")
                                .Background(new SolidColorBrush(State.SelectedSpeedIndex == 2 ? BootstrapTheme.Current.Primary : Colors.Transparent))
                                .TextColor(State.SelectedSpeedIndex == 2 ? Colors.White : BootstrapTheme.Current.GetOnBackground())
                                .BorderColor(BootstrapTheme.Current.GetOutline())
                                .BorderWidth(1)
                                .HeightRequest(44)
                                .Padding(12, 0)
                                .OnClicked(() =>
                                {
                                    SetState(s => { s.SelectedSpeedIndex = 2; s.PlaybackSpeed = 1.0f; });
                                })
                        )
                        .Margin(0, 4, 0, 0)
                    )
                    .Margin(0, 0, 0, 20),

                    // Voice Selection Section
                    VStack(
                        Label("Voice")
                            .FontAttributes(FontAttributes.Bold)
                            .FontSize(16)
                            .HStart(),

                        Button(State.SelectedVoiceDisplayName)
                            .Background(new SolidColorBrush(Colors.Transparent))
                            .TextColor(BootstrapTheme.Current.GetOnBackground())
                            .BorderColor(BootstrapTheme.Current.GetOutline())
                            .BorderWidth(1)
                            .OnClicked(() =>
                            {
                                SetState(s => s.IsNarrowScreenMenuVisible = false);
                                ShowVoiceSelection();
                            })
                            .Margin(0, 4, 0, 0)
                    )
                    .Margin(0, 0, 0, 20),

                    // Export Section
                    VStack(
                        Label("Export")
                            .FontAttributes(FontAttributes.Bold)
                            .FontSize(16)
                            .HStart(),

                        Button("Save as MP3")
                            .Background(new SolidColorBrush(Colors.Transparent))
                            .TextColor(BootstrapTheme.Current.GetOnBackground())
                            .BorderColor(BootstrapTheme.Current.GetOutline())
                            .BorderWidth(1)
                            .OnClicked(() =>
                            {
                                SetState(s => s.IsNarrowScreenMenuVisible = false);
                                SaveAudioAsMp3();
                            })
                            .Margin(0, 4, 0, 0)
                    )
                )
                .Padding(24)
            )
            .BackgroundColor(BootstrapTheme.Current.GetSurface())
            .Stroke(BootstrapTheme.Current.GetOutline())
            .StrokeThickness(1)
            .StrokeShape(new RoundRectangle().CornerRadius(12))
            .VEnd()
            .Margin(16)
        )
        .GridRowSpan(3)
        : null;

    /// <summary>
    /// Creates a visual node for the loading overlay displayed during busy operations.
    /// </summary>
    /// <returns>A visual node representing the loading overlay.</returns>
    private VisualNode LoadingOverlay() =>
        Grid(
            Label("Thinking.....")
                .FontSize(64)
                .TextColor(BootstrapTheme.Current.GetOnBackground())
                .Center()
        )
        .Background(Color.FromArgb("#80000000"))
        .GridRowSpan(3)
        .IsVisible(State.IsBusy);

    /// <summary>
    /// Renders the export options bottom sheet.
    /// </summary>
    private VisualNode RenderExportBottomSheet() =>
        State.IsExportMenuVisible ?
        Grid("*", "*",
            // Semi-transparent overlay
            BoxView()
                .Background(Color.FromArgb("#80000000"))
                .OnTapped(() => SetState(s => s.IsExportMenuVisible = false)),
            Border(
                VStack(
                    Label("Export Audio")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(20)
                        .TextColor(BootstrapTheme.Current.GetOnBackground())
                        .HCenter()
                        .Margin(0, 0, 0, 24),

                    State.IsSavingAudio ?
                    VStack(
                        ActivityIndicator()
                            .IsRunning(true)
                            .HCenter()
                            .HeightRequest(50)
                            .WidthRequest(50)
                            .Margin(0, 0, 0, 8),
                        Label(State.ExportProgressMessage)
                            .FontSize(16)
                            .HCenter()
                    ) :
                    VStack(
                        Button("Save as MP3")
                            .Background(new SolidColorBrush(BootstrapTheme.Current.Primary))
                            .TextColor(Colors.White)
                            .OnClicked(SaveAudioAsMp3)
                            .Margin(0, 0, 0, 8),

                        !string.IsNullOrEmpty(State.LastSavedFilePath) ?
                        VStack(
                            Label("Last Saved:")
                                .FontSize(14)
                                .Muted()
                                .HCenter(),
                            Label(State.LastSavedFilePath)
                                .FontSize(14)
                                .Muted()
                                .HCenter()
                        ) : null
                    )
                )
                .Padding(24)
                .HCenter()
            )
            .BackgroundColor(BootstrapTheme.Current.GetSurface())
            .Stroke(BootstrapTheme.Current.GetOutline())
            .StrokeThickness(1)
            .StrokeShape(new RoundRectangle().CornerRadius(12))
            .VEnd()
            .Margin(16)
        )
        .GridRowSpan(3)
        : null;

    /// <summary>
    /// Loads sentences for shadowing practice.
    /// Uses transcript if available, otherwise generates from vocabulary.
    /// </summary>
    async Task LoadSentences()
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

            var resourceId = Props.Resource?.Id ?? 0;

            // Use unified method that handles transcript vs generation
            var sentences = await _shadowingService.GetOrGenerateSentencesAsync(
                resourceId,
                count: 10,
                skillId: Props.Skill?.Id ?? 0);

            SetState(s =>
            {
                s.Sentences = sentences;
                s.IsBusy = false;
            });

            // Record this activity
            if (Props.Resource != null && Props.Skill != null)
            {
                await _userActivityRepository.SaveAsync(new UserActivity
                {
                    Activity = SentenceStudio.Shared.Models.Activity.Shadowing.ToString(),
                    Input = sentences.Any() ? sentences[0].TargetLanguageText : string.Empty,
                    Accuracy = 100,
                    Fluency = 100,
                    CreatedAt = DateTime.Now
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ShadowingPage: Error loading sentences");
            SetState(s => s.IsBusy = false);
        }
    }

    /// <summary>
    /// Moves to the previous sentence in the list.
    /// </summary>
    async Task PreviousSentence()
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
                SetState(s =>
                {
                    s.DurationDisplay = cacheEntry.DurationDisplay;
                    s.AudioDuration = cacheEntry.Duration;
                    s.WaveformData = cacheEntry.WaveformData;
                });

                _logger.LogDebug("ShadowingPage: Restored cached time display: {DurationDisplay}", cacheEntry.DurationDisplay);
            }
        }
    }

    /// <summary>
    /// Moves to the next sentence in the list.
    /// </summary>
    async Task NextSentence()
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
                SetState(s =>
                {
                    s.DurationDisplay = cacheEntry.DurationDisplay;
                    s.AudioDuration = cacheEntry.Duration;
                    s.WaveformData = cacheEntry.WaveformData;
                });

                _logger.LogDebug("ShadowingPage: Restored cached time display: {DurationDisplay}", cacheEntry.DurationDisplay);
            }
        }
    }

    /// <summary>
    /// Toggles audio playback between playing and paused states.
    /// </summary>
    async Task ToggleAudioPlayback()
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
    Task PauseAudio()
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

            _logger.LogDebug("ShadowingPage: Audio playback paused");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes playback from the paused position.
    /// </summary>
    Task ResumeAudio()
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

            _logger.LogDebug("ShadowingPage: Audio playback resumed");
        }

        return Task.CompletedTask;
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
                SetState(s =>
                {
                    s.AudioDuration = cacheEntry.Duration;
                    s.DurationDisplay = cacheEntry.DurationDisplay;
                });

                _logger.LogDebug("ShadowingPage: Using cached audio for: {CacheKey}", cacheKey);
            }
            else
            {
                // Generate new audio stream if not in cache, passing the selected voice Id and speed
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
            _logger.LogDebug("ShadowingPage: Audio duration: {Duration} seconds", audioDuration);

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

                    var audioWidth = _audioPlayer.Duration * Constants.PixelsPerSecond;

                    // Extract waveform data from the audio stream
                    var waveformData = await _audioAnalyzer.GetWaveformAsync(memStream, (int)audioWidth);

                    // Store waveform in cache
                    cacheEntry.WaveformData = waveformData;

                    // Reset the original stream position
                    audioStream.Position = 0;

                    _logger.LogDebug("ShadowingPage: Extracted waveform data: {Samples} samples with duration {Duration}s", waveformData.Length, audioDuration);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ShadowingPage: Error analyzing audio waveform");
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
            _logger.LogError(ex, "ShadowingPage: Error playing audio");
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

            SetState(s =>
            {
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
        double totalWidth = _audioPlayer.Duration * Constants.PixelsPerSecond;

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
            return "--:--.---";

        TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
        return $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    /// <summary>
    /// Event handler for when audio playback ends.
    /// </summary>
    private void OnAudioPlaybackEnded(object sender, EventArgs e)
    {
        _playbackTimer?.Stop();

        SetState(s =>
        {
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
    Task StopAudio()
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

        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the current audio to an MP3 file using the FileSaver service.
    /// </summary>
    private async Task SaveAudioAsMp3()
    {
        if (State.CurrentAudioStream == null)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = "No audio available to save",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
            return;
        }

        try
        {
            SetState(s =>
            {
                s.IsSavingAudio = true;
                s.ExportProgressMessage = "Preparing audio for export...";
            });

            // Create a unique filename based on text and timestamp
            string safeFilename = MakeSafeFileName(State.CurrentSentenceText);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{safeFilename}_{timestamp}.mp3";

            SetState(s => s.ExportProgressMessage = "Saving audio file...");

            // Clone the stream to a memory stream to avoid position issues
            MemoryStream memoryStream = new MemoryStream();
            State.CurrentAudioStream.Position = 0;
            await State.CurrentAudioStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            // Reset original stream position
            State.CurrentAudioStream.Position = 0;

            // Use the FileSaver to save the audio
            var fileSaverResult = await _fileSaver.SaveAsync(fileName, memoryStream, new CancellationToken());

            // Check if the save was successful
            if (fileSaverResult.IsSuccessful)
            {
                SetState(s =>
                {
                    s.IsSavingAudio = false;
                    s.LastSavedFilePath = fileSaverResult.FilePath;
                    s.ExportProgressMessage = "File saved successfully!";
                });

                // Show success message
                var savedToast = new UXDivers.Popups.Maui.Controls.Toast { Title = "Audio saved successfully!" };
                await IPopupService.Current.PushAsync(savedToast);
                _ = Task.Delay(2500).ContinueWith(async _ =>
                {
                    try { await IPopupService.Current.PopAsync(savedToast); } catch { }
                });

                // Close the export menu after successful save
                SetState(s => s.IsExportMenuVisible = false);
            }
            else
            {
                // Show error if save was canceled or failed
                if (!string.IsNullOrEmpty(fileSaverResult.Exception?.Message))
                {
                    await IPopupService.Current.PushAsync(new SimpleActionPopup
                    {
                        Title = "Error",
                        Text = $"Failed to save audio: {fileSaverResult.Exception.Message}",
                        ActionButtonText = "OK",
                        ShowSecondaryActionButton = false
                    });
                }

                SetState(s => s.IsSavingAudio = false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ShadowingPage: Error saving audio");
            SetState(s =>
            {
                s.IsSavingAudio = false;
                s.ExportProgressMessage = $"Error: {ex.Message}";
            });

            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to save audio: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    /// <summary>
    /// Creates a safe filename from a text string by removing invalid characters.
    /// </summary>
    private string MakeSafeFileName(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "audio";

        // Replace invalid filename characters with underscores
        string invalidChars = new string(System.IO.Path.GetInvalidFileNameChars());
        string invalidRegStr = string.Format(@"[{0}]", Regex.Escape(invalidChars));
        string safe = Regex.Replace(text, invalidRegStr, "_");

        // Trim to reasonable length
        if (safe.Length > 50)
            safe = safe.Substring(0, 50);

        return safe;
    }

    /// <summary>
    /// Performs cleanup when the component is unmounted.
    /// </summary>

    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _timerService.Pause();
        }

        // Stop and dispose the playback timer
        _playbackTimer?.Stop();
        _playbackTimer = null;

        // Clean up audio resources when navigating away from the page
        StopAudio().ConfigureAwait(false);

        // Clean up cached streams
        foreach (var entry in _audioCache.Values)
        {
            entry.AudioStream?.Dispose();
        }
        _audioCache.Clear();

        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();
}