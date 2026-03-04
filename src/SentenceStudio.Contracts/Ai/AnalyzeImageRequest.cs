namespace SentenceStudio.Contracts.Ai;

/// <summary>
/// Request for image analysis via chat completion.
/// </summary>
public sealed class AnalyzeImageRequest
{
    /// <summary>
    /// The user prompt describing what to analyze.
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded image data.
    /// </summary>
    public string ImageBase64 { get; set; } = string.Empty;

    /// <summary>
    /// MIME type of the image (e.g. "image/png", "image/jpeg").
    /// </summary>
    public string MediaType { get; set; } = "image/jpeg";
}
