namespace SentenceStudio.Pages.Controls;

/// <summary>
/// MauiReactor component for audio waveform visualization using GraphicsView.
/// </summary>
partial class Waveform : Component
{
    private readonly WaveformDrawable _drawable = new();
    private float _amplitude = 0.7f;
    private Color _waveColor = Colors.SteelBlue;
    private Color _playedColor = Colors.Orange;
    private float _playbackPosition = 0;
    private bool _autoGenerate = true;
    private int _sampleCount = 400;
    private int _height = 80;
    private string _audioId = string.Empty; // Used to track when audio source changes
    private StreamHistory _streamHistory;
    private bool _streamHistoryChanged;

    private MauiControls.GraphicsView _graphicsViewRef;
    
    /// <summary>
    /// Gets or sets the amplitude multiplier for the waveform.
    /// </summary>
    public Waveform Amplitude(float amplitude)
    {
        _amplitude = amplitude;
        return this;
    }
    
    /// <summary>
    /// Gets or sets the color of the waveform.
    /// </summary>
    public Waveform WaveColor(Color color)
    {
        _waveColor = color;
        return this;
    }
    
    /// <summary>
    /// Gets or sets the color of the played portion of the waveform.
    /// </summary>
    public Waveform PlayedColor(Color color)
    {
        _playedColor = color;
        return this;
    }
    
    /// <summary>
    /// Gets or sets the current playback position as a value between 0 and 1.
    /// </summary>
    public Waveform PlaybackPosition(float position)
    {
        _playbackPosition = Math.Clamp(position, 0f, 1f);
        return this;
    }
    
    /// <summary>
    /// Sets whether to automatically generate a random waveform when mounted.
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
            _drawable.Reset(); // Reset waveform when audio changes
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

    public override VisualNode Render()
    {
        Debug.WriteLine($"~~ Waveform.Render() - Height: {_height}");
        return new MauiReactor.GraphicsView(graphicsViewRef => _graphicsViewRef = graphicsViewRef)
            .Drawable(_drawable)
            .HeightRequest(_height);
    }
    
    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Configure the drawable
        _drawable.WaveColor = _waveColor;
        _drawable.PlayedColor = _playedColor;
        _drawable.Amplitude = _amplitude;
        _drawable.PlaybackPosition = _playbackPosition;
        
        // Use StreamHistory waveform data if available
        if (_streamHistory != null && _streamHistory.IsWaveformAnalyzed)
        {
            _drawable.UpdateWaveform(_streamHistory.WaveformData);
            _streamHistoryChanged = false;
        }
        // Generate a random waveform if requested and no StreamHistory data available
        else if (_autoGenerate)
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
        
        // Update with StreamHistory data if it changed
        if (_streamHistoryChanged && _streamHistory != null && _streamHistory.IsWaveformAnalyzed)
        {
            _drawable.UpdateWaveform(_streamHistory.WaveformData);
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
    public void UpdateWaveform(float[] audioData)
    {
        _drawable.UpdateWaveform(audioData);
        
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