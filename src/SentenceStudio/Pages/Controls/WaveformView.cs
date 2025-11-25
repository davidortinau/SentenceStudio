using Microsoft.Extensions.Logging;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// MauiReactor component for audio waveform visualization with integrated timescale using GraphicsView.
/// </summary>
partial class WaveformView : Component
{
    private ILogger<WaveformView>? _logger;
    private WaveformDrawable _drawable;
    private float _amplitude = 0.7f;
    private Color _waveColor = Colors.SteelBlue;
    private Color _playedColor = Colors.Orange;
    private Color _tickColor = Colors.Gray;
    private Color _textColor = Colors.Gray;
    private float _playbackPosition = 0;
    private int _sampleCount = 400;
    private int _height = 100;
    private string _audioId = string.Empty; // Used to track when audio source changes
    private StreamHistory _streamHistory;
    private bool _streamHistoryChanged;
    private double _audioDuration = 0; // Duration of the audio in seconds
    private double _pixelsPerSecond = Constants.PixelsPerSecond; // Pixels per second of audio
    private bool _showTimeScale = false; // Whether to show the timescale
    private float[] _waveformData;

    private MauiControls.GraphicsView _graphicsViewRef;
    private Action<float> _onPositionSelected;
    private Action _onInteractionStarted;
    
    
    // Constructor to ensure drawable is initialized
    public WaveformView()
    {

    }
    
    // === Waveform Properties ===
    
    /// <summary>
    /// Sets the amplitude multiplier for the waveform.
    /// </summary>
    public WaveformView Amplitude(float amplitude)
    {
        _amplitude = amplitude;
        return this;
    }

    public WaveformView WaveformData(float[] samples)
    {
        _waveformData = samples;
        ConfigureDrawable();
        return this;
    }
    
    /// <summary>
    /// Sets the color of the waveform.
    /// </summary>
    public WaveformView WaveColor(Color color)
    {
        _waveColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the color of the played portion of the waveform.
    /// </summary>
    public WaveformView PlayedColor(Color color)
    {
        _playedColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the current playback position as a value between 0 and 1.
    /// </summary>
    public WaveformView PlaybackPosition(float position)
    {
        _playbackPosition = Math.Clamp(position, 0f, 1f);
        return this;
    }
    
    /// <summary>
    /// Sets the number of samples to generate for the random waveform.
    /// </summary>
    public WaveformView SampleCount(int sampleCount)
    {
        _sampleCount = sampleCount;
        return this;
    }

    /// <summary>
    /// Sets the height of the waveform component.
    /// </summary>
    public WaveformView Height(int height)
    {
        _height = height;
        return this;
    }
    
    /// <summary>
    /// Sets the audio Id to track when the audio source has changed.
    /// Use this to reset the waveform when playing a different audio source.
    /// </summary>
    public WaveformView AudioId(string audioId)
    {
        if (_audioId != audioId)
        {
            _audioId = audioId;
            
            // Configure the new drawable with current settings
            ConfigureDrawable();
        }
        return this;
    }
    
    /// <summary>
    /// Sets the StreamHistory item to display its waveform data.
    /// This overrides the auto-generation of random data.
    /// </summary>
    public WaveformView StreamHistoryItem(StreamHistory streamHistory)
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
    public WaveformView AudioDuration(double duration)
    {
        _audioDuration = duration;
        return this;
    }
    
    /// <summary>
    /// Sets how many pixels represent one second of audio.
    /// Higher values mean more detailed but wider waveforms.
    /// </summary>
    public WaveformView PixelsPerSecond(double pixelsPerSecond)
    {
        _pixelsPerSecond = pixelsPerSecond;
        return this;
    }
    
    // === TimeScale Properties ===
    
    /// <summary>
    /// Sets whether to show the timescale.
    /// </summary>
    public WaveformView ShowTimeScale(bool show)
    {
        _showTimeScale = show;
        return this;
    }
    
    /// <summary>
    /// Sets the color of tick marks on the time scale.
    /// </summary>
    public WaveformView TickColor(Color color)
    {
        _tickColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the color of text labels on the time scale.
    /// </summary>
    public WaveformView TextColor(Color color)
    {
        _textColor = color;
        return this;
    }

    /// <summary>
    /// Sets the callback to be invoked when the user selects a position on the waveform.
    /// </summary>
    public WaveformView OnPositionSelected(Action<float> callback)
    {
        _onPositionSelected = callback;
        return this;
    }
    
    /// <summary>
    /// Sets the callback to be invoked when the user starts interacting with the waveform.
    /// </summary>
    public WaveformView OnInteractionStarted(Action callback)
    {
        _onInteractionStarted = callback;
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
            .VCenter()
            .OnStartInteraction(OnWaveformStartInteraction)
            .OnEndInteraction(OnWaveformEndInteraction)
            .OnDragInteraction(OnWaveformDragInteraction);

        // Set width request if we have a valid audio duration
        if (widthRequest > 0)
        {
            graphicsView = graphicsView.WidthRequest(widthRequest);
        }

        return graphicsView;
    }

    private void OnWaveformDragInteraction(object sender, TouchEventArgs args)
    {
        if (_onPositionSelected == null || args == null)
            return;

        // Calculate the position as a value between 0 and 1 based on drag location
        if (sender is MauiControls.GraphicsView graphicsView && 
            graphicsView.Width > 0)
        {
            var position = args.Touches[0];
            
            // Get the total waveform width which may be different from the view width
            float totalWidth = _drawable?.TotalWidth ?? (float)graphicsView.Width;
            
            // Get the X position, accounting for scrolling in a scroll view parent
            float touchX = (float)position.X;
            
            // Calculate normalized position based on the total audio width, not just the visible width
            float normalizedPosition;
            
            if (_audioDuration > 0)
            {
                // If we have a valid duration, calculate position based on the total audio duration
                float pixelsPerSecond = (float)_pixelsPerSecond;
                normalizedPosition = touchX / (float)(_audioDuration * pixelsPerSecond);
            }
            else
            {
                // Fallback to using the view width if no duration is available
                normalizedPosition = touchX / (float)graphicsView.Width;
            }
            
            // Clamp the value between 0 and 1
            normalizedPosition = Math.Clamp(normalizedPosition, 0f, 1f);
            
            _logger?.LogDebug("WaveformView: Drag position: X={TouchX}, Normalized={NormalizedPosition}", touchX, normalizedPosition);
            
            // Update the drawable's position directly for immediate visual feedback
            _drawable.PlaybackPosition = normalizedPosition;
            
            // Request a redraw
            graphicsView.Invalidate();
            
            // Invoke the callback with the selected position
            _onPositionSelected(normalizedPosition);
        }
    }

    private void OnWaveformStartInteraction(object sender, TouchEventArgs args)
    {
        _onInteractionStarted();
    }

    private void OnWaveformEndInteraction(object sender, TouchEventArgs args)
    {
        if (_onPositionSelected == null || args == null)
            return;

        // Calculate the position as a value between 0 and 1 based on tap location
        if (sender is MauiControls.GraphicsView graphicsView && 
            graphicsView.Width > 0)
        {
            var position = args.Touches[0];
            
            // Get the total waveform width which may be different from the view width
            float totalWidth = _drawable?.TotalWidth ?? (float)graphicsView.Width;
            
            // Get the X position, accounting for scrolling in a scroll view parent
            float touchX = (float)position.X;
            
            // Calculate normalized position based on the total audio width, not just the visible width
            float normalizedPosition;
            
            if (_audioDuration > 0)
            {
                // If we have a valid duration, calculate position based on the total audio duration
                float pixelsPerSecond = (float)_pixelsPerSecond;
                normalizedPosition = touchX / (float)(_audioDuration * pixelsPerSecond);
            }
            else
            {
                // Fallback to using the view width if no duration is available
                normalizedPosition = touchX / (float)graphicsView.Width;
            }
            
            // Clamp the value between 0 and 1
            normalizedPosition = Math.Clamp(normalizedPosition, 0f, 1f);
            
            _logger?.LogDebug("WaveformView: Tap position: X={TouchX}, Normalized={NormalizedPosition:F2}, TotalWidth={TotalWidth}", touchX, normalizedPosition, totalWidth);
            
            // Update the drawable's position directly for immediate visual feedback
            _drawable.PlaybackPosition = normalizedPosition;
            
            // Request a redraw
            graphicsView.Invalidate();
            
            // Invoke the callback with the selected position
            _onPositionSelected(normalizedPosition);
        }
    }

    /// <summary>
    /// Configure the drawable with the current settings
    /// </summary>
    private void ConfigureDrawable()
    {
        _drawable = new WaveformDrawable();
        _drawable.AudioId = _audioId; // Ensure the drawable has the correct audio ID
        _drawable.WaveColor = _waveColor;
        _drawable.PlayedColor = _playedColor;
        _drawable.Amplitude = _amplitude;
        _drawable.PlaybackPosition = _playbackPosition;
        _drawable.AudioDuration = _audioDuration;
        _drawable.PixelsPerSecond = _pixelsPerSecond;
        _drawable.SampleCount = _sampleCount;
        _drawable.ShowTimeScale = _showTimeScale;
        _drawable.TickColor = _tickColor;
        _drawable.TextColor = _textColor;
        _drawable.UpdateWaveform(_waveformData, _audioDuration);
    }
    
    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Configure the drawable
        ConfigureDrawable();
        
        // Use StreamHistory waveform data if available
        if (_streamHistory != null && _streamHistory.IsWaveformAnalyzed)
        {
            // Update waveform with data and duration if available
            _drawable.UpdateWaveform(_streamHistory.WaveformData, _streamHistory.Duration);
            _streamHistoryChanged = false;
        }
        
        if (_waveformData != null && _waveformData.Length > 0)
        {
            _drawable.UpdateWaveform(_waveformData);
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
        ConfigureDrawable();
        
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