using System.Diagnostics.CodeAnalysis;

namespace SentenceStudio.Abstractions;

/// <summary>
/// Immutable, validated request to present a photo in fullscreen.
/// </summary>
public sealed record PhotoViewerRequest
{
    /// <summary>
    /// The absolute URI of the image to display.
    /// </summary>
    public required Uri ImageUri { get; init; }

    /// <summary>
    /// Optional accessibility label describing the image content.
    /// </summary>
    public string? AccessibilityLabel { get; init; }

    /// <summary>
    /// Creates a validated <see cref="PhotoViewerRequest"/>.
    /// Returns false when the URI is null, relative, or uses an unsupported scheme.
    /// </summary>
    public static bool TryCreate(
        string? imageUri,
        string? accessibilityLabel,
        [NotNullWhen(true)] out PhotoViewerRequest? request)
    {
        request = null;

        if (string.IsNullOrWhiteSpace(imageUri))
            return false;

        if (!Uri.TryCreate(imageUri, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != "https" && uri.Scheme != "http")
            return false;

        request = new PhotoViewerRequest
        {
            ImageUri = uri,
            AccessibilityLabel = accessibilityLabel
        };
        return true;
    }
}

/// <summary>
/// Result of a native photo viewer presentation attempt.
/// </summary>
/// <param name="HandledByNative">
/// True if a native viewer successfully presented the image.
/// False means the caller should fall back to the Razor/CSS overlay.
/// </param>
public readonly record struct PhotoViewerResult(bool HandledByNative);

/// <summary>
/// Platform abstraction for fullscreen photo presentation.
/// Native implementations present the image using platform-native controls;
/// the default/web implementation declines so the existing Razor overlay is used.
/// </summary>
public interface IPhotoViewerService
{
    /// <summary>
    /// Attempts to present a photo in a native fullscreen viewer.
    /// </summary>
    /// <param name="request">Validated presentation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result indicating whether native presentation handled the request.
    /// Implementations MUST NOT swallow exceptions as a false success — if presentation
    /// was attempted and failed, the exception must propagate.
    /// </returns>
    Task<PhotoViewerResult> ShowAsync(PhotoViewerRequest request, CancellationToken cancellationToken = default);
}
