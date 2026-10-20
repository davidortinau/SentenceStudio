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
        Assert.Contains("await _dashboard.GenerateAsync(cancellationToken);", shell);
        Assert.Contains("_auth.SignOutAsync(cancellationToken)", shell);
        Assert.Contains("DashboardAppBar(palette),\n            viewport", compose);
        Assert.Contains(".VerticalScroll(_dashboardScroll)", compose);
        Assert.Contains("SelectedPlanMode()", compose);
        Assert.Contains("AddVocabularySummary(viewport, vocabulary, palette)", compose);
        Assert.Contains("state.NumberContexts.IsDefault ? [] : state.NumberContexts",
            compose);
        Assert.Contains("NumberMasteryCard(number, palette)", compose);
        Assert.Contains("_preview && Destination is not (NativeDestination.Dashboard",
            compose);
        Assert.Contains("viewport.Add(StatusAlert(\"Debug preview - isolated sample",
            compose);
        Assert.Contains("viewport.Add(StatusAlert(Error.Value, palette, error: true",
            compose);
        Assert.Contains("Dashboard_ChooseMyOwn", compose);
        Assert.DoesNotContain("BottomNavigation", compose);
    }

    [Fact]
    public void ChooseOwnModeAndSelectorsPersistOnlyForTheAuthenticatedOwner()
    {
        var composition = Source("AndroidSliceShell.Compose.cs");
        var controls = Source("AndroidSliceShell.Dashboard.Compose.cs");
        var selection = Source("AndroidSliceShell.Dashboard.cs");

        Assert.Contains("native.dashboard.mode.choose", composition);
        Assert.Contains("SelectDashboardMode(true)", composition);
        Assert.Contains("_dashboard.SetMode(mode)", selection);
        Assert.Contains("HasDashboardOwner(Auth.UserId)", selection);
        Assert.Contains("_dashboard.LoadChooseOwnAsync(cancellationToken)", selection);
        Assert.Contains("_dashboard.SelectChooseOwn(", selection);
        Assert.Contains("_dashboard.SetChooseOwnExpanded(", selection);
        Assert.Contains("private readonly MutableState<int> _chooseSelector",
            controls);
        Assert.Contains("if (_chooseSelector.Value != (int)ChooseSelector.None && chooseMode)",
            composition);
        Assert.Contains("_chooseSelector.Value = (int)ChooseSelector.Resources", controls);
        Assert.Contains("_chooseSelector.Value = (int)ChooseSelector.Skills", controls);
        Assert.Contains("new LazyColumn<NativeChooseOwnOption>", controls);
        Assert.Contains("choose.SelectedResourceIds", controls);
        Assert.Contains("NativeDashboardPresenter.ChooseOwnActivities", controls);
        Assert.Contains("choose.ShowAllActivities", controls);
        Assert.Contains("Dashboard_SeeAll", controls);
        Assert.Contains("Dashboard_ShowLess", controls);
        Assert.Contains("native.dashboard.choose.activity.", controls);
        Assert.Contains("NativeResources_Unavailable", controls);
        Assert.DoesNotContain("OpenQuiz(", controls);
    }

    [Fact]
    public void ConfirmedNewLearnerShowsSourceEntryChoicesButNoUnsafeStarterAction()
    {
        var compose = Source("AndroidSliceShell.Compose.cs");
        var visual = Source("AndroidSliceShell.Visual.cs");
        var start = compose.IndexOf("if (!_preview && state.IsNewUser)",
            StringComparison.Ordinal);
        var modes = compose.IndexOf("viewport.Add(SelectedPlanMode())",
            start, StringComparison.Ordinal);
        Assert.True(start >= 0 && modes > start);
        Assert.Contains("viewport.Add(RenderNewLearner(palette))",
            compose[start..modes]);
        Assert.Contains("return new Column", compose[start..modes]);
        var welcome = compose[compose.IndexOf(
            "private static ComposableNode RenderNewLearner(",
            StringComparison.Ordinal)..compose.IndexOf(
            "private static ComposableNode NumberMasteryCard(",
            StringComparison.Ordinal)];
        Assert.Contains("Dashboard_NewUserTitle", welcome);
        Assert.Contains("Dashboard_NewUserDescription", welcome);
        Assert.Contains("Dashboard_QuickStart", welcome);
        Assert.Contains("Dashboard_CreateYourOwn", welcome);
        Assert.Contains("native.dashboard.starter.unavailable", welcome);
        Assert.Contains("native.dashboard.create-own.unavailable", welcome);
        Assert.DoesNotContain("CreateStarterAsync", welcome);
        Assert.DoesNotContain("Action(", welcome);
        Assert.Contains("Dashboard_Welcome", visual);
        Assert.Contains("if (!Dashboard.IsNewUser || _preview)", visual);
    }

    [Fact]
    public void StaleChooseOwnSelectionKeepsOptionsVisibleButDisablesActionsUntilRetry()
    {
        var controls = Source("AndroidSliceShell.Dashboard.Compose.cs");
        var commands = Source("AndroidSliceShell.Dashboard.cs");

        Assert.Contains("content.Add(StatusAlert(Localize(error)", controls);
        Assert.Contains("var canSelect = choose.ErrorKey is null && !Busy.Value",
            controls);
        Assert.Contains("native.dashboard.choose.resources", controls);
        Assert.Contains("native.dashboard.choose.skill", controls);
        Assert.Contains("native.dashboard.choose.retry", controls);
        Assert.Contains("enabled: Dashboard.ChooseOwn is { ErrorKey: null }",
            controls);
        Assert.Contains("Dashboard.ChooseOwn is not { ErrorKey: null } selection",
            commands);
        Assert.Contains("new LazyColumn<NativeChooseOwnOption>", controls);
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
        Assert.Contains("native.plan.insight.resource.", compose);
        Assert.Contains("resource.Id}.unavailable", compose);
        Assert.Contains("vocab.TotalDue", compose);
        Assert.Contains("state.StrugglingCategories", compose);
        Assert.Contains("state.NewCategories", compose);
        Assert.Contains("state.DoingWellCategories", compose);
        Assert.Contains("narrative.FocusAreas", compose);
        Assert.DoesNotContain("Preview plan vocabulary", compose);
        Assert.Contains("Narrative = new PlanNarrative(", Source("DebugPreviewFixture.cs"));
    }

    [Fact]
    public void CompletedQuizCardsAreVisiblyUnavailableUntilSharedScoringSupportsReplay()
    {
        var compose = Source("AndroidSliceShell.Compose.cs");
        var theme = Source("AndroidVisualTheme.cs");

        Assert.Contains("card.ResourceTitle", compose);
        Assert.Contains("card.MinutesSpent", compose);
        Assert.Contains("card.IsCompleted", compose);
        Assert.Contains("var canOpen = card.Activity == PlanActivityType.VocabularyReview",
            compose);
        Assert.Contains("&& !card.IsCompleted;", compose);
        Assert.Contains("canOpen && !Busy.Value", compose);
        Assert.Contains("modifier.Clickable(() => OpenQuiz(card.Id))", compose);
        Assert.Contains("if (!canOpen)", compose);
        Assert.Contains("PlanSummary_Completed", compose);
        Assert.Contains("NativeResources_Unavailable", compose);
        Assert.Contains(".unavailable", compose);
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
        var icons = Regex.Matches(sources, @"new Icon\(").Count;
        Assert.True(icons >= 6);
        Assert.Equal(icons, Regex.Matches(sources,
            @"new Icon\((?:(?!new Icon\().)*?\)\s*\{\s*Modifier\s*=\s*Modifier\.Size\(",
            RegexOptions.Singleline).Count);
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidDashboardContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
