using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Native.iOS.App;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Recent lookups on iOS (Captain 2026-11-04, decision 4 B): a SwiftUI page pushed above the
/// Vocabulary list from its toolbar, on the shared coordinator, with an automation ID on every
/// control. Behavior is covered by NativeLookupHistoryTests on the shared coordinator.
/// </summary>
public sealed class NativeRecentLookupsContractTests
{
    [Fact]
    public void VocabularyToolbarOpensRecentLookups_PushedAboveTheListAndPoppedByBack()
    {
        var list = Source("Features/Vocabulary/VocabularyListView.cs");
        Assert.Contains("RecentLookupsAccess.Open", list);
        Assert.Contains("text(\"Lookups_Title\")", list);
        Assert.Contains("OpenLookups = OpenRecentLookups", Source("App/ApplicationController.Vocabulary.cs"));

        var scene = Source("App/ApplicationScene.Vocabulary.cs");
        // Pushed with the Sam entry like every other signed-in screen (W1, decision 6).
        Assert.Contains("stack.Push(ApplicationFormRoutes.RecentLookups,\n"
            + "                WithSamToolbar(stack, ApplicationFormRoutes.RecentLookups, _lookupsSlot));", scene);
        // A browse list one level above the Library's Vocabulary page keeps the tab bar.
        var lookups = scene[scene.IndexOf("_lookupsSlot = Views.VStack(", StringComparison.Ordinal)..];
        lookups = lookups[..lookups.IndexOf("stack.Push(", StringComparison.Ordinal)];
        Assert.DoesNotContain("WithTabBarHidden", lookups);
        Assert.Contains("_app.OnRecentLookupsPopped();", scene);
        Assert.Equal("recent-lookups", ApplicationFormRoutes.RecentLookups);
    }

    [Fact]
    public void PageIsSwiftUIOnTheSharedCoordinator_WithAnIdOnEveryControl()
    {
        var view = Source("Features/Lookups/RecentLookupsView.cs");
        var controller = Source("App/ApplicationController.Lookups.cs");
        foreach (var source in new[] { view, controller })
        {
            Assert.DoesNotContain("UIKit", source);
            Assert.DoesNotContain("UIView", source);
            Assert.DoesNotMatch(new Regex(@"class\s+RecentLookupsCoordinator\b"), source);
        }
        Assert.Contains("new RecentLookupsCoordinator(", controller);
        Assert.Contains("RecentLookupsCoordinator.SourceLine(", view);
        // Every Button, Menu and the retry control carries an accessibility identifier.
        foreach (Match control in Regex.Matches(view,
                     @"(Views\.(Button|Menu)\(|style\.SecondaryButton\()[\s\S]*?;"))
            Assert.Contains("WithAccessibilityIdentifier(", control.Value);
        Assert.Contains("RecentLookupsAccess.Destination(row, destination)", view);
        Assert.Contains("WithAccessibilityLabel(label)", view);
    }

    /// <summary>
    /// The facade refuses a Menu action that is not a Button (a heading Text left the page on
    /// Loading on the simulator, 2026-11-04). The menu takes only DestinationButtons, and the
    /// real-facade view host (tests/SentenceStudio.Native.SwiftUIViews.Tests, a macOS
    /// executable) builds this view.
    /// </summary>
    [Fact]
    public void AddMenu_HoldsOnlyDestinationButtons_AndTheRealFacadeHostBuildsTheView()
    {
        var view = Source("Features/Lookups/RecentLookupsView.cs");
        Assert.Contains("Views.Menu(face, DestinationButtons(row, add))", view);
        var buttons = view[view.IndexOf("internal static SwiftView[] DestinationButtons(", StringComparison.Ordinal)..];
        Assert.DoesNotMatch(new Regex(@"Views\.(?!Button\()[A-Z][A-Za-z]*\("), buttons);

        var host = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../SentenceStudio.Native.SwiftUIViews.Tests/SentenceStudio.Native.SwiftUIViews.Tests.csproj")));
        Assert.Contains("Features/Lookups/RecentLookupsView.cs", host);
        Assert.Contains("<TargetFramework>net11.0-macos</TargetFramework>", host);
    }

    [Fact]
    public void AutomationIds_AreStableAndSafe()
    {
        Assert.Equal("lk-0a1b", RecentLookupsAccess.Key("lk:0a1b"));
        Assert.Equal("native.vocabulary.lookups.open", RecentLookupsAccess.Open);
    }

    private static string Source(string relativePath) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", relativePath)));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
