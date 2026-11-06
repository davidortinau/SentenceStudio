using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>W3 Dashboard source fidelity on iOS (AF-D01, D02, D08, D10, D11, AF-N08).</summary>
public sealed class NativeSwiftUIDashboardFidelityContractTests
{
    [Fact]
    public void ModeSwitchShowsInEveryReturningLearnerPhase()
    {
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        var page = dashboard[dashboard.IndexOf("internal static SwiftView Page(",
                StringComparison.Ordinal)
            ..dashboard.IndexOf("internal static SwiftView WithToolbar(",
                StringComparison.Ordinal)];
        var switcher = page.IndexOf("var switcher = Views.ButtonGroup([",
            StringComparison.Ordinal);
        Assert.True(switcher > page.IndexOf("if (state.IsNewUser)", StringComparison.Ordinal));
        Assert.True(switcher < page.IndexOf("if (state.Phase is (NativePhase.Ready or NativePhase.Empty))",
            StringComparison.Ordinal));
        Assert.DoesNotContain("if (state.Phase is NativePhase.Ready or NativePhase.Empty)\n        {\n            var switcher",
            page);
    }

    [Fact]
    public void WelcomeHeadingUsesTheNameReadOnEachLoad()
    {
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        Assert.Contains("var name = state.ProfileName ?? profileName;", dashboard);
        Assert.Contains("text(\"Dashboard_WelcomeGreeting\"), name)", dashboard);
    }

    [Fact]
    public void VocabularySectionHasItsOwnLoadingAndShowsAFailedLoadOnlyWithoutStatistics()
    {
        var progress = Source("Features/Dashboard/DashboardLearningProgressView.cs");
        Assert.Contains("if (state.StatisticsLoading)", progress);
        Assert.DoesNotContain("state.Phase == NativePhase.Loading", progress);
        // Loaded statistics win over a plan error; a failed or blocked load without
        // statistics shows its message (shared projection), not the empty card.
        var summary = progress.IndexOf("else if (state.VocabSummary is { } summary)",
            StringComparison.Ordinal);
        var error = progress.IndexOf(
            "else if (NativeDashboardPresenter.VocabularySectionErrorKey(state) is { } vocabError)",
            StringComparison.Ordinal);
        var empty = progress.IndexOf("text(\"Dashboard_NoVocabData\")", StringComparison.Ordinal);
        Assert.True(summary > 0 && summary < error && error < empty);
        Assert.Contains("Views.Text(DashboardView.DashboardErrorText(vocabError, text))",
            progress);
        var app = Source("App/ApplicationController.cs");
        Assert.Contains("_dashboard.StateChanged += OnDashboardProgress;", app);
        var entries = Source("App/ApplicationController.DashboardEntries.cs");
        Assert.Contains("if (_disposed || state.Phase != NativePhase.Loading)", entries);
        Assert.Contains("_dashboard?.State is not { Phase: NativePhase.Loading } current", entries);
    }

    [Fact]
    public void DoingWellTierHasAVisibleLocalizedHeading()
    {
        var insight = Source("Features/Dashboard/PlanInsightView.cs");
        Assert.Contains("text(\"Dashboard_DoingWellWith\")", insight);
        Assert.Contains("_ => \"checkmark.circle\"", insight);
        foreach (var resource in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var strings = XDocument.Load(Path.GetFullPath(Path.Combine(HeadPath,
                "../SentenceStudio.Shared/Resources/Strings", resource)));
            Assert.Contains(strings.Descendants("data"), data =>
                (string?)data.Attribute("name") == "Dashboard_DoingWellWith"
                && !string.IsNullOrWhiteSpace(data.Element("value")?.Value));
        }
    }

    [Fact]
    public void LibraryOffersTheSourceMinimalPairsEntryAfterVocabularyAndSkills()
    {
        // Source NavMenu Minimal Pairs: the Library switcher's Pairs page (tab shell trial).
        var library = Source("App/ApplicationScene.Library.cs");
        Assert.Contains("\"native.library.segment.pairs\"", library);
        Assert.True(library.IndexOf("\"native.library.segment.vocabulary\"", StringComparison.Ordinal)
            < library.IndexOf("\"native.library.segment.pairs\"", StringComparison.Ordinal));
        var entries = Source("App/ApplicationController.Library.cs");
        Assert.Contains("_dashboard.SelectNavigationEntry()", entries);
        Assert.Contains(".FromChooseOwnAsync(selection, NativeActivityCatalog.MinimalPairs, _lifetime.Token)",
            entries);
        Assert.Contains("ActivityScreens.Create(launch.Route, new ActivityScreenHost(", entries);
    }

    [Fact]
    public void QuickStartCreatesTheSourceStarterSkillOnlyAfterANewStarter()
    {
        var app = Source("App/ApplicationController.cs");
        Assert.Contains("if (created.Result == NativeResourceMutationResult.Success)\n"
            + "                    await _dashboard.EnsureStarterSkillAsync(_lifetime.Token);",
            app);
    }

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string Source(string name) => File.ReadAllText(Path.Combine(
        HeadPath, name));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
