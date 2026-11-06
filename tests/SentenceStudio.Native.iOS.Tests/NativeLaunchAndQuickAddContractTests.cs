using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// The launch look while a remembered session is restored, and the Library's quick-add
/// grid (Captain, 2026-11-06).
/// </summary>
public sealed class NativeLaunchAndQuickAddContractTests
{
    [Fact]
    public void LaunchShowsTheLaunchLookUntilSignInIsResolved()
    {
        var scene = Source("App/ApplicationScene.cs");
        Assert.Contains("var target = _app.IsLaunching ? RootKind.Launch : _app.Destination switch", scene);
        Assert.Contains("Views.VStack(Views.AssetImage(\"LaunchLogo\"))", scene);
        // The Info.plist launch screen's color (one constant, equal to the color asset) and image.
        Assert.Contains(".WithBackgroundColor(LaunchBackgroundHex)", scene);
        Assert.Equal(1, scene.Split("#0C0B10").Length - 1);
        Assert.Contains("internal const string LaunchBackgroundHex = \"#0C0B10\";", scene);
        var colorset = File.ReadAllText(Path.Combine(HeadPath,
            "Resources/Assets.xcassets/LaunchBackground.colorset/Contents.json"));
        Assert.Contains("\"red\" : \"0x0C\"", colorset);
        Assert.Contains("\"green\" : \"0x0B\"", colorset);
        Assert.Contains("\"blue\" : \"0x10\"", colorset);
        Assert.Contains(".WithIgnoresSafeArea()", scene);
        var plist = File.ReadAllText(Path.Combine(HeadPath, "Info.plist"));
        Assert.Contains("<string>LaunchLogo</string>", plist);
        Assert.Contains("<string>LaunchBackground</string>", plist);

        var controller = Source("App/ApplicationController.cs");
        Assert.Contains("private bool _launching = true;", controller);
        // A remembered session keeps the launch look until Study's first load starts (Study
        // then shows that load), and the load runs busy so Refresh and the mode switch wait.
        var route = controller[controller.IndexOf("private async Task RouteAfterAuthAsync()", StringComparison.Ordinal)..];
        route = route[..route.IndexOf("RefreshSamAvailability();", StringComparison.Ordinal)];
        Assert.DoesNotContain("Publish();", route);
        var load = route[route.IndexOf("if (_dashboard is not null)", StringComparison.Ordinal)..];
        Assert.True(load.IndexOf("_launching = false;", StringComparison.Ordinal)
            < load.IndexOf("_dashboard.LoadAfterSignInAsync", StringComparison.Ordinal));
        Assert.Contains("_busy = true;", load);
        Assert.Contains("_busy = wasBusy;", load);
        Assert.True(controller.Split("_launching = false;").Length - 1 >= 4,
            "Every way out of Start ends the launch look.");
    }

    [Fact]
    public void QuickAddOpensTheExistingCreateFlowsBelowTheLibraryList()
    {
        var library = Source("App/ApplicationScene.Library.cs");
        Assert.Contains("if (QuickAddGrid() is { } quickAdd)", library);
        Assert.Contains("Views.Section(_app.Localize(\"Library_QuickAdd\"), grid)", library);
        Assert.Contains("$\"native.library.quickadd.{item.Id}\"", library);
        Assert.Contains(".WithButtonStyle(SwiftButtonStyle.Plain)", library);
        Assert.Contains("NativeDeviceIdiom.IsPhone || _style.ViewportWidth < 600 ? 2 : Math.Clamp(count, 2, 4)", library);

        var controller = Source("App/ApplicationController.Library.cs");
        Assert.Contains("new(\"resource\", \"doc.badge.plus\", \"Resources_AddResource\",", controller);
        Assert.Contains("() => OpenLibraryQuickAdd(OpenResourceCreate)", controller);
        Assert.Contains("() => OpenLibraryQuickAdd(() => OpenSkills(create: true))", controller);
        Assert.Contains("() => OpenLibraryQuickAdd(() => OpenLibraryPairs(create: true))", controller);
        // Built once; only flows that exist have a button (no placeholders).
        Assert.Contains("LibraryQuickAddItems => _libraryQuickAddItems ??=", controller);
        Assert.DoesNotContain("ImportContentReady", controller);
        Assert.DoesNotContain("#pragma warning disable CS0162", controller);
        Assert.DoesNotContain("_libraryPairsCreateRequested", controller);
        Assert.Contains("_ = OpenLibraryPairsOutsideBusyAsync(screen, launch, create);", controller);
        // Import content opens its native page; YouTube import joins when its flow exists.
        Assert.Contains("new(\"import-content\", \"square.and.arrow.down\", \"Nav_ImportContent\",", controller);
        Assert.Contains("() => OpenLibraryQuickAdd(OpenImportContent)", controller);
        Assert.DoesNotContain("youtube-import", controller);
        Assert.Contains("internal void OpenImportContent()",
            Source("App/ApplicationController.Import.cs"));

        foreach (var key in new[] { "Library_QuickAdd", "Resources_AddResource", "Skills_AddSkillProfile",
            "MinimalPairs_CreatePair" })
            foreach (var culture in new[] { "", ".ko" })
                Assert.True(HasValue(culture, key), $"{key} is missing from AppResources{culture}.resx.");
    }

    [Fact]
    public void AnActivityWhoseOpenThrowsShowsTheHostsErrorWithGoBack()
    {
        var host = Source("App/ApplicationController.Activities.cs");
        Assert.Contains("_activityOpenFailure = screen;", host);
        Assert.Contains("? ActivityOpenFailureView.Render(style, Text, ExitActivity)", host);
        Assert.Contains("!HasActivityOpenFailed && _activity?.TryUpdateInPlace(style) == true", host);
        Assert.Contains("_activityOpenFailure = null;", host);
        var screens = Source("Features/Activities/ActivityScreens.cs");
        // One shared fallback; no per-screen empty default.
        Assert.DoesNotContain("void OnOpenFailed()", screens);
        Assert.Contains("\"native.activity.open-failed.go-back\"", screens);
        Assert.Contains("text(\"Common_GoBack\")", screens);
    }

    [Fact]
    public void SamSlotCancelStopsThatChecksRequest()
    {
        var library = Source("App/ApplicationController.Library.cs");
        Assert.Contains("private CancellationTokenSource? _samSlotCheck;", library);
        Assert.Contains("check.CancelAfter(SamSlotCheckLimit);", library);
        Assert.Contains("answer = await sam.RefreshAvailabilityAsync(token);", library);
        var reset = library[library.IndexOf("private void ResetSamSlot()", StringComparison.Ordinal)..];
        reset = reset[..reset.IndexOf("}", StringComparison.Ordinal)];
        Assert.Contains("check?.Cancel();", reset);
        // An unexpected failure answers "not reachable" instead of a silent reset.
        Assert.Contains("ShowShellNotice(SamSlotText(NativeSamAvailabilityText.Unreachable));", library);
        // Recent lookups that failed before opening say so over the page the learner is on.
        Assert.Contains("ShowShellNotice(Text(\"Lookups_LoadFailed\"));",
            Source("App/ApplicationController.Lookups.cs"));
    }

    private static bool HasValue(string culture, string key)
    {
        var path = Path.Combine(RepositoryPath,
            $"src/SentenceStudio.Shared/Resources/Strings/AppResources{culture}.resx");
        return XDocument.Load(path).Root!.Elements("data").Any(data =>
            (string?)data.Attribute("name") == key
            && !string.IsNullOrWhiteSpace(data.Element("value")?.Value));
    }

    private static string RepositoryPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "..", ".."));

    private static string HeadPath => Path.Combine(RepositoryPath, "src", "SentenceStudio.Native.iOS");

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(HeadPath, relativePath));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
