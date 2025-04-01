using Microsoft.Maui.Graphics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// A custom drawable that renders an audio waveform visualization.
/// </summary>
public class WaveformDrawable : IDrawable
{
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
    private bool _autoGenerateWaveform = true; // Default to auto-generating waveform
    private int _sampleCount = 400; // Default sample count for random waveforms

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
    /// </summary>
    public float TotalWidth => (float)(_audioDuration * _pixelsPerSecond);

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
        float centerY = height / 2;
        
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
        canvas.DrawLine(0, centerY, width, centerY);
        
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
            float x = i * (barWidth + barSpacing);
            float amplitude = _audioSamples[i] * centerY * _amplitude;
            
            // Skip rendering bars outside the visible area
            if (x > dirtyRect.Width + dirtyRect.X || x + barWidth < dirtyRect.X)
                continue;
                
            // Set bar color based on playback position - compare with the sample position
            bool hasBeenPlayed = x <= playbackX;
            canvas.FillColor = hasBeenPlayed ? _playedColor : _waveColor;
            
            // Draw top bar (mirror of bottom bar)
            float barHeight = amplitude;
            canvas.FillRectangle(x, centerY - barHeight, barWidth, barHeight);
            
            // Draw bottom bar
            canvas.FillRectangle(x, centerY, barWidth, barHeight);
        }
    }

    /// <summary>
    /// Gets a value indicating whether this drawable currently has waveform data.
    /// </summary>
    public bool HasWaveformData => _randomSamplesGenerated && _audioSamples.Count > 0;
}