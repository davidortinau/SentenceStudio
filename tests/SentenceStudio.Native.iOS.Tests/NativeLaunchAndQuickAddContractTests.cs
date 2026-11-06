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
        // The Info.plist launch screen's color and image.
        Assert.Contains(".WithBackgroundColor(\"#0C0B10\")", scene);
        Assert.Contains(".WithIgnoresSafeArea()", scene);
        var plist = File.ReadAllText(Path.Combine(HeadPath, "Info.plist"));
        Assert.Contains("<string>LaunchLogo</string>", plist);
        Assert.Contains("<string>LaunchBackground</string>", plist);

        var controller = Source("App/ApplicationController.cs");
        Assert.Contains("private bool _launching = true;", controller);
        // A remembered session goes straight to Study (its plan loads in place); every other
        // outcome of the restore ends the launch look too.
        var route = controller[controller.IndexOf("private async Task RouteAfterAuthAsync()", StringComparison.Ordinal)..];
        route = route[..route.IndexOf("_dashboardState = await", StringComparison.Ordinal)];
        Assert.Contains("if (_launching)", route);
        Assert.Contains("_launching = false;", route);
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
        // The imports stay off until their native flows exist.
        Assert.Contains("private const bool ImportContentReady = false;", controller);
        Assert.Contains("private const bool YouTubeImportReady = false;", controller);
        Assert.Contains("internal void OpenImportContent()", controller);
        Assert.Contains("internal void OpenYouTubeImport()", controller);

        foreach (var key in new[] { "Library_QuickAdd", "Resources_AddResource", "Skills_AddSkillProfile",
            "MinimalPairs_CreatePair", "Nav_ImportContent", "ImportFromYouTube" })
            foreach (var culture in new[] { "", ".ko" })
                Assert.True(HasValue(culture, key), $"{key} is missing from AppResources{culture}.resx.");
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
