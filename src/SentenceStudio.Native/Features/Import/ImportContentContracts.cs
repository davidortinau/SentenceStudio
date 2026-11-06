using SentenceStudio.Services;

namespace SentenceStudio.Native.Features.Import;

/// <summary>An owner's editable resource that an import can add to (source: the existing-resource select).</summary>
public sealed record NativeImportTargetResource(string Id, string Title, string? Language);

/// <summary>
/// A committed import. <see cref="Result"/> is null when the device owner changed while the
/// rows were written: they belong to the owner the commit ran for, and the result is not for
/// display to whoever owns the device now (the commit is not reported as refused either).
/// </summary>
public sealed record NativeImportCommitted(ContentImportResult? Result);

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

    /// <summary>
    /// Source CommitImportAsync. Refused (nothing written) unless the owner is current before
    /// the write; after it, a changed owner gets a committed outcome with no result to show.
    /// </summary>
    Task<NativeImportCommitted> CommitAsync(
        string owner, ContentImportCommit commit, CancellationToken cancellationToken);

    /// <summary>
    /// Source LoadAvailableResources: the owner's resources that are not smart resources,
    /// ordered by title.
    /// </summary>
    Task<IReadOnlyList<NativeImportTargetResource>> ListTargetResourcesAsync(
        string owner, CancellationToken cancellationToken);
}
