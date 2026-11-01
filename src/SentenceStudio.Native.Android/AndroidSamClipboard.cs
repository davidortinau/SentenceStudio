using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Copy for Sam messages through Microsoft.Maui.Essentials Clipboard: a non-UI
/// platform service (AGENTS.md permits reviewed non-UI Microsoft.Maui.* packages;
/// its Android closure is Essentials, Graphics and AndroidX Activity/Browser/
/// Credentials/SecurityCrypto, with no MAUI Controls). Matches iOS NativeClipboard.
/// </summary>
internal static class AndroidSamClipboard
{
    internal static Task SetTextAsync(string text) => Clipboard.Default.SetTextAsync(text);
}
