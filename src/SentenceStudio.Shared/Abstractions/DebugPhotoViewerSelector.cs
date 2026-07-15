#if DEBUG
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Abstractions;

/// <summary>
/// DEBUG-only prototype selector that reads a preference key to choose between
/// "webview" (current Razor overlay) and "native" (future platform viewer) without
/// source edits. Invalid or missing values deterministically select the Razor/WebView path.
///
/// Release builds never instantiate this class — see conditional registration in
/// CoreServiceExtensions.
/// </summary>
public sealed class DebugPhotoViewerSelector : IPhotoViewerService
{
    /// <summary>
    /// Preference key read from <see cref="IPreferencesService"/>.
    /// Set to "native" to route through the native prototype; any other value
    /// (including missing/empty/invalid) selects the current WebView/Razor path.
    /// </summary>
    public const string PreferenceKey = "debug_photo_viewer_prototype";

    /// <summary>Valid value that selects the native prototype.</summary>
    public const string NativeValue = "native";

    /// <summary>Valid value that selects the WebView/Razor path (default).</summary>
    public const string WebViewValue = "webview";

    private readonly IPreferencesService _preferences;
    private readonly IPhotoViewerService _nativeImplementation;
    private readonly ILogger<DebugPhotoViewerSelector> _logger;

    public DebugPhotoViewerSelector(
        IPreferencesService preferences,
        ILogger<DebugPhotoViewerSelector> logger,
        IPhotoViewerService? nativeImplementation = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Native implementation is optional — when not registered, native path is unavailable
        _nativeImplementation = nativeImplementation ?? new DefaultPhotoViewerService();
    }

    public async Task<PhotoViewerResult> ShowAsync(PhotoViewerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedPrototype = _preferences.Get(PreferenceKey, WebViewValue);

        if (string.IsNullOrWhiteSpace(selectedPrototype) ||
            !string.Equals(selectedPrototype, NativeValue, StringComparison.OrdinalIgnoreCase))
        {
            // Default / invalid / "webview" → decline native, use Razor overlay
            _logger.LogDebug(
                "DebugPhotoViewerSelector: prototype='{Prototype}' → using WebView/Razor path",
                selectedPrototype ?? "(empty)");
            return new PhotoViewerResult(HandledByNative: false);
        }

        _logger.LogDebug("DebugPhotoViewerSelector: prototype='native' → attempting native presentation");
        return await _nativeImplementation.ShowAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
#endif
