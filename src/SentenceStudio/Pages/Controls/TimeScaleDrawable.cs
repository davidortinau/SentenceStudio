// filepath: /Users/davidortinau/work/SentenceStudio/src/SentenceStudio/Pages/Controls/TimeScaleDrawable.cs
using Microsoft.Maui.Graphics;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// A custom drawable that renders a time scale with markers at different intervals.
/// Shows small tick marks for 1/10 second increments, medium ticks for half seconds,
/// and large ticks with text labels for full seconds.
/// </summary>
public class TimeScaleDrawable : IDrawable
{
    private double _audioDuration = 0;
    private double _pixelsPerSecond = 100;
    private Color _tickColor = Colors.Gray;
    private Color _textColor = Colors.Gray;
    
    /// <summary>
    /// Gets or sets the duration of the audio in seconds.
    /// </summary>
    public double AudioDuration
    {
        get => _audioDuration;
        set => _audioDuration = Math.Max(0, value);
    }
    
    /// <summary>
    /// Gets or sets how many pixels represent one second of audio.
    /// </summary>
    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set => _pixelsPerSecond = Math.Max(10, value); // At least 10 pixels per second
    }
    
    /// <summary>
    /// Gets or sets the color of tick marks.
    /// </summary>
    public Color TickColor
    {
        get => _tickColor;
        set => _tickColor = value;
    }
    
    /// <summary>
    /// Gets or sets the color of the text labels.
    /// </summary>
    public Color TextColor
    {
        get => _textColor;
        set => _textColor = value;
    }
    
    /// <summary>
    /// Calculates the total width of the time scale based on audio duration and scale.
    /// Ensures a minimum of 60 seconds (1 minute) is shown.
    /// </summary>
    public float TotalWidth => (float)(Math.Max(_audioDuration, 60) * _pixelsPerSecond);

    /// <summary>
    /// Implements the drawing logic for the time scale.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="dirtyRect">The region that needs to be redrawn.</param>
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float height = dirtyRect.Height;
        float width = dirtyRect.Width;
        
        // Calculate how many seconds to display based on the visible width
        int secondsToShow = (int)Math.Ceiling(width / _pixelsPerSecond);
        
        // Ensure we show at least the full audio duration or 60 seconds (1 minute), whichever is larger
        secondsToShow = Math.Max(secondsToShow, (int)Math.Ceiling(Math.Max(_audioDuration, 60)));
        
        // For each full second
        for (int second = 0; second <= secondsToShow; second++)
        {
            float x = (float)(second * _pixelsPerSecond);
            
            // Skip if outside visible area
            if (x < dirtyRect.X - 50 || x > dirtyRect.Right + 50)
                continue;
                
            // Draw second marker (full height tick + text)
            canvas.StrokeColor = _tickColor;
            canvas.StrokeSize = 2;
            canvas.DrawLine(x, 0, x, height * 0.5f);
            
            // Add text label for seconds
            canvas.FontSize = 10;
            canvas.FontColor = _textColor;
            canvas.DrawString(second.ToString(), x + 3, height * 0.3f, HorizontalAlignment.Left);
            
            // Draw half-second marker
            if (second < secondsToShow)
            {
                float halfSecondX = (float)((second + 0.5) * _pixelsPerSecond);
                canvas.StrokeColor = _tickColor.WithAlpha(0.8f);
                canvas.StrokeSize = 1.5f;
                canvas.DrawLine(halfSecondX, 0, halfSecondX, height * 0.4f);
            }
            
            // Draw 1/10 second markers
            for (int tenth = 1; tenth <= 9; tenth++)
            {
                // Skip the half-second mark as we already drew it
                if (tenth == 5)
                    continue;
                    
                float tenthSecondX = (float)((second + tenth * 0.1) * _pixelsPerSecond);
                
                // Skip if outside visible area
                if (tenthSecondX < dirtyRect.X - 10 || tenthSecondX > dirtyRect.Right + 10)
                    continue;
                    
                canvas.StrokeColor = _tickColor.WithAlpha(0.5f);
                canvas.StrokeSize = 1;
                canvas.DrawLine(tenthSecondX, 0, tenthSecondX, height * 0.25f);
            }
        }
    }
}