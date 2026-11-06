using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// The Library's YouTube import page (source MediaImport.razor) on the native iOS head: a
/// browse page on the Library stack drawn in SwiftUI, released with the Library, blocked
/// (nothing shown) when the owner changes, and localized in English and Korean.
/// </summary>
public sealed class NativeYouTubeImportContractTests
{
    [Fact]
    public void ThePageIsABrowsePageOnTheLibraryStack()
    {
        var scene = Source("App/ApplicationScene.YouTube.cs");
        Assert.Contains("stack.Push(ApplicationYouTubeRoutes.YouTubeImport, page.WithTabBarHidden(false));", scene);
        Assert.Contains("if (stack.NavigationPath.Count != 0)\n                return;", scene);
        Assert.Contains("_app.OnYouTubeImportPopped();", scene);
        var library = Source("App/ApplicationScene.Library.cs");
        Assert.Contains("UpdateLibraryYouTube(appearanceChanged);", library);
        Assert.Contains("OnYouTubeRoutesChanged(routes);", library);
        Assert.Contains("DropYouTubeReferences();", library);

        var controller = Source("App/ApplicationController.cs");
        Assert.Contains("|| IsYouTubeImportNavigationBlocked", controller);
        // Released on sign-out and on dispose, and when a Study entry returns the Library to its list.
        Assert.Equal(2, Regex.Matches(controller, @"ReleaseYouTubeImport\(\);").Count);
        Assert.Contains("ReleaseYouTubeImport();", Source("App/ApplicationController.Library.cs"));
        Assert.Contains("_youTubeImportToasts?.Dispose();", controller);
    }

    [Fact]
    public void TheViewIsSwiftUIAndShowsNothingOnceBlocked()
    {
        foreach (var file in new[] { "Features/Import/YouTubeImportView.cs",
            "App/ApplicationController.YouTube.cs", "App/ApplicationScene.YouTube.cs" })
        {
            var source = Source(file);
            Assert.DoesNotContain("UIKit", source);
            Assert.DoesNotContain("UIView", source);
            Assert.DoesNotContain("WKWebView", source);
        }
        var view = Source("Features/Import/YouTubeImportView.cs");
        // A blocked page draws the tab group and the reason, never a tab's content.
        var blocked = view.IndexOf("if (state.Blocked)", StringComparison.Ordinal);
        Assert.InRange(blocked, 0, view.IndexOf("items.AddRange(state.Tab switch", StringComparison.Ordinal));
        // The link's Return fetches (source HandleUrlKeyDown), and typing updates Fetch in place.
        Assert.Contains(".OnSubmit(() => _ = import.FetchTranscriptsAsync()))", view);
        Assert.Contains("SetAvailability(fetch, style, import.CanFetch);", view);
        // The bridge refuses an unnamed spinner; one in a redraw would fault the action that drew it.
        Assert.DoesNotContain("IndeterminateProgress(string.Empty)", view);
        // The channel card's switch draws alone; its text is the accessible name (bridge preview.42).
        Assert.Contains(".WithLabelsHidden()", view);
        // Source: the channel column shows from the md breakpoint, the page's Add Channel button too.
        Assert.Equal(2, Regex.Matches(view, @"style\.ViewportWidth >= 768").Count);
    }

    [Fact]
    public void TheChannelPageIsAnEditorAboveTheYouTubePage()
    {
        var scene = Source("App/ApplicationScene.YouTube.cs");
        Assert.Contains("stack.Push(ApplicationYouTubeRoutes.YouTubeChannel,\n                    WithSamToolbar(stack, route, _youTubeChannelSlot));", scene);
        Assert.Contains(".WithTabBarHidden();", scene);
        Assert.Contains("_app.OnYouTubeChannelPopped();", scene);
        // Save or Cancel decides how it closes: a Study entry shows it instead of closing it.
        Assert.Contains("|| IsImportWordOpen || IsYouTubeChannelOpen;", Source("App/ApplicationController.Library.cs"));
        var controller = Source("App/ApplicationController.YouTube.cs");
        Assert.Contains("|| _youTubeChannel?.IsNavigationBlocked == true;", controller);
        Assert.Contains("ReleaseYouTubeChannel();\n        _youTubeImportToasts?.Reset();", controller);
        var view = Source("Features/Import/YouTubeChannelView.cs");
        Assert.DoesNotContain("IndeterminateProgress(string.Empty)", view);
        // The bridge's picker refuses an empty value: "-- Select Language --" has a placeholder one.
        Assert.Contains("new(NoLanguage, text(\"ChannelDetail_SelectLanguage\"))", view);
        Assert.DoesNotContain("new(string.Empty,", view);
        Assert.DoesNotContain("UIKit", view);
    }

    [Fact]
    public void EveryColorIsASixDigitHex()
    {
        // The bridge refuses any other color (an #RRGGBBAA "transparent" fill faulted the history
        // and the channel page's redraws); an outline button leaves its fill unset instead.
        foreach (var file in new[] { "Features/Import/YouTubeImportView.cs", "Features/Import/YouTubeChannelView.cs" })
        {
            var source = Source(file);
            foreach (Match color in Regex.Matches(source, "\"#[0-9A-Fa-f]*\""))
                Assert.Matches("^\"#[0-9A-Fa-f]{6}\"$", color.Value);
            Assert.DoesNotContain("WithRoundedSurface(\"#00000000\"", source);
        }
        Assert.Contains("YouTubeImportView.OutlineWarningButton(label,", Source("Features/Import/YouTubeChannelView.cs"));
    }

    [Fact]
    public void BadgesAndShortButtonsKeepTheirWidthBesideATruncatedTitle()
    {
        // A SwiftUI row gives each item an equal share, which wrapped "Failed" and "Retry" mid-word
        // on iPhone; source badges are nowrap and the title truncates (bridge preview.43).
        var import = Source("Features/Import/YouTubeImportView.cs");
        var channel = Source("Features/Import/YouTubeChannelView.cs");
        Assert.Equal(2, Regex.Matches(import, @"\.WithFixedSize\(\)").Count); // status badge, Retry
        Assert.Equal(2, Regex.Matches(channel, @"\.WithFixedSize\(\)").Count); // badge, Select All
        Assert.Contains("SourceTextRole.Body2).WithLineLimit(1).WithFlexibleWidth()", channel);
    }

    [Fact]
    public void TheTextIsLocalizedInEnglishAndKorean()
    {
        var keys = Regex.Matches(Source("Features/Import/YouTubeImportView.cs")
                + Source("Features/Import/YouTubeChannelView.cs")
                + File.ReadAllText(Path.Combine(RepositoryPath,
                    "src/SentenceStudio.Native/Features/Import/YouTubeChannelCoordinator.cs"))
                + Source("App/ApplicationController.YouTube.cs")
                + File.ReadAllText(Path.Combine(RepositoryPath,
                    "src/SentenceStudio.Native/Features/Import/YouTubeImportCoordinator.cs")),
                "\"((?:Import|YouTubeImport|YouTubeChannel|ChannelDetail|Common)_[A-Za-z0-9_]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.Contains("YouTubeImport_FetchTranscriptsError", keys);
        foreach (var key in keys)
            foreach (var culture in new[] { "", ".ko" })
                Assert.True(HasValue(culture, key), $"{key} is missing from AppResources{culture}.resx.");
        // The new keys say they are Korean drafts.
        var korean = XDocument.Load(Path.Combine(RepositoryPath,
            "src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx"));
        Assert.All(korean.Root!.Elements("data")
                .Where(data => ((string?)data.Attribute("name")) is { } name
                    && (name.StartsWith("YouTubeImport_") || name.StartsWith("YouTubeChannel_"))),
            data => Assert.Contains("Korean draft", (string?)data.Element("comment") ?? string.Empty));
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
