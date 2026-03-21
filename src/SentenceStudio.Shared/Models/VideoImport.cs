using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Tracks a single YouTube video import through the processing pipeline.
/// Created either from a monitored channel poll or a manual paste-URL import.
/// </summary>
[Table("VideoImport")]
public partial class VideoImport : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string? UserProfileId { get; set; }

    /// <summary>YouTube video ID (the 11-char identifier).</summary>
    [ObservableProperty]
    private string? videoId;

    /// <summary>Video title from YouTube metadata.</summary>
    [ObservableProperty]
    private string? videoTitle;

    /// <summary>Full YouTube video URL.</summary>
    [ObservableProperty]
    private string? videoUrl;

    /// <summary>FK to MonitoredChannel (null when imported via manual paste).</summary>
    public string? MonitoredChannelId { get; set; }

    /// <summary>Current pipeline status.</summary>
    [ObservableProperty]
    private VideoImportStatus status = VideoImportStatus.Pending;

    /// <summary>Human-readable error when Status == Failed.</summary>
    [ObservableProperty]
    private string? errorMessage;

    /// <summary>FK to the LearningResource created on completion (null until Completed).</summary>
    public string? LearningResourceId { get; set; }

    /// <summary>Raw transcript text fetched from YouTube.</summary>
    [ObservableProperty]
    private string? rawTranscript;

    /// <summary>Cleaned/polished transcript after AI processing.</summary>
    [ObservableProperty]
    private string? cleanedTranscript;

    /// <summary>Language of the transcript (e.g. "Korean").</summary>
    [ObservableProperty]
    private string? language;

    [JsonIgnore]
    public DateTime CreatedAt { get; set; }

    [JsonIgnore]
    public DateTime? CompletedAt { get; set; }

    // Navigation properties
    [JsonIgnore]
    public MonitoredChannel? MonitoredChannel { get; set; }

    [JsonIgnore]
    public LearningResource? LearningResource { get; set; }
}
