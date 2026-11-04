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
        "Features/Sam/SamSettingsView.cs",
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
        // Phone / narrow: a sheet over the tabs whose detent follows the size (60% or full
        // height). iPad: a side panel beside the page (source non-modal overlay). The SwiftUI
        // inspector's own compact sheet is never used: it does not follow a detent change.
        var scene = Source("App/ApplicationScene.Sam.cs");
        Assert.Contains("Views.Sheet(tabs, _samSheetHolder, OnSamSheetDismissed)", scene);
        Assert.Contains("Views.Inspector(_samSheet, _samInspectorHolder, _app.DismissSam)", scene);
        Assert.Contains("NativeDeviceIdiom.IsPhone || _style.ViewportWidth < 768", scene);
        Assert.Contains("size == NativeSamPanelSize.Compact ? 0.6 : 1.0", scene);
        Assert.Contains("_samSheet!.WithSheetDetentFraction(detent)", scene);
        // Crossing the breakpoint moves the same stack (chat, draft, focus) between hosts.
        Assert.Contains("(useSheet ? _samSheetHolder! : _samInspectorHolder!).Add(_samStack!)", scene);
        Assert.Contains("field.FocusRequested = true", scene);
        Assert.Contains("UpdateSam();", Source("App/ApplicationScene.cs"));
        // Source widths (app.css .sam-panel--compact 360px / --expanded 520px).
        Assert.Contains("size == NativeSamPanelSize.Compact ? 360 : 520", scene);
        Assert.Contains(".WithInspectorColumnWidth(width, min: width, max: width)", scene);
        // Source J1: compact below 992, expanded at or above it.
        Assert.Contains("_samCompactDefault = width < 992", Source("App/ApplicationController.Sam.cs"));
        Assert.Contains("Views.NavigationStack(_samPanelSlot", Source("App/ApplicationScene.Sam.cs"));
        // The chat transcript opens at, and follows, the newest message.
        Assert.Contains("Views.Scroll(_samStreamSlot).WithScrollAnchoredToBottom()",
            Source("App/ApplicationScene.Sam.cs"));
    }

    /// <summary>
    /// Decision 6 (2026-11-03): Sam on every signed-in screen, as the source MainLayout
    /// mounts it. Tab roots keep the floating entry; every screen pushed on either tab's
    /// NavigationStack gets the navigation bar entry, so none of them can be missed.
    /// </summary>
    [Fact]
    public void EveryPushedSignedInScreenCarriesTheSamToolbarEntry()
    {
        var files = Directory.GetFiles(Path.Combine(HeadPath, "App"), "ApplicationScene*.cs");
        var pushes = files.SelectMany(file => Regex.Matches(File.ReadAllText(file),
                @"\.Push\(([^,]+),\s*([^\n]*)")
            .Select(match => (File: Path.GetFileName(file), Route: match.Groups[1].Value.Trim(),
                Destination: match.Groups[2].Value.Trim())))
            .Where(push => push.Route != "\"sam-conversations\"")
            .ToArray();
        Assert.True(pushes.Length >= 12, $"Expected every pushed screen, found {pushes.Length}.");
        foreach (var push in pushes)
            Assert.True(push.Destination.StartsWith("WithSamToolbar(", StringComparison.Ordinal),
                $"{push.File} pushes {push.Route} without the Sam entry.");
        var scene = Source("App/ApplicationScene.Sam.cs");
        Assert.Contains("SamView.ToolbarEntry(_app.SamState, _app.Localize, _app.SamActions.Open)", scene);
        Assert.Contains("UpdateSamToolbars();", scene);
        Assert.Contains("\"native.sam.toolbar\"", Source("Features/Sam/SamView.cs"));

        var controller = Source("App/ApplicationController.Sam.cs");
        Assert.Contains("destination is NativeDestination.Dashboard or NativeDestination.VocabularyQuiz\n"
            + "            or NativeDestination.QuizSummary or NativeDestination.Activity", controller);
        Assert.Contains("IsSamShellDestination(_destination) && !_preview", controller);
        Assert.Contains("internal bool IsSamPresented => IsSamShellDestination(_destination)", controller);
        // Start review away from the Dashboard root asks to close the screen instead of doing nothing.
        Assert.Contains("_sam?.ReportVocabularyReviewBlockedByScreen();", controller);
    }

    [Theory]
    [InlineData("native.sam.fab")]
    [InlineData("native.sam.toolbar")]
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
    [InlineData("native.sam.fab.resources")]
    [InlineData("native.sam.copy.")]
    [InlineData("native.sam.report.")]
    [InlineData("native.sam.report.submit")]
    [InlineData("native.sam.report.cancel")]
    [InlineData("native.sam.write.")]
    [InlineData("native.sam.memory.candidate")]
    [InlineData("native.sam.dispute")]
    [InlineData("native.sam.repair.")]
    [InlineData("native.sam.conversations.dialog.confirm")]
    [InlineData("native.sam.settings.delete")]
    [InlineData("native.sam.settings.delete.confirm")]
    [InlineData("native.sam.memory.forgetall")]
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
            + File.ReadAllText(Path.Combine(HeadPath, "../SentenceStudio.Native/Sam/NativeSamPresenter.Turns.cs"))
            + File.ReadAllText(Path.Combine(HeadPath, "../SentenceStudio.Native/Sam/NativeSamPresenter.Writes.cs"))
            + File.ReadAllText(Path.Combine(HeadPath, "../SentenceStudio.Native/Sam/NativeSamPresenter.Manage.cs"))
            + File.ReadAllText(Path.Combine(HeadPath, "../SentenceStudio.Native/Sam/NativeSamWritePresentation.cs"));
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

    /// <summary>
    /// Copy goes through the Essentials clipboard (non-UI service), never UIPasteboard,
    /// and the confirmation secret is never rendered or stored in published state.
    /// </summary>
    [Fact]
    public void CopyUsesEssentialsAndTheConfirmationValueNeverReachesTheView()
    {
        var clipboard = Source("Platform/NativeClipboard.cs");
        Assert.Contains("Microsoft.Maui.ApplicationModel.DataTransfer", clipboard);
        Assert.DoesNotMatch(@"\bUIPasteboard\b|\bUIKit\b", clipboard);
        Assert.Contains("NativeClipboard.SetTextAsync", Source("App/ApplicationController.Sam.cs"));
        foreach (var file in SamFiles)
            Assert.DoesNotContain("CoachWriteConfirmation", Source(file));
        var contracts = File.ReadAllText(Path.Combine(HeadPath, "../SentenceStudio.Native/Sam/NativeSamContracts.cs"));
        Assert.DoesNotContain("CoachWriteConfirmation", contracts);
    }

    /// <summary>The Resources tab entry floats as a bottom inset the search update unwraps.</summary>
    [Fact]
    public void ResourcesSamEntryKeepsTheIncrementalSearchUpdate()
    {
        var scene = Source("App/ApplicationScene.cs");
        Assert.Contains("WithoutSamInset(current.Children[0])", scene);
        Assert.Contains("SameBrowserControls(WithoutSamInset(", scene);
        Assert.Contains("Views.BottomInset(style.Page(page), samEntry)",
            Source("Features/LearningResources/LearningResourcesView.cs"));
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
