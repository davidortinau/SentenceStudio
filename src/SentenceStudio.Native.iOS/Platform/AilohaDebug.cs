#if AILOHA_DEBUG
using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace SentenceStudio.Native.iOS.Platform;

internal static class AilohaDebug
{
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void Send(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendTag(
        IntPtr receiver, IntPtr selector, IntPtr root, IntPtr placeholder, IntPtr identifier);

    private static IntPtr ShimClass =>
        GetClass("SentenceStudioAilohaDebug") is var type && type != IntPtr.Zero
            ? type
            : throw new InvalidOperationException("The official Ailoha Debug shim was not loaded.");

    internal static void Start() => Send(ShimClass, Selector.GetHandle("start"));
    internal static void Stop() => Send(ShimClass, Selector.GetHandle("stop"));

    internal static void TagInput(UIView root, string placeholder, string identifier)
    {
        using var nativePlaceholder = new NSString(placeholder);
        using var nativeIdentifier = new NSString(identifier);
        SendTag(ShimClass, Selector.GetHandle("tagInput:placeholder:identifier:"),
            root.Handle, nativePlaceholder.Handle, nativeIdentifier.Handle);
    }
}
#endif
