using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("SceneImages")]
public partial class SceneImage : ObservableObject
{
    /// <summary>
    /// Text ID, unique on every client, so the row can sync (phase 3,
    /// docs/sync-all-tables-plan.md).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The learner who added the image. Null only for rows saved before images had an owner;
    /// those stay on the device or server that has them and never sync (D3).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UserProfileId { get; set; }
    
    /// <summary>
    /// The URL prefix of a photo stored as a file on the device that took it. Such a row never
    /// leaves that device: the server has no image storage, so no other client could show it
    /// (Captain 2026-11-03, phase 3).
    /// </summary>
    public const string DevicePhotoScheme = "native-scene-image:";

    /// <summary>True for a photo stored on one device (see <see cref="DevicePhotoScheme"/>).</summary>
    public static bool IsDevicePhoto(string? url) =>
        url?.StartsWith(DevicePhotoScheme, StringComparison.Ordinal) == true;

    // Original properties 
    public string? Url { get; set; }
    public string? Description { get; set; }
    
    // Observable property for UI binding
    [ObservableProperty]
    [NotMapped]
    private bool _isSelected;

    // Constructors
    public SceneImage() { }
    
    public SceneImage(string url, string description, bool isSelected = false)
    {
        Url = url;
        Description = description;
        IsSelected = isSelected;
    }
}
