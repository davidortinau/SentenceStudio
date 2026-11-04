using Android.Content.Res;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Source Settings text size (85-150%, AppearanceSelection.FontScale) on Android.
/// The Compose facade has no LocalDensity provider, so the app multiplies the
/// system font scale in the activity's own resource configuration and lets the
/// Compose host re-read its density (AndroidComposeView.onConfigurationChanged).
/// Every sp text size follows it, as with the system font-size setting; dp layout
/// does not change. No view is created or replaced.
/// </summary>
internal static class AndroidFontScale
{
    /// <summary>The system font scale, without the app's text-size multiplier.</summary>
    internal static float SystemScale =>
        global::Android.Content.Res.Resources.System?.Configuration?.FontScale is > 0f and var scale
            ? scale : 1f;

    internal static float Effective(double appScale) =>
        (float)Math.Round(SystemScale * appScale, 3);

    /// <summary>Applies the scale when it differs from the activity's; true when it changed.</summary>
    internal static bool Apply(global::Android.App.Activity activity, double appScale)
    {
        var resources = activity.Resources;
        var current = resources?.Configuration;
        if (resources is null || current is null)
            return false;
        var target = Effective(appScale);
        if (Math.Abs(current.FontScale - target) < 0.001f)
            return false;
        var updated = new Configuration(current) { FontScale = target };
#pragma warning disable CS0618, CA1422 // Resources.UpdateConfiguration: the only in-place update for an attached host.
        resources.UpdateConfiguration(updated, resources.DisplayMetrics);
#pragma warning restore CS0618, CA1422
        activity.Window?.DecorView?.DispatchConfigurationChanged(updated);
        return true;
    }
}
