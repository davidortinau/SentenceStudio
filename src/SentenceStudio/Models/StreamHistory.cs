namespace SentenceStudio.Models;

using SQLite;
using System.Text.Json.Serialization;

/// <summary>
/// Represents an audio stream and associated phrase with waveform data for visualization
/// </summary>
public class StreamHistory
{
    /// <summary>
    /// Unique identifier for the stream history record
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }

    /// <summary>
    /// The phrase associated with this audio stream
    /// </summary>
    [NotNull]
    public string Phrase { get; set; }

    /// <summary>
    /// The audio stream containing the spoken phrase - not stored in database
    /// </summary>
    [Ignore]
    public Stream Stream { get; set; }

    /// <summary>
    /// Waveform data extracted from the audio stream for visualization - not stored in database
    /// </summary>
    [Ignore]
    public float[] WaveformData { get; set; }

    /// <summary>
    /// The duration of the audio in seconds
    /// </summary>
    public double Duration { get; set; }

    /// <summary>
    /// Whether the waveform data has been analyzed yet - not stored in database
    /// </summary>
    [Ignore]
    public bool IsWaveformAnalyzed => WaveformData != null && WaveformData.Length > 0;

    /// <summary>
    /// The ID of the voice used to generate this audio
    /// </summary>
    public string VoiceId { get; set; }

    /// <summary>
    /// Path to the audio file on disk (for persistence between app sessions)
    /// </summary>
    public string AudioFilePath { get; set; }
    
    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Last updated timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    public string FileName { get; set; }
    public string Title { get; set; }
    public string Source { get; set; }
    public string SourceUrl { get; set; }
}