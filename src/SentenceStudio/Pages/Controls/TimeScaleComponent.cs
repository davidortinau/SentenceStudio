// filepath: /Users/davidortinau/work/SentenceStudio/src/SentenceStudio/Pages/Controls/TimeScaleComponent.cs


using Microsoft.Maui.Graphics;
using MauiReactor;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// MauiReactor component for time scale visualization using GraphicsView.
/// Shows 1/10 second increments with small hash marks, half seconds with medium lines,
/// and full seconds with thicker lines and text labels.
/// </summary>
partial class TimeScale : Component
{
    private readonly TimeScaleDrawable _drawable = new();
    private Color _tickColor = Colors.Gray;
    private Color _textColor = Colors.Gray;
    private double _audioDuration = 0; // Duration of the audio in seconds
    private double _pixelsPerSecond = 100; // Pixels per second of audio
    private int _height = 30; // Default height for the time scale

    private MauiControls.GraphicsView _graphicsViewRef;
    
    /// <summary>
    /// Sets the tick mark color of the time scale.
    /// </summary>
    public TimeScale TickColor(Color color)
    {
        _tickColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the color of text labels on the time scale.
    /// </summary>
    public TimeScale TextColor(Color color)
    {
        _textColor = color;
        return this;
    }
    
    /// <summary>
    /// Sets the height of the time scale component.
    /// </summary>
    public TimeScale Height(int height)
    {
        _height = height;
        return this;
    }

    /// <summary>
    /// Sets the duration of the audio in seconds.
    /// This is used to scale the time scale properly based on audio length.
    /// </summary>
    public TimeScale AudioDuration(double duration)
    {
        _audioDuration = duration;
        return this;
    }

    /// <summary>
    /// Sets how many pixels represent one second of audio.
    /// Higher values mean more detailed but wider time scale.
    /// </summary>
    public TimeScale PixelsPerSecond(double pixelsPerSecond)
    {
        _pixelsPerSecond = pixelsPerSecond;
        return this;
    }

    public override VisualNode Render()
    {
        // Use the TotalWidth property from the drawable which ensures
        // at least 1 minute (60 seconds) of time marks are shown
        float totalWidth = _drawable.TotalWidth;
        
        // Always use the calculated total width to ensure proper sizing
        var graphicsView = new MauiReactor.GraphicsView(graphicsViewRef => _graphicsViewRef = graphicsViewRef)
            .Drawable(_drawable)
            .HeightRequest(_height)
            .WidthRequest(totalWidth)
            .VStart()
            .HStart();
            
        return graphicsView;
    }

    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Configure the drawable
        _drawable.TickColor = _tickColor;
        _drawable.TextColor = _textColor;
        _drawable.AudioDuration = _audioDuration;
        _drawable.PixelsPerSecond = _pixelsPerSecond;
        
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
        _drawable.TickColor = _tickColor;
        _drawable.TextColor = _textColor;
        _drawable.AudioDuration = _audioDuration;
        _drawable.PixelsPerSecond = _pixelsPerSecond;
        
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