namespace SentenceStudio.Models;

/// <summary>
/// Represents an audio stream and associated phrase with waveform data for visualization
/// </summary>
public class StreamHistory
{
    /// <summary>
    /// The phrase associated with this audio stream
    /// </summary>
    public string Phrase { get; set; }

    /// <summary>
    /// The audio stream containing the spoken phrase
    /// </summary>
    public Stream Stream { get; set; }

    /// <summary>
    /// Waveform data extracted from the audio stream for visualization
    /// </summary>
    public float[] WaveformData { get; set; }

    /// <summary>
    /// Whether the waveform data has been analyzed yet
    /// </summary>
    public bool IsWaveformAnalyzed => WaveformData != null && WaveformData.Length > 0;
}