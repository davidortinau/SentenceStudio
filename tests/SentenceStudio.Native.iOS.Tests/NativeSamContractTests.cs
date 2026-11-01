using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Source contracts for the native iOS Sam head: SwiftUI-only presentation,
/// stable native.sam accessibility ids, and en/ko resources for every key the
/// view resolves (Coach_* are reused source keys; NativeSam_* are new).
/// </summary>
public sealed class NativeSamContractTests
{
    private static readonly string[] SamFiles =
    [
        "Features/Sam/SamView.cs",
        "App/ApplicationController.Sam.cs",
        "App/ApplicationScene.Sam.cs",
    ];

    [Fact]
    public void SamPresentationUsesOnlyTheSwiftUIFacade()
    {
        foreach (var file in SamFiles)
        {
            var source = string.Join('\n', Source(file).Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotMatch(@"\bUIKit\b|\bUIView\b|\bUIViewController\b|\bUIPasteboard\b|\bUILabel\b", source);
            Assert.DoesNotContain("Microsoft.Maui", source);
        }
        Assert.Contains("Views.Sheet(tabs, _samStack", Source("App/ApplicationScene.Sam.cs"));
        Assert.Contains("Views.NavigationStack(_samPanelSlot", Source("App/ApplicationScene.Sam.cs"));
        // The chat transcript opens at, and follows, the newest message.
        Assert.Contains("Views.Scroll(_samStreamSlot).WithScrollAnchoredToBottom()",
            Source("App/ApplicationScene.Sam.cs"));
    }

    [Theory]
    [InlineData("native.sam.fab")]
    [InlineData("native.sam.panel")]
    [InlineData("native.sam.composer")]
    [InlineData("native.sam.send")]
    [InlineData("native.sam.counter")]
    [InlineData("native.sam.close")]
    [InlineData("native.sam.size")]
    [InlineData("native.sam.conversations.toggle")]
    [InlineData("native.sam.new")]
    [InlineData("native.sam.suggestion.accept")]
    [InlineData("native.sam.suggestion.reject")]
    [InlineData("native.sam.receipt.undo")]
    [InlineData("native.sam.vocabset.approve")]
    [InlineData("native.sam.vocabset.start")]
    [InlineData("native.sam.status")]
    [InlineData("native.sam.retry")]
    [InlineData("native.sam.stop")]
    public void EveryInteractiveSamSurfaceHasAStableAccessibilityId(string id)
    {
        var all = string.Concat(SamFiles.Select(Source));
        Assert.Contains($"\"{id}", all);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void EveryResourceKeyTheSamViewResolvesExistsInEnglishAndKorean(string file)
    {
        var names = XDocument.Load(Path.Combine(ResourcesPath, file)).Descendants("data")
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Element("value")?.Value))
            .Select(entry => entry.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
        var source = string.Concat(SamFiles.Select(Source))
            + File.ReadAllText(Path.Combine(HeadPath, "../SentenceStudio.Native/Sam/NativeSamPresenter.cs"))
            + File.ReadAllText(Path.Combine(HeadPath, "../SentenceStudio.Native/Sam/NativeSamPresenter.Turns.cs"));
        var literal = Regex.Matches(source, "\"((?:Coach|NativeSam|Common)_[A-Za-z_]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(key => !key.EndsWith('_'))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(literal);
        var composed = Enum.GetNames<CoachAnswerBlockKind>().Where(kind => kind != "Answer")
                .Select(kind => "Coach_AnswerBlock_" + kind)
            .Concat(Enum.GetNames<CoachLimitationCode>().Where(code => code != "Unknown")
                .Select(code => "Coach_Limitation_" + code))
            .Concat(Enum.GetNames<CoachRouteName>().Where(route => route != "Unknown")
                .Select(route => "Coach_Route_" + route))
            .Concat(Enum.GetNames<CoachRouteSideEffect>().Select(effect => "Coach_RouteEffect_" + effect))
            .Concat(Enum.GetNames<CoachHintKind>().Where(kind => kind != "Unknown")
                .Select(kind => "Coach_Hint_" + kind))
            .Concat(new[] { CoachAlternativeCode.RemoveOneListAtATime, CoachAlternativeCode.StartAFreshList,
                    CoachAlternativeCode.TakeAShorterSession, CoachAlternativeCode.UseHintLadder }
                .Select(code => "Coach_Alternative_" + code));
        var missing = literal.Concat(composed).Where(key => !names.Contains(key)).ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void NewSamStringsNeverHardCodeTheCoachName()
    {
        foreach (var file in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var values = XDocument.Load(Path.Combine(ResourcesPath, file)).Descendants("data")
                .Where(entry => entry.Attribute("name")!.Value.StartsWith("NativeSam_", StringComparison.Ordinal))
                .Select(entry => entry.Element("value")!.Value)
                .ToArray();
            Assert.NotEmpty(values);
            Assert.All(values, value =>
            {
                Assert.DoesNotContain("Sam", value, StringComparison.Ordinal);
                Assert.DoesNotContain("쌤", value, StringComparison.Ordinal);
            });
        }
    }

    /// <summary>
    /// Every path whose destination becomes Sign In drops Sam (cancels the run,
    /// clears the transcript) inside the same method, so nothing of the previous
    /// owner survives behind the sign-in screen.
    /// </summary>
    [Theory]
    [InlineData("App/ApplicationController.cs")]
    [InlineData("App/ApplicationController.Quiz.cs")]
    public void EveryRouteToSignInClearsSam(string file)
    {
        var lines = Source(file).Split('\n');
        var checkedRoutes = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s+_destination = NativeDestination\.SignIn;"))
                continue;
            checkedRoutes++;
            var cleared = false;
            for (var j = i - 1; j >= 0; j--)
            {
                if (lines[j].Contains("ClearSamForAccountBoundary();", StringComparison.Ordinal))
                {
                    cleared = true;
                    break;
                }
                if (Regex.IsMatch(lines[j], @"^    (private|internal|public)\b"))
                    break;
            }
            Assert.True(cleared, $"{file}:{i + 1} routes to Sign In without clearing Sam.");
        }
        Assert.True(checkedRoutes > 0);
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(HeadPath, relative));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string ResourcesPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Shared/Resources/Strings"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
