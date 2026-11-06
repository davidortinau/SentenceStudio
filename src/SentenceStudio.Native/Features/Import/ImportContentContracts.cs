using SentenceStudio.Services;

namespace SentenceStudio.Native.Features.Import;

/// <summary>An owner's editable resource that an import can add to (source: the existing-resource select).</summary>
public sealed record NativeImportTargetResource(string Id, string Title, string? Language);

/// <summary>
/// The source Import Content page's calls (ImportContent.razor), each bound to the owner the
/// screen opened for. The production adapter runs the shared <see cref="ContentImportService"/>
/// with the owner-bound AI gateway; an absent or changed owner gets no data and no write.
/// </summary>
public interface INativeContentImport
{
    /// <summary>Source ClassifyContentAsync (the Auto-detect path, before any preview).</summary>
    Task<ContentClassificationResult> ClassifyAsync(
        string owner, string content, CancellationToken cancellationToken);

    /// <summary>Source ParseContentAsync followed by EnrichPreviewWithDuplicateInfoAsync.</summary>
    Task<ContentImportPreview> PreviewAsync(
        string owner, ContentImportRequest request, CancellationToken cancellationToken);

    /// <summary>Source CommitImportAsync.</summary>
    Task<ContentImportResult> CommitAsync(
        string owner, ContentImportCommit commit, CancellationToken cancellationToken);

    /// <summary>
    /// Source LoadAvailableResources: the owner's resources that are not smart resources,
    /// ordered by title.
    /// </summary>
    Task<IReadOnlyList<NativeImportTargetResource>> ListTargetResourcesAsync(
        string owner, CancellationToken cancellationToken);
}
