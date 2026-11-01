using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Copy for Sam messages through Microsoft.Maui.Essentials Clipboard: a non-UI
/// platform service (AGENTS.md permits reviewed non-UI Microsoft.Maui.* packages;
/// the iOS head already references Microsoft.Maui.Essentials with no MAUI Controls).
/// No app-authored platform UI is used here.
/// </summary>
internal static class NativeClipboard
{
    internal static Task SetTextAsync(string text) => Clipboard.Default.SetTextAsync(text);
}
