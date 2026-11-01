using Microsoft.Maui.ApplicationModel;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Opens an external web link (Video Watching "Open in YouTube") through
/// Microsoft.Maui.Essentials Launcher: a non-UI platform service that hands the
/// URL to the system (the YouTube app or Safari), like the source's
/// target=_blank link. It presents no in-app UI; the iOS head already
/// references Microsoft.Maui.Essentials without MAUI Controls.
/// </summary>
internal static class NativeExternalLauncher
{
    internal static Task<bool> OpenAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return Task.FromResult(false);
        return Launcher.Default.OpenAsync(uri);
    }
}
