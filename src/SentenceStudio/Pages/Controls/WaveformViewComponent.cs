using MauiReactor.Compatibility;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// MauiReactor component for audio waveform visualization with integrated timescale using GraphicsView.
/// </summary>
partial class Waveform : Component
{
    private readonly WaveformDrawable _drawable = new();
    private float _amplitude = 0.7f;
    private Color _waveColor = Colors.SteelBlue;
    private Color _playedColor = Colors.Orange;
    private Color _tickColor = Colors.Gray;
    private Color _textColor = Colors.Gray;
    private float _playbackPosition = 0;
    private bool _autoGenerate = true;
    private int _sampleCount = 400;
    private int _height = 100;
    private string _audioId = string.Empty; // Used to track when audio source changes
    private StreamHistory _streamHistory;
    private bool _streamHistoryChanged;
    private double _audioDuration = 0; // Duration of the audio in seconds
    private double _pixelsPerSecond = 100; // Pixels per second of audio
    private bool _showTimeScale = false; // Whether to show the timescale

    private MauiControls.GraphicsView _graphicsViewRef;
    
    // === Waveform Properties ===
    
    /// <summary>
    /// Sets the amplitude multiplier for the waveform.
    /// </summary>
    public Waveform Amplitude(float amplitude)
    {
        _amplitude = amplitude;
        return this;
    }
    
    /// <summary>
    /// Sets the color of the waveform.
    /// </summary>
    public Waveform WaveColor(Color color)
    {
        _waveColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the color of the played portion of the waveform.
    /// </summary>
    public Waveform PlayedColor(Color color)
    {
        _playedColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the current playback position as a value between 0 and 1.
    /// </summary>
    public Waveform PlaybackPosition(float position)
    {
        _playbackPosition = Math.Clamp(position, 0f, 1f);
        return this;
    }
    
    /// <summary>
    /// Sets whether to automatically generate a random waveform when no data is available.
    /// </summary>
    public Waveform AutoGenerateWaveform(bool autoGenerate)
    {
        _autoGenerate = autoGenerate;
        return this;
    }
    
    /// <summary>
    /// Sets the number of samples to generate for the random waveform.
    /// </summary>
    public Waveform SampleCount(int sampleCount)
    {
        _sampleCount = sampleCount;
        return this;
    }

    /// <summary>
    /// Sets the height of the waveform component.
    /// </summary>
    public Waveform Height(int height)
    {
        _height = height;
        return this;
    }
    
    /// <summary>
    /// Sets the audio ID to track when the audio source has changed.
    /// Use this to reset the waveform when playing a different audio source.
    /// </summary>
    public Waveform AudioId(string audioId)
    {
        if (_audioId != audioId)
        {
            _audioId = audioId;
            // Don't reset waveform anymore, instead pass the ID to drawable
            _drawable.AudioId = audioId;
        }
        return this;
    }
    
    /// <summary>
    /// Sets the StreamHistory item to display its waveform data.
    /// This overrides the auto-generation of random data.
    /// </summary>
    public Waveform StreamHistoryItem(StreamHistory streamHistory)
    {
        if (_streamHistory != streamHistory)
        {
            _streamHistory = streamHistory;
            _streamHistoryChanged = true;
        }
        return this;
    }
    
    /// <summary>
    /// Sets the duration of the audio in seconds.
    /// This is used to scale the waveform properly based on audio length.
    /// </summary>
    public Waveform AudioDuration(double duration)
    {
        _audioDuration = duration;
        return this;
    }
    
    /// <summary>
    /// Sets how many pixels represent one second of audio.
    /// Higher values mean more detailed but wider waveforms.
    /// </summary>
    public Waveform PixelsPerSecond(double pixelsPerSecond)
    {
        _pixelsPerSecond = pixelsPerSecond;
        return this;
    }
    
    // === TimeScale Properties ===
    
    /// <summary>
    /// Sets whether to show the timescale.
    /// </summary>
    public Waveform ShowTimeScale(bool show)
    {
        _showTimeScale = show;
        return this;
    }
    
    /// <summary>
    /// Sets the color of tick marks on the time scale.
    /// </summary>
    public Waveform TickColor(Color color)
    {
        _tickColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the color of text labels on the time scale.
    /// </summary>
    public Waveform TextColor(Color color)
    {
        _textColor = color;
        return this;
    }

    public override VisualNode Render()
    {
        // Calculate the total width based on audio duration and pixels per second
        // This ensures a minimum of 60 seconds (1 minute) is shown
        float totalWidth = _drawable.TotalWidth;
        
        // If we have a valid duration and total width would be greater than usual,
        // set explicit width to make the waveform scrollable
        float widthRequest = (_audioDuration > 0) ? Math.Max(totalWidth, 300) : -1;

        var graphicsView = new MauiReactor.GraphicsView(graphicsViewRef => _graphicsViewRef = graphicsViewRef)
            .Drawable(_drawable)
            .HeightRequest(_height)
            .HStart()
            .VCenter();
            
        // Set width request if we have a valid audio duration
        if (widthRequest > 0)
        {
            graphicsView = graphicsView.WidthRequest(widthRequest);
        }
        
        return graphicsView;
    }
    
    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Configure the drawable
        _drawable.WaveColor = _waveColor;
        _drawable.PlayedColor = _playedColor;
        _drawable.Amplitude = _amplitude;
        _drawable.PlaybackPosition = _playbackPosition;
        _drawable.AudioDuration = _audioDuration;
        _drawable.PixelsPerSecond = _pixelsPerSecond;
        _drawable.AutoGenerateWaveform = _autoGenerate;
        _drawable.SampleCount = _sampleCount;
        _drawable.ShowTimeScale = _showTimeScale;
        _drawable.TickColor = _tickColor;
        _drawable.TextColor = _textColor;
        
        // Use StreamHistory waveform data if available
        if (_streamHistory != null && _streamHistory.IsWaveformAnalyzed)
        {
            // Update waveform with data and duration if available
            _drawable.UpdateWaveform(_streamHistory.WaveformData, _streamHistory.Duration);
            _streamHistoryChanged = false;
        }
        // Generate a random waveform if auto-generate is enabled and no data is available yet
        else if (_autoGenerate && !_drawable.HasWaveformData)
        {
            _drawable.GenerateRandomWaveform(_sampleCount);
        }
        
        // Force an initial draw
        if (_graphicsViewRef != null)
        {
            MauiControls.Application.Current?.Dispatcher.Dispatch(() => _graphicsViewRef?.Invalidate());
        }
    }

    protected override void OnPropsChanged()
    {
        base.OnPropsChanged();
        
        // Update the drawable properties when they change
        _drawable.WaveColor = _waveColor;
        _drawable.PlayedColor = _playedColor;
        _drawable.Amplitude = _amplitude;
        _drawable.PlaybackPosition = _playbackPosition;
        _drawable.AudioDuration = _audioDuration;
        _drawable.PixelsPerSecond = _pixelsPerSecond;
        _drawable.AutoGenerateWaveform = _autoGenerate;
        _drawable.SampleCount = _sampleCount;
        _drawable.ShowTimeScale = _showTimeScale;
        _drawable.TickColor = _tickColor;
        _drawable.TextColor = _textColor;
        
        // Update with StreamHistory data if it changed
        if (_streamHistoryChanged && _streamHistory != null && _streamHistory.IsWaveformAnalyzed)
        {
            _drawable.UpdateWaveform(_streamHistory.WaveformData, _streamHistory.Duration);
            _streamHistoryChanged = false;
        }
        
        // Request a redraw
        if (MauiControls.Application.Current != null && _graphicsViewRef != null)
        {
            MauiControls.Application.Current.Dispatcher.Dispatch(() =>
            {
                _graphicsViewRef?.Invalidate();
            });
        }
    }
    
    /// <summary>
    /// Updates the waveform with the provided audio data.
    /// </summary>
    /// <param name="audioData">The audio sample data.</param>
    /// <param name="duration">The duration of the audio in seconds (optional).</param>
    public void UpdateWaveform(float[] audioData, double duration = 0)
    {
        _drawable.UpdateWaveform(audioData, duration);
        
        if (duration > 0)
        {
            _audioDuration = duration;
        }
        
        // Request a redraw
        if (MauiControls.Application.Current != null && _graphicsViewRef != null)
        {
            MauiControls.Application.Current.Dispatcher.Dispatch(() => 
            {
                _graphicsViewRef?.Invalidate();
            });
        }
    }

    /// <summary>
    /// Updates the playback position without recreating the whole waveform.
    /// This is more efficient for frequent position updates during playback.
    /// </summary>
    /// <param name="position">The new playback position (0-1).</param>
    public void UpdatePlaybackPosition(float position)
    {
        _drawable.PlaybackPosition = Math.Clamp(position, 0f, 1f);
        
        // Request a redraw on the UI thread
        if (MauiControls.Application.Current != null && _graphicsViewRef != null)
        {
            MauiControls.Application.Current.Dispatcher.Dispatch(() => 
            {
                _graphicsViewRef?.Invalidate();
            });
        }
    }

    /// <summary>
    /// Resets the waveform to generate new samples on next draw.
    /// Call this when switching to a different audio source.
    /// </summary>
    public void Reset()
    {
        _drawable.Reset();
    }
}