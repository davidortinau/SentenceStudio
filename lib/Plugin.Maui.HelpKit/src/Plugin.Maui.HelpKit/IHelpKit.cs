namespace Plugin.Maui.HelpKit;

/// <summary>
/// Public entry point for the HelpKit library. Resolve via DI after
/// calling <c>builder.AddHelpKit(...)</c>.
/// </summary>
public interface IHelpKit
{
    /// <summary>Presents the help overlay over the current page.</summary>
    Task ShowAsync(CancellationToken ct = default);

    /// <summary>Dismisses the help overlay if shown.</summary>
    Task HideAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears stored chat history for the resolved user (see
    /// <see cref="HelpKitOptions.CurrentUserProvider"/>).
    /// </summary>
    Task ClearHistoryAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs (or re-runs) ingestion of the configured
    /// <see cref="HelpKitOptions.ContentDirectories"/>. Safe to call at
    /// startup; incremental based on content hash + pipeline fingerprint.
    /// </summary>
    Task IngestAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams an answer to <paramref name="question"/>, yielding
    /// partial <see cref="HelpKitMessage"/> instances as tokens arrive.
    /// Final message carries validated citations.
    /// </summary>
    /// <param name="question">User's question.</param>
    /// <param name="conversationId">Optional existing conversation id
    /// to continue; when <c>null</c>, a new conversation is started.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<HelpKitMessage> StreamAskAsync(
        string question,
        string? conversationId = null,
        CancellationToken ct = default);
}

/// <summary>
/// A single message emitted by <see cref="IHelpKit.StreamAskAsync"/>.
/// </summary>
/// <param name="Role">Role name (<c>"user"</c>, <c>"assistant"</c>, or <c>"system"</c>).</param>
/// <param name="Content">Message content (may be partial during streaming).</param>
/// <param name="Citations">Validated citations for the current content.</param>
public sealed record HelpKitMessage(
    string Role,
    string Content,
    IReadOnlyList<HelpKitCitation> Citations);

/// <summary>
/// Citation back to a source document chunk retrieved during RAG.
/// </summary>
/// <param name="SourcePath">Relative path of the source file.</param>
/// <param name="HeadingPath">Optional slash-joined heading breadcrumb (e.g. <c>"Vocabulary/Resetting"</c>).</param>
/// <param name="SectionAnchor">Optional slug anchor within the source file.</param>
public sealed record HelpKitCitation(
    string SourcePath,
    string? HeadingPath,
    string? SectionAnchor);
