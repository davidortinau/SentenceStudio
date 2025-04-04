using Microsoft.Maui.Graphics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// A custom drawable that renders an audio waveform visualization with timescale.
/// </summary>
public class WaveformDrawable : IDrawable
{
    // Waveform properties
    private readonly List<float> _audioSamples = new();
    private float _playbackPosition = 0;
    private readonly Random _random = new(42); // Use fixed seed for consistent random generation
    private Color _waveColor = Colors.SteelBlue;
    private Color _playedColor = Colors.Orange;
    private float _amplitude = 0.7f;
    private bool _randomSamplesGenerated = false;
    private double _audioDuration = 0; // Duration of audio in seconds
    private double _pixelsPerSecond = 100; // Default scale: 100 pixels per second
    private string _currentAudioId = string.Empty; // ID for the current audio being displayed
    private bool _autoGenerateWaveform = false; // Default to auto-generating waveform
    private int _sampleCount = 400; // Default sample count for random waveforms
    
    // TimeScale properties
    private Color _tickColor = Colors.Gray;
    private Color _textColor = Colors.Gray;
    private bool _showTimeScale = false; // Whether to show timescale

    // Cache for storing waveform data by audio ID
    private readonly Dictionary<string, (float[] Samples, double Duration)> _waveformCache = new();
    
    /// <summary>
    /// Gets or sets the color of the waveform.
    /// </summary>
    public Color WaveColor 
    { 
        get => _waveColor;
        set => _waveColor = value;
    }

    /// <summary>
    /// Gets or sets the color of the played portion of the waveform.
    /// </summary>
    public Color PlayedColor 
    { 
        get => _playedColor;
        set => _playedColor = value;
    }

    /// <summary>
    /// Gets or sets the amplitude multiplier for the waveform.
    /// </summary>
    public float Amplitude 
    { 
        get => _amplitude;
        set => _amplitude = value;
    }

    /// <summary>
    /// Gets or sets the color of tick marks on the time scale.
    /// </summary>
    public Color TickColor
    {
        get => _tickColor;
        set => _tickColor = value;
    }
    
    /// <summary>
    /// Gets or sets the color of text labels on the time scale.
    /// </summary>
    public Color TextColor
    {
        get => _textColor;
        set => _textColor = value;
    }
    
    /// <summary>
    /// Gets or sets whether to show the time scale.
    /// </summary>
    public bool ShowTimeScale
    {
        get => _showTimeScale;
        set => _showTimeScale = value;
    }

    /// <summary>
    /// Gets or sets the current playback position as a value between 0 and 1.
    /// </summary>
    public float PlaybackPosition 
    { 
        get => _playbackPosition;
        set => _playbackPosition = Math.Clamp(value, 0f, 1f);
    }

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
    /// Gets or sets the ID of the current audio being displayed.
    /// Used to retrieve cached waveform data.
    /// </summary>
    public string AudioId
    {
        get => _currentAudioId;
        set
        {
            if (_currentAudioId != value)
            {
                _currentAudioId = value;
                LoadCachedWaveform();
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the waveform should be automatically generated when no data is available.
    /// </summary>
    public bool AutoGenerateWaveform
    {
        get => _autoGenerateWaveform;
        set => _autoGenerateWaveform = value;
    }

    /// <summary>
    /// Gets or sets the number of samples to generate for random waveforms.
    /// </summary>
    public int SampleCount
    {
        get => _sampleCount;
        set => _sampleCount = Math.Max(10, value); // Ensure at least 10 samples
    }

    /// <summary>
    /// Calculates the total width of the waveform based on audio duration and scale.
    /// Ensures a minimum of 60 seconds (1 minute) is shown.
    /// </summary>
    public float TotalWidth => (float)(Math.Max(_audioDuration, 60) * _pixelsPerSecond);

    /// <summary>
    /// Loads cached waveform data for the current audio ID if available.
    /// </summary>
    private void LoadCachedWaveform()
    {
        if (string.IsNullOrEmpty(_currentAudioId))
            return;
            
        if (_waveformCache.TryGetValue(_currentAudioId, out var cachedData))
        {
            Debug.WriteLine($"Using cached waveform data for audio ID: {_currentAudioId}");
            _audioSamples.Clear();
            _audioSamples.AddRange(cachedData.Samples);
            _audioDuration = cachedData.Duration;
            _randomSamplesGenerated = true; // Mark as having data so we don't generate random samples
        }
        else
        {
            // If not found in cache, we'll need new data
            _audioSamples.Clear();
            _randomSamplesGenerated = false;
        }
    }

    /// <summary>
    /// Generates a random waveform with the specified number of samples.
    /// Used for preview purposes until actual audio data is processed.
    /// </summary>
    /// <param name="sampleCount">Number of samples to generate. Defaults to the SampleCount property value.</param>
    public void GenerateRandomWaveform(int sampleCount = 0)
    {
        // If no sample count is provided, use the property value
        if (sampleCount <= 0)
        {
            sampleCount = _sampleCount;
        }
        
        // If we have a valid audio ID, try to load from cache first
        if (!string.IsNullOrEmpty(_currentAudioId) && _waveformCache.ContainsKey(_currentAudioId))
        {
            LoadCachedWaveform();
            return;
        }
        
        // Only generate samples if we haven't already
        if (_randomSamplesGenerated && _audioSamples.Count > 0)
            return;
            
        _audioSamples.Clear();
        
        // Use a seed value for consistent random generation
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = (float)(_random.NextDouble() * _amplitude);
            
            // Add some variation to make it look like speech with periodic patterns
            if (i % 10 < 3)
            {
                sample *= 0.3f; // Quieter sections
            }
            else if (i % 15 == 0)
            {
                sample *= 1.2f; // Occasional peaks
            }
            
            _audioSamples.Add(sample);
        }
        
        _randomSamplesGenerated = true;
        Debug.WriteLine($"Generated {sampleCount} random audio samples for waveform visualization");
    }

    /// <summary>
    /// Updates the waveform with the provided audio data.
    /// </summary>
    /// <param name="audioData">The audio sample data.</param>
    /// <param name="duration">The duration of the audio in seconds.</param>
    public void UpdateWaveform(float[] audioData, double duration = 0)
    {
        Debug.WriteLine($"Updating waveform with {audioData?.Length ?? 0} samples, duration: {duration}s, audioId: {_currentAudioId}");
        if (audioData == null || audioData.Length == 0)
            return;
            
        _audioSamples.Clear();
        _audioSamples.AddRange(audioData);
        _randomSamplesGenerated = true;
        
        // Update audio duration if provided
        if (duration > 0)
        {
            _audioDuration = duration;
        }
        
        // Cache the waveform data by audio ID if we have a valid ID
        if (!string.IsNullOrEmpty(_currentAudioId))
        {
            // Create a copy of the audio data to store in cache
            float[] dataCopy = new float[audioData.Length];
            Array.Copy(audioData, dataCopy, audioData.Length);
            
            _waveformCache[_currentAudioId] = (dataCopy, duration);
            Debug.WriteLine($"Cached waveform data for audio ID: {_currentAudioId}");
        }
    }
    
    /// <summary>
    /// Resets the waveform's generated samples, forcing it to create new ones.
    /// Use this when you want to display a different waveform for a new audio.
    /// </summary>
    public void Reset()
    {
        _audioSamples.Clear();
        _randomSamplesGenerated = false;
        _audioDuration = 0;
    }
    
    /// <summary>
    /// Clears all cached waveform data.
    /// </summary>
    public void ClearCache()
    {
        _waveformCache.Clear();
        Debug.WriteLine("Cleared waveform cache");
    }

    /// <summary>
    /// Implements the drawing logic for the waveform.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="dirtyRect">The region that needs to be redrawn.</param>
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float height = dirtyRect.Height;
        float width = dirtyRect.Width;
        
        // Determine time scale height - about 30% of total height when enabled, or 0 if disabled
        float timeScaleHeight = _showTimeScale ? Math.Min(30, height * 0.3f) : 0;
        
        // If time scale is enabled, draw it first
        if (_showTimeScale)
        {
            DrawTimeScale(canvas, new RectF(dirtyRect.X, dirtyRect.Y, dirtyRect.Width, timeScaleHeight));
        }

        // Calculate the waveform area
        float waveformHeight = height - timeScaleHeight;
        float waveformY = dirtyRect.Y + timeScaleHeight;
        
        // Draw the waveform in the remaining space
        DrawWaveform(canvas, new RectF(dirtyRect.X, waveformY, dirtyRect.Width, waveformHeight));
        
        // Calculate playhead position
        if (_audioDuration > 0)
        {
            float audioWidth = (float)(_audioDuration * _pixelsPerSecond);
            float playheadX = dirtyRect.X + (audioWidth * _playbackPosition);
            
            // Draw vertical playhead line across the entire height (timescale + waveform)
            DrawPlayhead(canvas, playheadX, dirtyRect.Y, height);
        }
    }
    
    /// <summary>
    /// Draws the playhead indicator at the current position.
    /// </summary>
    private void DrawPlayhead(ICanvas canvas, float x, float y, float height)
    {
        // Use the same color as the played part of the waveform for consistency
        Color playheadColor = _playedColor;
        
        // Draw a vertical line for the playhead
        canvas.StrokeColor = playheadColor.WithAlpha(0.8f);
        canvas.StrokeSize = 2f;
        canvas.DrawLine(x, y, x, y + height);
        
        // Draw a triangle at the top of the playhead pointing downward
        float triangleSize = 12f;
        var trianglePath = new PathF();
        trianglePath.MoveTo(x, y + triangleSize);
        trianglePath.LineTo(x - triangleSize/2, y);
        trianglePath.LineTo(x + triangleSize/2, y);
        trianglePath.Close();
        
        canvas.FillColor = playheadColor;
        canvas.FillPath(trianglePath);
        
        // No circle at the bottom as requested
    }
    
    /// <summary>
    /// Draws just the waveform part of the visualization.
    /// </summary>
    private void DrawWaveform(ICanvas canvas, RectF dirtyRect)
    {
        // If we have no samples and auto-generation is enabled, generate random ones
        if (_audioSamples.Count == 0 && !_randomSamplesGenerated && _autoGenerateWaveform)
        {
            Debug.WriteLine("No audio samples available, generating random samples for preview");
            GenerateRandomWaveform(_sampleCount);
            if (_audioSamples.Count == 0)
            {
                return; // Safety check
            }
        }
        else if (_audioSamples.Count == 0)
        {
            // No samples and auto-generation is disabled, nothing to draw
            return;
        }

        float height = dirtyRect.Height;
        float width = dirtyRect.Width;
        float centerY = dirtyRect.Y + (height / 2);
        
        // Calculate total width based on audio duration and scale
        float audioWidth;
        
        if (_audioDuration > 0)
        {
            // Use time-based scaling if we have duration info
            audioWidth = (float)(_audioDuration * _pixelsPerSecond);
        }
        else
        {
            // Otherwise use available width
            audioWidth = width;
        }
        
        // Always draw the midline across the entire container/dirtyRect width
        canvas.StrokeColor = Colors.Gray.WithAlpha(0.3f);
        canvas.StrokeSize = 1;
        canvas.DrawLine(dirtyRect.X, centerY, dirtyRect.X + width, centerY);
        
        // Calculate bar width based on available samples
        float barWidth = audioWidth / _audioSamples.Count;
        float barSpacing = Math.Max(0.5f, barWidth * 0.1f); // Adjust spacing based on bar width
        barWidth -= barSpacing;
        
        // Ensure minimum bar width
        barWidth = Math.Max(0.5f, barWidth);

        // Calculate playback position - make sure we're using the same scale as the waveform width
        float playbackX = audioWidth * _playbackPosition;

        for (int i = 0; i < _audioSamples.Count; i++)
        {
            float x = dirtyRect.X + (i * (barWidth + barSpacing));
            float amplitude = _audioSamples[i] * (height / 2) * _amplitude;
            
            // Skip rendering bars outside the visible area
            if (x > dirtyRect.Width + dirtyRect.X || x + barWidth < dirtyRect.X)
                continue;
                
            // Set bar color based on playback position - compare with the sample position
            bool hasBeenPlayed = x <= playbackX + dirtyRect.X;
            canvas.FillColor = hasBeenPlayed ? _playedColor : _waveColor;
            
            // Draw top bar (mirror of bottom bar)
            float barHeight = amplitude;
            canvas.FillRectangle(x, centerY - barHeight, barWidth, barHeight);
            
            // Draw bottom bar
            canvas.FillRectangle(x, centerY, barWidth, barHeight);
        }
    }
    
    /// <summary>
    /// Draws the time scale portion of the visualization.
    /// </summary>
    private void DrawTimeScale(ICanvas canvas, RectF dirtyRect)
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
            float x = dirtyRect.X + (float)(second * _pixelsPerSecond);
            
            // Skip if outside visible area
            if (x < dirtyRect.X - 50 || x > dirtyRect.Right + 50)
                continue;
                
            // Draw second marker (full height tick + text)
            canvas.StrokeColor = _tickColor;
            canvas.StrokeSize = 2;
            canvas.DrawLine(x, dirtyRect.Y, x, dirtyRect.Y + height * 0.5f);
            
            // Add text label for seconds
            canvas.FontSize = 10;
            canvas.FontColor = _textColor;
            canvas.DrawString(second.ToString(), x + 3, dirtyRect.Y + height * 0.3f, HorizontalAlignment.Left);
            
            // Draw half-second marker
            if (second < secondsToShow)
            {
                float halfSecondX = dirtyRect.X + (float)((second + 0.5) * _pixelsPerSecond);
                
                // Skip if outside visible area
                if (halfSecondX < dirtyRect.X - 10 || halfSecondX > dirtyRect.Right + 10)
                    continue;
                    
                canvas.StrokeColor = _tickColor.WithAlpha(0.8f);
                canvas.StrokeSize = 1.5f;
                canvas.DrawLine(halfSecondX, dirtyRect.Y, halfSecondX, dirtyRect.Y + height * 0.4f);
            }
            
            // Draw 1/10 second markers
            for (int tenth = 1; tenth <= 9; tenth++)
            {
                // Skip the half-second mark as we already drew it
                if (tenth == 5)
                    continue;
                    
                float tenthSecondX = dirtyRect.X + (float)((second + tenth * 0.1) * _pixelsPerSecond);
                
                // Skip if outside visible area
                if (tenthSecondX < dirtyRect.X - 10 || tenthSecondX > dirtyRect.Right + 10)
                    continue;
                    
                canvas.StrokeColor = _tickColor.WithAlpha(0.5f);
                canvas.StrokeSize = 1;
                canvas.DrawLine(tenthSecondX, dirtyRect.Y, tenthSecondX, dirtyRect.Y + height * 0.25f);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether this drawable currently has waveform data.
    /// </summary>
    public bool HasWaveformData => _randomSamplesGenerated && _audioSamples.Count > 0;
}