namespace SentenceStudio.Shared.Models;

/// <summary>
/// Not used for JSON deserialization — the transcript cleanup prompt returns plain text.
/// This class exists to document the pipeline stage and provide a typed container
/// when passing cleaned transcripts between service methods.
/// </summary>
public class TranscriptCleanupResult
{
    /// <summary>
    /// The cleaned, properly punctuated Korean transcript.
    /// </summary>
    public string CleanedTranscript { get; set; } = string.Empty;

    /// <summary>
    /// Approximate character count of the original raw transcript.
    /// </summary>
    public int OriginalLength { get; set; }

    /// <summary>
    /// Approximate character count after cleanup.
    /// </summary>
    public int CleanedLength { get; set; }

    /// <summary>
    /// Whether the transcript was chunked for processing (exceeded single-call limits).
    /// </summary>
    public bool WasChunked { get; set; }

    /// <summary>
    /// Number of chunks processed (1 if not chunked).
    /// </summary>
    public int ChunkCount { get; set; } = 1;
}
