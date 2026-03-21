using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// A YouTube channel the user monitors for new video content.
/// </summary>
[Table("MonitoredChannel")]
public partial class MonitoredChannel : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string? UserProfileId { get; set; }

    /// <summary>Full canonical URL of the channel (e.g. https://www.youtube.com/@handle).</summary>
    [ObservableProperty]
    private string? channelUrl;

    /// <summary>Display name of the channel.</summary>
    [ObservableProperty]
    private string? channelName;

    /// <summary>YouTube handle (e.g. @KoreanLessons).</summary>
    [ObservableProperty]
    private string? channelHandle;

    /// <summary>YouTube internal channel ID (e.g. UCxxxxxxxx).</summary>
    [ObservableProperty]
    private string? youTubeChannelId;

    /// <summary>When we last checked this channel for new videos.</summary>
    public DateTime? LastCheckedAt { get; set; }

    /// <summary>Whether the background worker should poll this channel.</summary>
    [ObservableProperty]
    private bool isActive = true;

    /// <summary>How often to check for new videos (hours). Default 6.</summary>
    [ObservableProperty]
    private int checkIntervalHours = 6;

    /// <summary>Target language of the channel content (e.g. "Korean").</summary>
    [ObservableProperty]
    private string? language;

    [JsonIgnore]
    public DateTime CreatedAt { get; set; }

    [JsonIgnore]
    public DateTime UpdatedAt { get; set; }

    // Navigation — imports spawned from this channel
    [JsonIgnore]
    public List<VideoImport> VideoImports { get; set; } = new();
}
