using System.Reflection;
using System.Text.RegularExpressions;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>W3 Dashboard source fidelity on Android (AF-D01, D03, D05, D06, D08, D09, D10).</summary>
public sealed class AndroidDashboardFidelityContractTests
{
    [Fact]
    public void ChooseOwnTilesAreTheSourceIconCardGrid()
    {
        var controls = Source("AndroidSliceShell.Dashboard.Compose.cs");
        Assert.Contains("var compact = WindowWidthDp < 768;", controls);
        // Source 2/3/4 columns at 768 and 992, fewer as the font scale grows
        // (shared NativeAdaptiveLayout, covered by NativeAdaptiveLayoutTests).
        Assert.Contains(
            "var columns = NativeAdaptiveLayout.ActivityColumns(WindowWidthDp, WindowFontScale);",
            controls);
        Assert.Contains("if (compact && NativeDashboardPresenter.ChooseOwnActivities.Length > 6)",
            controls);
        Assert.Contains("ActivityTile(visible[index], canSelect, palette)", controls);
        Assert.Contains("new Icon(ChooseOwnTileIcon(route), null)", controls);
        Assert.Contains("OpenChooseOwnQuiz : () => OpenChooseOwnActivity(route)", controls);
        foreach (var route in NativeDashboardPresenter.ChooseOwnActivities.Select(a => a.Route))
            Assert.Contains($"\"{route}\" => Icons.", controls);
    }

    [Fact]
    public void InsightUsesTheFiveSourceMediaIconsAndLabelledSeverityBadges()
    {
        var compose = Source("AndroidSliceShell.Compose.cs");
        var insight = Source("AndroidSliceShell.Dashboard.Insight.Compose.cs");
        Assert.Contains("new Icon(ResourceMediaIcon(resource.MediaType),", compose);
        foreach (var media in new[] { "\"Video\"", "\"Podcast\"", "\"Text\"", "\"Vocabulary List\"", "_ =>" })
            Assert.Contains(media, insight[insight.IndexOf("ResourceMediaIcon(string? mediaType)",
                StringComparison.Ordinal)..]);
        Assert.Contains("Localize(\"Dashboard_StrugglingWith\"), Icons.Default.Warning", compose);
        Assert.Contains("palette.WarningText, palette, \"struggling\"", compose);
        Assert.Contains("palette.SecondaryText, palette, \"new\"", compose);
        Assert.Contains("Localize(\"Dashboard_DoingWellWith\"), Icons.Default.CheckCircle", compose);
        Assert.Contains("palette.KnownText, palette, \"doing-well\"", compose);
        Assert.Contains("var badges = new FlowRow", insight);
        Assert.Contains("TierBadge(category, accent, palette,", insight);
        Assert.DoesNotContain("CategoryInsight(", compose);
    }

    [Fact]
    public void NumbersAndVocabularySectionsHaveTheirOwnStates()
    {
        var progress = Source("AndroidSliceShell.Dashboard.Progress.Compose.cs");
        Assert.Contains("if (state.StatisticsLoading)", progress);
        // A failed or blocked load without statistics shows its message (shared
        // projection), with the {0} reason filled, never the empty card.
        Assert.Contains(
            "else if (NativeDashboardPresenter.VocabularySectionErrorKey(state) is { } vocabError)",
            progress);
        Assert.Contains("StatusAlert(DashboardErrorText(vocabError)", progress);
        Assert.Contains("\"native.dashboard.vocab.error\"", progress);
        Assert.Contains("Localize(NativeText.UnexpectedError)", progress);
        Assert.Contains("StatusAlert(DashboardErrorText(state.ErrorKey),",
            Source("AndroidSliceShell.Compose.cs"));
        Assert.Contains("Localize(\"Dashboard_NoVocabData\")", progress);
        Assert.Contains("if (!state.NumbersApplicable && state.NumberErrorKey is null)", progress);
        Assert.Contains("Localize(\"NativeDashboard_NumbersMastery\")", progress);
        Assert.Contains("Localize(\"NativeDashboard_NumbersEmptyTitle\")", progress);
        Assert.Contains("Localize(\"NativeDashboard_NumbersEmptyDescription\")", progress);
        Assert.Contains("if (number.DueCount > 0)", progress);
        Assert.Contains("Localize(\"NativeDashboard_NumbersDue\")", progress);
        Assert.Contains("Localize(\"NativeDashboard_NumbersCorrect\")", progress);
        Assert.DoesNotContain("{Localize(\"Activity_NumberDrill\")} — ",
            Source("AndroidSliceShell.Compose.cs"));
        Assert.DoesNotContain("DueNow", progress);
    }

    [Fact]
    public void QuickStartCreatesTheSourceStarterSkillOnlyAfterANewStarter()
    {
        var wiring = Source("AndroidSliceShell.Resources.cs");
        Assert.Contains("if (created.Result == NativeResourceMutationResult.Success)\n"
            + "                        await _dashboard.EnsureStarterSkillAsync(cancellationToken);",
            wiring);
    }

    [Fact]
    public void NewDashboardIconsSupplyTheModifierTheImageVectorBridgeRequires()
    {
        var sources = Source("AndroidSliceShell.Dashboard.Compose.cs")
            + Source("AndroidSliceShell.Dashboard.Insight.Compose.cs")
            + Source("AndroidSliceShell.Dashboard.Progress.Compose.cs");
        var icons = Regex.Matches(sources, @"new Icon\(").Count;
        Assert.True(icons >= 2);
        Assert.Equal(icons, Regex.Matches(sources,
            @"new Icon\((?:(?!new Icon\().)*?\)\s*\{\s*Modifier\s*=\s*Modifier\.Size\(",
            RegexOptions.Singleline).Count);
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidDashboardFidelityContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
