using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Features;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeDashboardContractTests
{
    [Fact]
    public void OnlyTheImplementedDashboardIsTopLevel_NoDeadTabs()
    {
        Assert.Equal(NativeDestination.Dashboard,
            Assert.Single(IosDashboardNavigation.TopLevelDestinations));
        Assert.False(IosDashboardNavigation.UsesBottomTabs);
        Assert.DoesNotContain(NativeDestination.TodaysPlan,
            IosDashboardNavigation.TopLevelDestinations);
        Assert.DoesNotContain(NativeDestination.VocabularyQuiz,
            IosDashboardNavigation.TopLevelDestinations);
    }

    [Fact]
    public void PinnedUIKitBarHasRefreshAndWorkingOverflowActions()
    {
        var feature = Source("Features/NativeFeatureViews.cs");
        var host = Source("Platform/NativeVisualViewController.cs");
        var controller = Source("NativeAppController.cs");
        var scene = Source("SceneDelegate.cs");

        Assert.Contains("WithAccessibilityIdentifier(\"native.dashboard.refresh\")", feature);
        Assert.Contains("new UINavigationController(_hostingController)", scene);
        Assert.Contains("RootViewController = _navigationController", scene);
        Assert.DoesNotContain("RootViewController = controller", scene);
        Assert.Contains("if (id == \"native.dashboard.refresh\")", host);
        Assert.Contains("NavigationItem.RightBarButtonItems = refresh is null", host);
        Assert.Contains("new UIBarButtonItem(more)", host);
        Assert.Contains("more.Menu = UIMenu.Create(menuItems.ToArray())", host);
        Assert.Contains("more.ShowsMenuAsPrimaryAction = true", host);
        Assert.Contains("\"native.dashboard.regenerate\"", host);
        Assert.Contains("\"native.dashboard.sign-out\"", host);
        Assert.Contains("more.WidthAnchor.ConstraintEqualTo(", host);
        Assert.Contains("more.Enabled = !busy", host);
        Assert.Contains("button.Enabled = !_dashboardBusy", host);
        Assert.Contains("button.WidthAnchor.ConstraintEqualTo(", host);
        Assert.Contains("dashboard.Phase == NativePhase.Ready && regenerate is not null", host);
        Assert.Contains("signOut: () => Submit(SignOutAsync)", controller);
        Assert.Contains("_auth.SignOutAsync(_lifetime.Token)", controller);
        Assert.Contains("preferences.Remove(UserProfileRepository.ActiveProfileIdKey)",
            controller);
        Assert.Contains("scope.SetActiveUser(null)", controller);
        Assert.Contains("_dashboard!.RegenerateAsync(_lifetime.Token)", controller);
        Assert.Contains("new UIScrollView", host);
        Assert.Contains("new UIStackView([_scroll, _footer])", host);
        Assert.Contains("View.SafeAreaLayoutGuide.TopAnchor", host);
        Assert.Contains("View.KeyboardLayoutGuide.UsesBottomSafeArea = false", host);
        Assert.Contains("root.BottomAnchor.ConstraintEqualTo(View.KeyboardLayoutGuide.TopAnchor)", host);
        Assert.Contains("BackgroundColor = NativeVisualTheme.Background", host);
        Assert.Contains("View!.BackgroundColor = NativeVisualTheme.Background", host);
        Assert.DoesNotContain("statusChrome", host);
        Assert.DoesNotContain("Dashboard_ChooseMyOwn", feature);
        Assert.DoesNotContain("UITabBarController", host);
    }

    [Fact]
    public void StudyInsightHasAccessibleCollapsedAndExpandedNativeStates()
    {
        var feature = Source("Features/NativeFeatureViews.cs");
        var host = Source("Platform/NativeVisualViewController.cs");
        var controller = Source("NativeAppController.cs");

        Assert.Contains("private bool _insightExpanded;", controller);
        Assert.Contains("if (insightExpanded)", feature);
        Assert.Contains("WithAccessibilityIdentifier(\"native.plan.insight.toggle\")",
            feature);
        Assert.Contains("button.AccessibilityValue = LocalizationManager.Instance.GetString(",
            host);
        Assert.Contains("button.AccessibilityTraits = UIAccessibilityTrait.Button", host);
        Assert.Contains("button.AccessibilityLabel = node.Text", host);
        Assert.Contains("var shell = id == \"native.plan.insight\"", host);
        Assert.Contains("var header = content.ArrangedSubviews.FirstOrDefault() as UIButton", host);
        Assert.Contains("sections.AddArrangedSubview(content)", host);
        Assert.Contains("UserInteractionEnabled = false", host);
        Assert.Contains("_insightExpanded ? \"chevron.up\" : \"chevron.down\"", host);
        Assert.Contains("narrative.Story", feature);
        Assert.Contains("resource.SelectionReason", feature);
        Assert.Contains("vocab.TotalDue", feature);
        Assert.Contains("narrative.FocusAreas", feature);
        Assert.Contains("toggleInsight: () => {", controller);
        Assert.DoesNotContain("Preview plan vocabulary", feature);

        foreach (var resource in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var path = Path.GetFullPath(Path.Combine(HeadPath,
                "../SentenceStudio.Shared/Resources/Strings", resource));
            var keys = XDocument.Load(path).Descendants("data")
                .Select(data => data.Attribute("name")?.Value).ToArray();
            Assert.Contains("Dashboard_InsightExpanded", keys);
            Assert.Contains("Dashboard_InsightCollapsed", keys);
        }
    }

    [Fact]
    public void PlanCardsUseRealMetadataAndThemeAwareNativeControls()
    {
        var feature = Source("Features/NativeFeatureViews.cs");
        var host = Source("Platform/NativeVisualViewController.cs");
        var theme = Source("Platform/NativeVisualTheme.cs");

        Assert.Contains("card.ResourceTitle", feature);
        Assert.Contains("AddVocabularySummary(items, vocabulary, text)", feature);
        Assert.Contains(
            "VocabularyTile(summary.Known, text(\"Dashboard_VocabKnown\"), \"known\")",
            feature);
        Assert.Contains("card.MinutesSpent", feature);
        Assert.Contains("card.IsCompleted", feature);
        Assert.Contains("card.Activity == PlanActivityType.VocabularyReview", host);
        Assert.Contains("button.TouchUpInside += (_, _) => _openQuiz?.Invoke(card.Id)", host);
        Assert.Contains("UIImage.GetSystemImage(\"chevron.right\")", host);
        Assert.Contains("UIImage.GetSystemImage(\"lightbulb\")", host);
        Assert.Contains("NativeVisualTheme.OnPrimary", host);
        Assert.Contains("ThemeCatalog.Default.PaletteFor(dark ?", theme);
        Assert.Contains("SrgbColor.Parse(dark ? \"#0C1222\" : \"#FFFFFF\")", theme);
    }

    private static string HeadPath => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string Source(string name) => File.ReadAllText(Path.Combine(HeadPath, name));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
