using System.Reflection;
using System.Text.RegularExpressions;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidDashboardContractTests
{
    [Fact]
    public void DashboardHasFixedNativeBarAndOnlyRealMenuActions()
    {
        var visual = Source("AndroidSliceShell.Visual.cs");
        var compose = Source("AndroidSliceShell.Compose.cs");
        var shell = Source("AndroidSliceShell.cs");

        Assert.Contains("new TopAppBar", visual);
        Assert.Contains("new DropdownMenu(", visual);
        Assert.Contains("new DropdownMenuItem(", visual);
        Assert.Contains("RegeneratePlan();", visual);
        Assert.Contains("SignOut();", visual);
        Assert.Contains("_dashboard.RegenerateAsync(cancellationToken)", shell);
        Assert.Contains("_auth.SignOutAsync(cancellationToken)", shell);
        Assert.Contains("DashboardAppBar(palette),\n            viewport", compose);
        Assert.Contains(".VerticalScroll(_dashboardScroll)", compose);
        Assert.Contains("SelectedPlanMode(palette)", compose);
        Assert.Contains("AddVocabularySummary(viewport, vocabulary, palette)", compose);
        Assert.Contains("_preview && Destination is not (NativeDestination.Dashboard",
            compose);
        Assert.Contains("viewport.Add(StatusAlert(\"Debug preview - isolated sample",
            compose);
        Assert.Contains("viewport.Add(StatusAlert(Error.Value, palette, error: true",
            compose);
        Assert.DoesNotContain("Dashboard_ChooseMyOwn", compose);
        Assert.DoesNotContain("BottomNavigation", compose);
    }

    [Fact]
    public void StudyInsightIsAccessibleAccordionWithPersistedSourceFields()
    {
        var compose = Source("AndroidSliceShell.Compose.cs");
        var shell = Source("AndroidSliceShell.cs");

        Assert.Contains("private bool _insightExpanded;", shell);
        Assert.Contains("native.plan.insight.toggle", compose);
        Assert.Contains(".Clickable(() => Publish(() => _insightExpanded = !_insightExpanded))",
            compose);
        Assert.Contains("semantics.StateDescription(", compose);
        Assert.Contains("Dashboard_InsightExpanded", compose);
        Assert.Contains("Dashboard_InsightCollapsed", compose);
        Assert.Contains("Icons.Default.KeyboardArrowUp", compose);
        Assert.Contains("Icons.Default.KeyboardArrowDown", compose);
        Assert.Contains("if (expanded)", compose);
        Assert.Contains("narrative.Story", compose);
        Assert.Contains("resource.SelectionReason", compose);
        Assert.Contains("vocab.TotalDue", compose);
        Assert.Contains("narrative.FocusAreas", compose);
        Assert.DoesNotContain("Preview plan vocabulary", compose);
        Assert.Contains("Narrative = new PlanNarrative(", Source("DebugPreviewFixture.cs"));
    }

    [Fact]
    public void PlanCardsUseActualMetadataAndOnlyQuizCardsAreClickable()
    {
        var compose = Source("AndroidSliceShell.Compose.cs");
        var theme = Source("AndroidVisualTheme.cs");

        Assert.Contains("card.ResourceTitle", compose);
        Assert.Contains("card.MinutesSpent", compose);
        Assert.Contains("card.IsCompleted", compose);
        Assert.Contains("canOpen && !Busy.Value", compose);
        Assert.Contains("modifier.Clickable(() => OpenQuiz(card.Id))", compose);
        Assert.Contains("if (canOpen)", compose);
        Assert.Contains("Icons.AutoMirrored.Default.KeyboardArrowRight", compose);
        Assert.Contains("MaterialTheme.IsSystemInDarkTheme(composer)", compose);
        Assert.Contains("ThemeCatalog.Default.PaletteFor(dark ?", theme);
        Assert.Contains("Color.FromHex(dark ? \"#FFD77A\" : \"#855900\")", theme);
    }

    [Fact]
    public void DashboardIconsSupplyModifiersRequiredByTheComposeImageVectorBridge()
    {
        var sources = Source("AndroidSliceShell.Compose.cs")
            + Source("AndroidSliceShell.Visual.cs");
        Assert.Equal(8, Regex.Matches(sources, @"new Icon\(").Count);
        Assert.Equal(8, Regex.Matches(sources,
            @"Modifier = Modifier\.Size\(").Count);
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidDashboardContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
