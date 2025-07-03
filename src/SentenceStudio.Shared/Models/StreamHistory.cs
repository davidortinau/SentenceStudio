using SQLite;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents an audio stream and associated phrase with waveform data for visualization
/// </summary>
public partial class StreamHistory : ObservableObject
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
    public string? Phrase { get; set; }

    /// <summary>
    /// The audio stream containing the spoken phrase - not stored in database
    /// </summary>
    [Ignore]
    public Stream? Stream { get; set; }

    /// <summary>
    /// Waveform data extracted from the audio stream for visualization - not stored in database
    /// </summary>
    [Ignore]
    public float[]? WaveformData { get; set; }

    /// <summary>
    /// The duration of the audio in seconds
    /// </summary>
    [ObservableProperty]
    private double duration;

    /// <summary>
    /// Whether the waveform data has been analyzed yet - not stored in database
    /// </summary>
    [Ignore]
    public bool IsWaveformAnalyzed => WaveformData != null && WaveformData.Length > 0;

    /// <summary>
    /// The ID of the voice used to generate this audio
    /// </summary>
    [ObservableProperty]
    private string? voiceId;

    /// <summary>
    /// Path to the audio file on disk (for persistence between app sessions)
    /// </summary>
    [ObservableProperty]
    private string? audioFilePath;
    
    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Last updated timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    [ObservableProperty]
    private string? fileName;
    
    [ObservableProperty]
    private string? title;
    
    [ObservableProperty]
    private string? source;
    
    [ObservableProperty]
    private string? sourceUrl;
}
