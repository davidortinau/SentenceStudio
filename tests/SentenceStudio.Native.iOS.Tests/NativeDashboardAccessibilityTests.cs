using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native.iOS.Platform;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeDashboardAccessibilityTests
{
    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void CompletedCard_AnnouncesTitleMetadataAndLocalizedStatusOnce(string resourceFile)
    {
        var resources = Path.GetFullPath(Path.Combine(HeadPath,
            "../SentenceStudio.Shared/Resources/Strings", resourceFile));
        var values = XDocument.Load(resources).Descendants("data");
        var completed = values
            .Single(item => item.Attribute("name")?.Value == "PlanSummary_Completed")
            .Element("value")!.Value;
        Assert.False(string.IsNullOrWhiteSpace(completed));

        var label = NativeDashboardCardAccessibility.Label(
            "Vocabulary Review", "Preview vocabulary · ~6 min", completed);

        Assert.Equal(
            $"Vocabulary Review. Preview vocabulary · ~6 min. {completed}",
            label);
        Assert.Equal(1, label.Split(completed, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CardWithoutCompletedStatus_DoesNotAnnounceOne()
    {
        Assert.Equal("Vocabulary Review. ~6 min",
            NativeDashboardCardAccessibility.Label("Vocabulary Review", "~6 min", null));
        Assert.Equal("Vocabulary Review. Completed",
            NativeDashboardCardAccessibility.Label("Vocabulary Review", "", "Completed"));
    }

    [Fact]
    public void ExternalActivityCard_DoesNotAnnounceAnInventedUnavailableCaption()
    {
        Assert.Equal("Reading. ~10 min",
            NativeDashboardCardAccessibility.Label("Reading", "~10 min", null));
    }

    [Fact]
    public void InsightDoingWellTier_AnnouncesTagAndAccuracyWithoutColorAlone()
    {
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features/Dashboard/PlanInsightView.cs"));

        Assert.Contains("state.DoingWellCategories", feature);
        Assert.Contains("category.AverageAccuracy:P0", feature);
        Assert.Contains("CategoryRow(", feature);
        Assert.Contains("native.plan.insight.doingwell", feature);
        Assert.DoesNotContain("NativeDashboard_DoingWell", feature);
    }

    [Fact]
    public void SwiftUICard_UsesVisibleLocalizedStatusOnItsComposedAccessibilityElement()
    {
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features/Dashboard/PlanCardView.cs"));

        Assert.Contains("NativeDashboardCardAccessibility.Label(", feature);
        Assert.Contains("card.IsCompleted ? text(\"PlanSummary_Completed\") : null",
            feature);
        Assert.DoesNotContain("text(\"NativeDashboard_ActivityNotAvailable\")", feature);
        Assert.Contains("Views.Button(content,", feature);
        Assert.Contains(".WithAccessibilityLabel(accessibility)", feature);
        Assert.Contains("native.plan.card.completed", feature);
        Assert.DoesNotContain("native.plan.card.unavailable", feature);
    }

    private static string HeadPath => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
