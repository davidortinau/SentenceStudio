namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// What the system says about itself, read from UIKit as a non-UI platform service (AGENTS.md
/// permits those): whether VoiceOver is running, the system's light or dark style, and the
/// device's version and model. Nothing here draws. Presentation code asks through this type so
/// that it names no UIKit type itself.
/// </summary>
internal static class NativeSystemState
{
    /// <summary>Whether VoiceOver is running now. Read on each call: it changes while the app runs.</summary>
    internal static bool IsVoiceOverRunning => UIKit.UIAccessibility.IsVoiceOverRunning;

    /// <summary>
    /// Whether the system's style is dark, or null off the main thread, where the trait
    /// collection may not be read.
    /// </summary>
    internal static bool? IsSystemStyleDark => Foundation.NSThread.IsMain
        ? UIKit.UITraitCollection.CurrentTraitCollection.UserInterfaceStyle == UIKit.UIUserInterfaceStyle.Dark
        : null;

    /// <summary>The system's version, as in "26.1".</summary>
    internal static string SystemVersion => UIKit.UIDevice.CurrentDevice.SystemVersion;

    /// <summary>The device's model, as in "iPhone" or "iPad".</summary>
    internal static string Model => UIKit.UIDevice.CurrentDevice.Model;
}
