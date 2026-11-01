using Microsoft.Maui.Devices;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Device idiom through Microsoft.Maui.Essentials DeviceInfo: a non-UI platform
/// service (AGENTS.md permits reviewed non-UI Microsoft.Maui.* packages). Width
/// alone cannot tell a landscape phone (852-956 pt) from a portrait iPad.
/// </summary>
internal static class NativeDeviceIdiom
{
    internal static bool IsPhone { get; } = DeviceInfo.Current.Idiom == DeviceIdiom.Phone;
}
