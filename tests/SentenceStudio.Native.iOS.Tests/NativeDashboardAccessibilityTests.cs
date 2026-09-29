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
        var completed = XDocument.Load(resources).Descendants("data")
            .Single(item => item.Attribute("name")?.Value == "PlanSummary_Completed")
            .Element("value")!.Value;
        Assert.False(string.IsNullOrWhiteSpace(completed));

        var label = NativeDashboardCardAccessibility.Label(
            "Vocabulary Review", "Preview vocabulary · ~6 min", completed);

        Assert.Equal($"Vocabulary Review. Preview vocabulary · ~6 min. {completed}", label);
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
    public void UIKitCard_UsesTheVisibleLocalizedStatusOnItsAccessibilityElement()
    {
        var host = File.ReadAllText(Path.Combine(
            HeadPath, "Platform/NativeVisualViewController.cs"));

        Assert.Contains("== \"native.plan.card.completed\")?.Text", host);
        Assert.Contains("LocalizationManager.Instance.GetString(\"PlanSummary_Completed\")", host);
        Assert.Contains("NativeDashboardCardAccessibility.Label(", host);
        Assert.Contains("shell.AccessibilityLabel = accessibilityLabel;", host);
        Assert.Contains("shell.IsAccessibilityElement = true;", host);
    }

    private static string HeadPath => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
