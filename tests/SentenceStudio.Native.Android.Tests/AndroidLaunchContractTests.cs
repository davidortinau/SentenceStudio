using System.Reflection;
using System.Text.RegularExpressions;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// The app icon and launch screen match the MAUI head (MauiIcon and MauiSplashScreen in
/// SentenceStudio.MauiCompatibility.csproj: studio_icon.png on #0c0b10), and the launch look
/// stays until sign-in is resolved, as on iOS (25fef602).
/// </summary>
public sealed class AndroidLaunchContractTests
{
    [Fact]
    public void IconAndSplashUseTheMauiArtworkAndColor()
    {
        var colors = Text("Resources/values/colors.xml");
        Assert.Contains("<color name=\"launch_background\">#0c0b10</color>", colors);
        Assert.Contains("<color name=\"app_icon_background\">#0c0b10</color>", colors);
        // The Compose launch look draws the same color as the system splash (no flash at handoff).
        Assert.Contains("Color.FromHex(\"#0c0b10\")", Text("AndroidSliceShell.Launch.cs"));
        foreach (var styles in new[] { "Resources/values/styles.xml", "Resources/values-night/styles.xml" })
        {
            var style = Text(styles);
            Assert.Contains("android:windowSplashScreenBackground\">@color/launch_background", style);
            Assert.Contains("android:windowSplashScreenAnimatedIcon\">@drawable/launch_icon", style);
        }
        var launchIcon = Text("Resources/drawable/launch_icon.xml");
        Assert.Contains("android:width=\"108dp\" android:height=\"108dp\" android:gravity=\"center\"", launchIcon);
        Assert.Contains("@mipmap/appicon_foreground", launchIcon);
        var adaptive = Text("Resources/mipmap-anydpi-v26/appicon.xml");
        Assert.Contains("@color/app_icon_background", adaptive);
        Assert.Contains("@mipmap/appicon_foreground", adaptive);
        // The artwork is opaque edge to edge: a monochrome layer would make a blank themed icon.
        Assert.DoesNotContain("<monochrome", adaptive);
        foreach (var density in new[] { "mdpi", "hdpi", "xhdpi", "xxhdpi", "xxxhdpi" })
            Assert.True(File.Exists(Path.Combine(Root, "Resources", $"mipmap-{density}", "appicon_foreground.png")), density);
        foreach (var manifest in new[] { "Properties/AndroidManifest.Debug.xml", "Properties/AndroidManifest.Release.xml" })
        {
            var text = Text(manifest);
            Assert.Contains("android:icon=\"@mipmap/appicon\"", text);
            Assert.Contains("android:roundIcon=\"@mipmap/appicon\"", text);
        }
    }

    [Fact]
    public void LaunchLookStaysUntilSignInIsResolvedAndAlwaysEnds()
    {
        var shell = Text("AndroidSliceShell.cs");
        var compose = Text("AndroidSliceShell.Compose.cs");
        Assert.Contains("if (_launching)\n        {\n            theme.Add(RenderLaunch());", compose);
        // Before any shell content, so Sign In is never drawn first.
        Assert.True(compose.IndexOf("if (_launching)", StringComparison.Ordinal)
            < compose.IndexOf("var content = new Column", StringComparison.Ordinal));
        var start = shell.IndexOf("private async Task InitializeNormalAsync(", StringComparison.Ordinal);
        var initialize = shell[start..shell.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        Assert.Matches(new Regex(@"finally\s*\{[^}]*Publish\(EndLaunch\);", RegexOptions.Singleline), initialize);
        var from = shell.IndexOf("private async Task HandleAuthAsync(", StringComparison.Ordinal);
        var handle = shell[from..shell.IndexOf("\n    }\n", from, StringComparison.Ordinal)];
        Assert.True(handle.IndexOf("Destination = AndroidRoutePolicy.AfterAuth(state);", StringComparison.Ordinal)
            < handle.IndexOf("EndLaunch();", StringComparison.Ordinal));
        var launch = Text("AndroidSliceShell.Launch.cs");
        Assert.Contains("TestTag(\"native.launch\")", launch);
        Assert.Contains("semantics.ContentDescription(", launch);
        Assert.Contains("native.launch.progress", launch);
    }

    private static string Text(string name) => File.ReadAllText(Path.Combine(Root, name)).Replace("\r\n", "\n");

    private static string Root => typeof(AndroidLaunchContractTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
}
