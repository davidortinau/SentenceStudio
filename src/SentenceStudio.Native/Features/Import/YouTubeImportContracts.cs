using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Import;

/// <summary>
/// What a saved video becomes (source MediaImport.razor SaveAsResource: a "Video" resource with
/// the video link and the edited transcript).
/// </summary>
public sealed record NativeYouTubeResourceDraft(
    string Title, string MediaUrl, string Transcript, string Language);

/// <summary>
/// A saved video. <see cref="ResourceId"/> is null when the device owner changed while the row
/// was written: it belongs to the owner the save ran for and is not shown to whoever owns the
/// device now (the save is not reported as failed either).
/// </summary>
public sealed record NativeYouTubeSaved(string? ResourceId);

/// <summary>
/// A channel's discovered upload (source ChannelDetail DiscoveredVideo before its status).
/// </summary>
public sealed record NativeYouTubeVideo(string VideoId, string Title, string Url);

/// <summary>
/// A new channel's save (source ChannelMonitorService.AddAsync): the channel is always saved;
/// <see cref="MetadataResolved"/> says whether YouTube resolved it.
/// </summary>
public sealed record NativeYouTubeChannelAdded(
    MonitoredChannel Channel, bool MetadataResolved, string? ResolutionError);

/// <summary>
/// The source Media Import and Channel Detail pages' calls (MediaImport.razor, ChannelDetail.razor),
/// each bound to the owner the screen opened for. The video, its captions, the transcript cleanup
/// and the AI polish run on the device through the shared YouTube services (as on MAUI; the AI goes
/// through the owner-bound API gateway); the resource is written to the owner's device database.
/// Channels and the import history come from the API (/api/channels, /api/imports), which scopes
/// them to the signed-in learner. An absent or changed owner gets no data and no write.
/// </summary>
public interface INativeYouTubeImport
{
    /// <summary>The owner's profile target language (source ProfileRepo.GetAsync().TargetLanguage).</summary>
    Task<string?> GetTargetLanguageAsync(string owner, CancellationToken cancellationToken);

    /// <summary>Source ImportSvc.GetVideoMetadataAsync: the video's title.</summary>
    Task<string?> GetVideoTitleAsync(string owner, string videoUrl, CancellationToken cancellationToken);

    /// <summary>Source ImportSvc.GetAvailableTranscriptsAsync.</summary>
    Task<IReadOnlyList<TranscriptTrack>> GetTracksAsync(
        string owner, string videoUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Source DownloadTranscript: the track's captions, cleaned up by
    /// TranscriptFormattingService.SmartCleanup with the track's language code.
    /// </summary>
    Task<string> DownloadTranscriptAsync(
        string owner, TranscriptTrack track, CancellationToken cancellationToken);

    /// <summary>Source PolishTranscript: TranscriptFormattingService.PolishWithAiAsync.</summary>
    Task<string> PolishAsync(
        string owner, string transcript, string? languageCode, CancellationToken cancellationToken);

    /// <summary>
    /// Source SaveAsResource (LearningResourceRepository.SaveResourceAsync). Refused (nothing
    /// written) unless the owner is current before the write.
    /// </summary>
    Task<NativeYouTubeSaved> SaveResourceAsync(
        string owner, NativeYouTubeResourceDraft draft, CancellationToken cancellationToken);

    /// <summary>Source ChannelMonitorService.GetAllAsync (GET /api/channels).</summary>
    Task<IReadOnlyList<MonitoredChannel>> ListChannelsAsync(string owner, CancellationToken cancellationToken);

    /// <summary>Source ChannelMonitorService.UpdateAsync (PUT /api/channels/{id}).</summary>
    Task UpdateChannelAsync(string owner, MonitoredChannel channel, CancellationToken cancellationToken);

    /// <summary>Source VideoImportPipelineService.GetImportHistoryAsync (GET /api/imports).</summary>
    Task<IReadOnlyList<NativeYouTubeImportRecord>> ListImportsAsync(
        string owner, CancellationToken cancellationToken);

    /// <summary>Source VideoImportPipelineService.RetryImportAsync (POST /api/imports/{id}/retry).</summary>
    Task RetryImportAsync(string owner, string importId, CancellationToken cancellationToken);
}

/// <summary>An import history row as the API returns it (source VideoImport).</summary>
public sealed record NativeYouTubeImportRecord(
    string Id,
    string? VideoId,
    string? VideoTitle,
    string? VideoUrl,
    string? MonitoredChannelId,
    VideoImportStatus Status,
    string? ErrorMessage,
    string? LearningResourceId,
    DateTime CreatedAt,
    DateTime? CompletedAt);
