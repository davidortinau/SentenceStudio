namespace SentenceStudio.Sharing;

public sealed class SharedIngestItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public SharedIngestKind Kind { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string? SourceAppBundleId { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public int SchemaVersion { get; set; } = SharingConstants.CurrentSchemaVersion;
}
