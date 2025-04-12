using Plugin.Maui.Audio;
using MauiReactor.Shapes;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// A simple floating audio player component that appears in the corner of the screen.
/// </summary>
public class FloatingAudioPlayerState
{
    /// <summary>
    /// Gets or sets whether the player is visible.
    /// </summary>
    public bool IsVisible { get; set; } = false;

    /// <summary>
    /// Gets or sets whether audio is currently playing.
    /// </summary>
    public bool IsPlaying { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the player is in loading state.
    /// </summary>
    public bool IsLoading { get; set; } = false;

    /// <summary>
    /// Gets or sets the current playback position as a normalized value (0-1).
    /// </summary>
    public float PlaybackPosition { get; set; } = 0f;
    
    /// <summary>
    /// Gets or sets the title to display on the player.
    /// </summary>
    public string Title { get; set; } = "Now Playing";
}

/// <summary>
/// A reusable floating audio player component that can be added to any page.
/// Shows play/pause, rewind, and stop controls for audio playback.
/// </summary>
partial class FloatingAudioPlayer : Component<FloatingAudioPlayerState>
{
    // Event handlers
    private readonly Action _onPlay;
    private readonly Action _onPause;
    private readonly Action _onRewind;
    private readonly Action _onStop;
    private readonly IAudioPlayer _audioPlayer;
    
    /// <summary>
    /// Creates a new floating audio player component.
    /// </summary>
    /// <param name="audioPlayer">The audio player to control</param>
    /// <param name="onPlay">Action to execute when play is pressed</param>
    /// <param name="onPause">Action to execute when pause is pressed</param>
    /// <param name="onRewind">Action to execute when rewind is pressed</param>
    /// <param name="onStop">Action to execute when stop is pressed</param>
    public FloatingAudioPlayer(
        IAudioPlayer audioPlayer,
        Action onPlay = null,
        Action onPause = null, 
        Action onRewind = null,
        Action onStop = null)
    {
        _audioPlayer = audioPlayer;
        _onPlay = onPlay;
        _onPause = onPause;
        _onRewind = onRewind;
        _onStop = onStop;
    }

    public override VisualNode Render()
    {
        return Border(
            Grid(rows: "0, 3, Auto", columns: "*",
                State.IsLoading ? 
                    null : 
                    new LinearProgressBar()
                        .Progress(State.PlaybackPosition)
                        .ProgressColor(Colors.Orange)
                        .TrackColor(Colors.Gray)
                        .HeightRequest(3)
                        .Margin(8, 0, 8, 0)
                        .GridRow(1),

                State.IsLoading ?
                    ActivityIndicator()
                        .IsRunning(true)
                        .HeightRequest(24).WidthRequest(24)
                        .GridRowSpan(3)
                        .Margin(8)
                        .HCenter().VCenter()
                    :
                    HStack(
                        ImageButton()
                            .Source(ApplicationTheme.IconRewindSm)
                            .BackgroundColor(Colors.Transparent)
                            .WidthRequest(18)
                            .HeightRequest(18)
                            .Padding(4)
                            .OnClicked(() =>
                            {
                                SetState(s => s.PlaybackPosition = 0f);
                                _onRewind?.Invoke();
                            }),

                        ImageButton()
                            .Source(State.IsPlaying ? ApplicationTheme.IconPauseSm : ApplicationTheme.IconPlaySm)
                            .BackgroundColor(Colors.Transparent)
                            .WidthRequest(18)
                            .HeightRequest(18)
                            .Padding(4)
                            .OnClicked(() =>
                            {
                                if (State.IsPlaying)
                                {
                                    SetState(s => s.IsPlaying = false);
                                    _onPause?.Invoke();
                                }
                                else
                                {
                                    SetState(s => s.IsPlaying = true);
                                    _onPlay?.Invoke();
                                }
                            }),

                        ImageButton()
                            .Source(ApplicationTheme.IconStopSm)
                            .BackgroundColor(Colors.Transparent)
                            .WidthRequest(18)
                            .HeightRequest(18)
                            .Padding(4)
                            .OnClicked(() =>
                            {
                                SetState(s =>
                                {
                                    s.IsPlaying = false;
                                    s.IsVisible = false;
                                    s.PlaybackPosition = 0f;
                                });
                                _onStop?.Invoke();
                            })
                    )
                    .HCenter()
                    .Spacing(4)
                    .Padding(4, 0, 8, 0)
                    .GridRow(2)
            )
            .RowSpacing(4)
        )
        .Padding(ApplicationTheme.Size120)
        .StrokeShape(new RoundRectangle().CornerRadius(12))
        .Stroke(Colors.Transparent)
        .BackgroundColor(Color.FromArgb("#AA343434"))
        .Shadow(
            Shadow()
                .Brush(Colors.Black)
                .Opacity(0.6f)
                .Offset(0, 3)
                .Radius(6)
        )
        .Margin(0, 0, 16, 32)
        .WidthRequest(220)
        .HeightRequest(70)
        .HEnd()
        .VEnd()
        .IsVisible(State.IsVisible);
    }

    /// <summary>
    /// Shows the audio player.
    /// </summary>
    public void Show()
    {
        SetState(s => s.IsVisible = true);
    }
    
    /// <summary>
    /// Shows the audio player in loading state.
    /// </summary>
    public void ShowLoading()
    {
        SetState(s => {
            s.IsVisible = true;
            s.IsLoading = true;
        });
    }

    /// <summary>
    /// Hides the audio player.
    /// </summary>
    public void Hide()
    {
        SetState(s => {
            s.IsVisible = false;
            s.IsPlaying = false;
            s.IsLoading = false;
        });
    }
    
    /// <summary>
    /// Sets the component to ready state (not loading).
    /// </summary>
    public void SetReady()
    {
        SetState(s => s.IsLoading = false);
    }

    /// <summary>
    /// Updates the playback position.
    /// </summary>
    /// <param name="position">Normalized position (0-1)</param>
    public void UpdatePosition(float position)
    {
        SetState(s => s.PlaybackPosition = position);
    }

    /// <summary>
    /// Sets the player to playing state.
    /// </summary>
    public void SetPlaying()
    {
        SetState(s => s.IsPlaying = true);
    }

    /// <summary>
    /// Sets the player to paused state.
    /// </summary>
    public void SetPaused()
    {
        SetState(s => s.IsPlaying = false);
    }

    /// <summary>
    /// Sets the title displayed in the player.
    /// </summary>
    /// <param name="title">Title text</param>
    public void SetTitle(string title)
    {
        SetState(s => s.Title = title);
    }
}

/// <summary>
/// Simple linear progress bar component for the floating audio player.
/// </summary>
partial class LinearProgressBar : Component
{
    private float _progress = 0;
    private Color _progressColor = Colors.Orange;
    private Color _trackColor = Colors.Gray;
    private float _heightRequest = 4;
    private Thickness _margin = new(0);

    public LinearProgressBar Progress(float progress)
    {
        _progress = progress;
        return this;
    }

    public LinearProgressBar ProgressColor(Color color)
    {
        _progressColor = color;
        return this;
    }

    public LinearProgressBar TrackColor(Color color)
    {
        _trackColor = color;
        return this;
    }

    public LinearProgressBar HeightRequest(float height)
    {
        _heightRequest = height;
        return this;
    }

    public LinearProgressBar Margin(Thickness margin)
    {
        _margin = margin;
        return this;
    }

    public LinearProgressBar Margin(double left, double top, double right, double bottom)
    {
        _margin = new Thickness(left, top, right, bottom);
        return this;
    }

    public override VisualNode Render()
    {
        return Grid("*", "*",
            // Background track
            Rectangle()
                .Fill(_trackColor)
                .HeightRequest(_heightRequest)
                .StrokeThickness(0)
                .RadiusX(_heightRequest / 2)
                .RadiusY(_heightRequest / 2)
                .VCenter(),
                
            // Progress indicator
            Rectangle()
                .Fill(_progressColor)
                .HeightRequest(_heightRequest)
                .WidthRequest(_progress * 100)
                .HStart()
                .StrokeThickness(0)
                .RadiusX(_heightRequest / 2)
                .RadiusY(_heightRequest / 2)
                .VCenter()
        )
        .Margin(_margin);
    }
}
