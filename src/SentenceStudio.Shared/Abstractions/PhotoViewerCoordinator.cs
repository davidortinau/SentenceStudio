using Microsoft.Extensions.Logging;

namespace SentenceStudio.Abstractions;

/// <summary>
/// Coordinates native-vs-web photo viewer presentation. Extracted as a pure
/// coordinator so the fallback decision logic is unit-testable without coupling
/// to Razor internals.
/// </summary>
public sealed class PhotoViewerCoordinator
{
    private readonly IPhotoViewerService _service;
    private readonly ILogger<PhotoViewerCoordinator> _logger;

    public PhotoViewerCoordinator(IPhotoViewerService service, ILogger<PhotoViewerCoordinator> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Attempts native photo presentation. Returns true if native handled the request,
    /// false if the caller should use the existing Razor/CSS overlay.
    ///
    /// Cancellation propagates. Native exceptions are logged and converted to a
    /// false return (web fallback) — they are never swallowed silently.
    /// </summary>
    public async Task<bool> TryShowNativeAsync(PhotoViewerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var result = await _service.ShowAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.HandledByNative)
            {
                _logger.LogDebug("Photo viewer: native presentation handled request for {Uri}", request.ImageUri);
            }
            return result.HandledByNative;
        }
        catch (OperationCanceledException)
        {
            throw; // Cancellation always propagates
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Photo viewer: native presentation failed for {Uri} — falling back to web overlay", request.ImageUri);
            return false;
        }
    }
}
