using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("StreamHistory")]
/// <summary>
/// Represents an audio stream and associated phrase with waveform data for visualization
/// </summary>

public partial class StreamHistory : ObservableObject
{
    /// <summary>
    /// Unique identifier for the stream history record
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Owner of this audio history entry. Legacy entries have no reliable per-row
    /// owner evidence and remain unowned until individually verified recovery.
    /// </summary>
    public string? UserProfileId { get; set; }

    /// <summary>
    /// The phrase associated with this audio stream
    /// </summary>
    public string? Phrase { get; set; }

    /// <summary>
    /// The audio stream containing the spoken phrase - not stored in database
    /// </summary>
    [NotMapped]
    public Stream? Stream { get; set; }

    /// <summary>
    /// Waveform data extracted from the audio stream for visualization - not stored in database
    /// </summary>
    [NotMapped]
    public float[]? WaveformData { get; set; }

    /// <summary>
    /// The duration of the audio in seconds
    /// </summary>
    [ObservableProperty]
    private double duration;

    /// <summary>
    /// Whether the waveform data has been analyzed yet - not stored in database
    /// </summary>
    [NotMapped]
    public bool IsWaveformAnalyzed => WaveformData != null && WaveformData.Length > 0;

    /// <summary>
    /// The Id of the voice used to generate this audio
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

    /// <summary>
    /// The file name, inside a client's own AudioCache folder, of a clip that client made again
    /// for this synced entry because the entry's own clip lives on another device (D4). Derived
    /// from the entry ID, so each client finds its copy without changing the shared row: a row
    /// rewritten to one device's file name would send every other device to synthesize again.
    /// </summary>
    public static string RegeneratedClipFileName(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var safe = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        if (safe.Length == 0)
            throw new ArgumentException("An entry ID needs letters or digits.", nameof(id));
        return $"entry_{safe}.mp3";
    }
}
