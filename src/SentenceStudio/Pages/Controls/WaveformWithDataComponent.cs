using Microsoft.Maui.Graphics;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// MauiReactor component for audio waveform visualization using GraphicsView with pre-loaded waveform data.
/// This component follows the MVU pattern by accepting data through its constructor rather than
/// through direct method calls after creation.
/// </summary>
partial class WaveformWithData : Component
{
    private readonly WaveformDrawable _drawable = new();
    private readonly Color _waveColor;
    private readonly Color _playedColor;
    private readonly float _playbackPosition;
    private readonly float _amplitude;
    private readonly float[] _waveformData;
    private readonly int _height;

    private MauiControls.GraphicsView _graphicsViewRef;

    /// <summary>
    /// Creates a new instance of WaveformWithData with the specified parameters.
    /// </summary>
    /// <param name="waveColor">Color for the unplayed portion of the waveform</param>
    /// <param name="playedColor">Color for the played portion of the waveform</param>
    /// <param name="playbackPosition">Current playback position (0-1)</param>
    /// <param name="amplitude">Amplitude multiplier for the waveform</param>
    /// <param name="waveformData">Array of waveform data samples</param>
    /// <param name="height">Height of the waveform in pixels</param>
    public WaveformWithData(
        Color waveColor,
        Color playedColor,
        float playbackPosition,
        float amplitude,
        float[] waveformData,
        int height)
    {
        _waveColor = waveColor;
        _playedColor = playedColor;
        _playbackPosition = Math.Clamp(playbackPosition, 0f, 1f);
        _amplitude = amplitude;
        _waveformData = waveformData;
        _height = height;
    }

    public override VisualNode Render()
    {
        Debug.WriteLine($"~~ WaveformWithData.Render() - Height: {_height}");
        return new MauiReactor.GraphicsView(graphicsViewRef => _graphicsViewRef = graphicsViewRef)
            .Drawable(_drawable)
            .HeightRequest(_height);
    }
    
    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Configure the drawable with our immutable properties
        _drawable.WaveColor = _waveColor;
        _drawable.PlayedColor = _playedColor;
        _drawable.Amplitude = _amplitude;
        _drawable.PlaybackPosition = _playbackPosition;
        
        // Set the waveform data immediately
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
        
        // Update the playback position when props change
        _drawable.PlaybackPosition = _playbackPosition;
        
        // Request a redraw
        if (MauiControls.Application.Current != null && _graphicsViewRef != null)
        {
            MauiControls.Application.Current.Dispatcher.Dispatch(() =>
            {
                _graphicsViewRef?.Invalidate();
            });
        }
    }
}