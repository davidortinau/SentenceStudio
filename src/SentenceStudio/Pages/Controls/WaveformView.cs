using Microsoft.Maui.Graphics;
using System.Collections.Generic;
using System.Threading.Tasks;

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
    /// Generates a random waveform with the specified number of samples.
    /// Used for preview purposes until actual audio data is processed.
    /// </summary>
    /// <param name="sampleCount">Number of samples to generate.</param>
    public void GenerateRandomWaveform(int sampleCount = 400)
    {
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
    public void UpdateWaveform(float[] audioData)
    {
        Debug.WriteLine($"Updating waveform with {audioData.Length} samples");
        if (audioData == null || audioData.Length == 0)
            return;
            
        _audioSamples.Clear();
        _audioSamples.AddRange(audioData);
        _randomSamplesGenerated = true;
    }
    
    /// <summary>
    /// Resets the waveform's generated samples, forcing it to create new ones.
    /// Use this when you want to display a different waveform for a new audio.
    /// </summary>
    public void Reset()
    {
        _audioSamples.Clear();
        _randomSamplesGenerated = false;
    }

    /// <summary>
    /// Implements the drawing logic for the waveform.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="dirtyRect">The region that needs to be redrawn.</param>
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        // If we have no samples, generate some random ones so something is visible
        if (_audioSamples.Count == 0 && !_randomSamplesGenerated)
        {
            Debug.WriteLine("No audio samples available, generating random samples for preview");
            GenerateRandomWaveform();
            if (_audioSamples.Count == 0)
            {
                return; // Safety check
            }
        }

        float width = dirtyRect.Width;
        float height = dirtyRect.Height;
        float centerY = height / 2;
        
        // Calculate bar width based on available samples
        float barWidth = width / _audioSamples.Count;
        float barSpacing = Math.Max(1, barWidth * 0.2f);
        barWidth -= barSpacing;
        
        // Adjust bar width for reasonable visuals
        barWidth = Math.Max(2, barWidth);

        // Calculate playback position cutoff
        float playbackX = width * _playbackPosition;

        for (int i = 0; i < _audioSamples.Count; i++)
        {
            float x = i * (barWidth + barSpacing);
            float amplitude = _audioSamples[i] * centerY;
            
            // Set bar color based on playback position
            canvas.FillColor = x <= playbackX ? 
                _playedColor : 
                _waveColor;
            
            // Draw top bar (mirror of bottom bar)
            float barHeight = amplitude;
            canvas.FillRectangle(x, centerY - barHeight, barWidth, barHeight);
            
            // Draw bottom bar
            canvas.FillRectangle(x, centerY, barWidth, barHeight);
        }
    }
}