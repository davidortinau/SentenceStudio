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
        "Features/Sam/SamFloatingEntry.cs",
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
    /// Issue 316: what Sam's presentation needs from the system (whether VoiceOver is running,
    /// the system style, the device's version and model) is asked through the platform service,
    /// which draws nothing, and the rule it serves stays: Sam never opens an activity by itself
    /// while VoiceOver is running.
    /// </summary>
    [Fact]
    public void SamAsksTheSystemThroughThePlatformServiceAndNeverAutoOpensUnderVoiceOver()
    {
        var controller = Source("App/ApplicationController.Sam.cs");
        Assert.Matches(
            @"_sam\.CanAutoOpenActivity = \(\) => !NativeSystemState\.IsVoiceOverRunning\s+&& _destination == NativeDestination\.Dashboard && !IsActivityOpen && !NavigationBlocked;",
            controller);
        Assert.Contains("NativeSystemState.IsSystemStyleDark switch", controller);
        Assert.Contains("\"iOS \" + NativeSystemState.SystemVersion", controller);
        Assert.Contains("NativeSystemState.Model,", controller);

        var service = Source("Platform/NativeSystemState.cs");
        // Read on each call (a property, not a value kept from startup): VoiceOver is turned
        // on and off while the app runs.
        Assert.Contains(
            "internal static bool IsVoiceOverRunning => UIKit.UIAccessibility.IsVoiceOverRunning;", service);
        // The trait collection is read on the main thread only, as before.
        Assert.Matches(@"IsSystemStyleDark => Foundation\.NSThread\.IsMain\s+\? UIKit\.UITraitCollection", service);
        // A service, not UI: it authors no view, controller or control.
        Assert.DoesNotMatch(
            @"\bUIView\b|\bUIViewController\b|\bUIWindow\b|\bUIControl\b|\bUILabel\b|\bUIApplication\b|Microsoft\.SwiftUI",
            service);
    }

    /// <summary>
    /// Decision 6 (2026-11-03): Sam on every signed-in screen, as the source MainLayout
    /// mounts it. Where the tab bar shows, Sam's tab slot is the one entry (never a second
    /// toolbar button, Captain 2026-11-06); every pushed screen that hides the tab bar gets
    /// the navigation bar entry, so none of them can be missed.
    /// </summary>
    [Fact]
    public void EveryPushedScreenWithoutTheTabBarCarriesTheSamToolbarEntry()
    {
        // The browse pages keep the tab bar, so Sam's slot is their entry.
        var browse = new[]
        {
            "\"activity-log\"", "\"skills\"", "ApplicationFormRoutes.LibraryResources",
            "ApplicationFormRoutes.MinimalPairs", "ApplicationFormRoutes.RecentLookups",
            "ApplicationFormRoutes.VocabularyDuplicates",
            "ApplicationFormRoutes.Vocabulary", "ApplicationImportRoutes.ImportContent",
            "ApplicationYouTubeRoutes.YouTubeImport",
        };
        var files = Directory.GetFiles(Path.Combine(HeadPath, "App"), "ApplicationScene*.cs");
        var pushes = files.SelectMany(file => Regex.Matches(File.ReadAllText(file),
                @"\.Push\(([^,]+),\s*([^\n]*)")
            .Select(match => (File: Path.GetFileName(file), Route: match.Groups[1].Value.Trim(),
                Destination: match.Groups[2].Value.Trim())))
            .Where(push => push.Route != "\"sam-conversations\"")
            .ToArray();
        // Tab shell trial: Settings, Skills and Vocabulary are tab or Library roots now (no
        // push); the Minimal Pairs child page joined the Library stack.
        Assert.True(pushes.Length >= 11, $"Expected every pushed screen, found {pushes.Length}.");
        foreach (var push in pushes)
        {
            if (browse.Contains(push.Route))
                Assert.False(push.Destination.StartsWith("WithSamToolbar(", StringComparison.Ordinal),
                    $"{push.File} pushes {push.Route} with a second Sam entry beside his tab slot.");
            else
                Assert.True(push.Destination.StartsWith("WithSamToolbar(", StringComparison.Ordinal),
                    $"{push.File} pushes {push.Route} without the Sam entry.");
        }
        Assert.Equal(browse.Length, pushes.Count(push => browse.Contains(push.Route)));
        // A smart resource read in the Library keeps the tab bar: its toolbar entry drops then.
        Assert.Contains("tabBarShown: () => !_renderedResourceFormHidesTabBar",
            Source("App/ApplicationScene.cs"));
        var scene = Source("App/ApplicationScene.Sam.cs");
        Assert.Contains("SamView.ToolbarEntry(_app.SamState, _app.Localize, _app.SamActions.Open)", scene);
        Assert.Contains("UpdateSamToolbars();", scene);
        Assert.Contains("\"native.sam.toolbar\"", Source("Features/Sam/SamView.cs"));
        // Every push gets a slot whatever Sam's state: a facade toolbar's actions are
        // fixed once built, so the entry is a toolbar host added to (and removed from)
        // the slot as availability changes, never decided once at push time.
        var withToolbar = scene[scene.IndexOf("private SwiftView WithSamToolbar(", StringComparison.Ordinal)..
            scene.IndexOf("private void UpdateSamToolbars()", StringComparison.Ordinal)];
        Assert.DoesNotContain("IsSamAvailable", withToolbar);
        Assert.DoesNotContain("return page;", withToolbar);
        Assert.Contains("return Views.Overlay(page, slot, SwiftOverlayEdge.Top);", withToolbar);
        var update = scene[scene.IndexOf("private void UpdateSamToolbars()", StringComparison.Ordinal)..
            scene.IndexOf("private SwiftView SamToolbarHost()", StringComparison.Ordinal)];
        Assert.Contains("var available = _app.IsSamAvailable;", update);
        Assert.Contains("if (!available || entry.TabBarShown?.Invoke() == true)", update);
        Assert.Contains("slot.Add(SamToolbarHost());", update);
        Assert.Contains("slot.RemoveAt(0);", update);

        var controller = Source("App/ApplicationController.Sam.cs");
        Assert.Contains("destination is NativeDestination.Dashboard or NativeDestination.VocabularyQuiz\n"
            + "            or NativeDestination.QuizSummary or NativeDestination.Activity", controller);
        Assert.Contains("IsSamShellDestination(_destination) && !_preview", controller);
        Assert.Contains("internal bool IsSamPresented => IsSamShellDestination(_destination)", controller);
        // Start review away from the Dashboard root asks to close the screen instead of doing
        // nothing; an open resource form (unsaved edits) and Recent lookups block it too.
        Assert.Contains("_sam?.ReportVocabularyReviewBlockedByScreen();", controller);
        Assert.Contains("if (IsSamReviewBlockedByScreen)", controller);
        var guard = controller[controller.IndexOf("internal bool IsSamReviewBlockedByScreen =>",
            StringComparison.Ordinal)..];
        guard = guard[..guard.IndexOf(';')];
        // Tab shell trial: the review opens over the Study root, so History (pushed over
        // Study) and every editor with unsaved edits on another tab block it; a smart
        // resource being read in the Library does not.
        foreach (var screen in new[] { "_destination != NativeDestination.Dashboard",
            "IsActivityLogOpen", "IsProfileEditing", "HasLibraryEditor" })
            Assert.Contains(screen, guard);
        var library = Source("App/ApplicationController.Library.cs");
        var editor = library[library.IndexOf("internal bool HasLibraryEditor =>",
            StringComparison.Ordinal)..];
        editor = editor[..editor.IndexOf(';')];
        foreach (var screen in new[] { "IsResourcesFormOpen && !IsResourceFormReadOnly",
            "IsVocabularyWordOpen", "IsSkillFormOpen", "LibraryPairsHasChildPage" })
            Assert.Contains(screen, editor);
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

    /// <summary>
    /// Tab shell trial: Sam's slot replaces the Resources page's floating entry, so the
    /// browser has no bottom inset for the search update to unwrap.
    /// </summary>
    [Fact]
    public void ResourcesPageHasNoFloatingSamEntry()
    {
        var scene = Source("App/ApplicationScene.cs");
        Assert.DoesNotContain("WithoutSamInset", scene);
        Assert.Contains("!BrowserPageUpdate.Resources.TryUpdateInPlace(current, next)", scene);
        var view = Source("Features/LearningResources/LearningResourcesView.cs");
        Assert.DoesNotContain("samEntry", view);
        Assert.DoesNotContain("native.resources.more", view);
    }

    /// <summary>
    /// docs/sam-entry.md: the floating button on the Dashboard and Resources slides away on
    /// scroll through the shared rule, from SwiftUI reports only (scroll geometry, keyboard
    /// safe area, VoiceOver), keeps its room in the bottom inset, and is not tappable or
    /// announced while away.
    /// </summary>
    [Fact]
    public void FloatingSamEntryHidesOnScrollThroughTheSharedRule()
    {
        // iOS tab shell trial (2026-11-05): Sam's tab slot replaces the floating button on
        // the Study and Library roots; the binding below is kept for that button's return.
        var controller = Source("App/ApplicationController.Sam.cs");
        Assert.DoesNotContain("_dashboardSamEntry", controller);
        Assert.Contains("resources.RenderBrowser(style)", Source("App/ApplicationController.cs"));
        var scene = Source("App/ApplicationScene.cs");
        Assert.Contains(".WithTabRole(SwiftTabRole.Search)", scene);
        Assert.Contains("_samTab.WithTabHidden(hidden);", Source("App/ApplicationScene.Sam.cs"));

        var entry = Source("Features/Sam/SamFloatingEntry.cs");
        // The shared rule decides; the head only feeds it (no copy of its threshold or logic).
        Assert.Contains("private readonly NativeSamEntryAutoHide _rule = new();", entry);
        Assert.DoesNotContain("Threshold", entry);
        Assert.DoesNotContain("24", entry);
        foreach (var call in new[]
                 {
                     "_rule.OnScroll(delta, geometry.IsAtTop, geometry.IsAtBottom);",
                     "_rule.SetKeyboardVisible(points > 0);",
                     "_rule.SetScreenReaderRunning(settings.HasFlag(SwiftAccessibilitySettings.VoiceOver));",
                     "_rule.Reset();",
                     "inset.Children[0].OnScrollGeometryChanged(binding.Scrolled);",
                     "inset.OnAppear(binding.Appeared)",
                     ".OnKeyboardOverlapChanged(binding.KeyboardOverlapChanged)",
                     ".OnAccessibilitySettingsChanged(binding.AccessibilitySettingsChanged);",
                     "_entry.WithHidden(hidden, SwiftHideTransition.SlideDown);",
                 })
            Assert.Contains(call, entry);
        // Appearing keeps the keyboard state: Resources gets its search focus (and keyboard)
        // back on return, at an overlap the bridge does not report again.
        Assert.DoesNotContain("_rule.SetKeyboardVisible(false)", entry);
        // SwiftUI-only: no keyboard notifications or UIKit accessibility queries.
        Assert.DoesNotMatch(@"UIKeyboard|UIAccessibility|NSNotificationCenter|KeyboardWillShow", entry);
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

    [Fact]
    public void OpeningSam_MovesTheKeyboardToHisComposer()
    {
        static string Slice(string text, string from, string to)
        {
            var start = text.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, from);
            var end = text.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end > start, to);
            return text[start..end];
        }

        // Issue 262: with the keyboard up in the quiz answer, opening Sam left the keyboard
        // there, and what the learner typed for Sam went into the answer under him. The
        // source overlay focuses its composer when it opens (SamOverlayHost.OpenPanelAsync).
        var sam = Source("App/ApplicationScene.Sam.cs");
        var handoff = Slice(sam, "private void BeginSamFocusHandoff()",
            "private void OnSamSheetDismissed()");
        Assert.Contains("if (presented && !_samRenderedPresented)", handoff);
        Assert.Contains("_samComposerFocusPending = true;", handoff);
        Assert.Contains("_app.YieldInputFocusToSam();", handoff);

        // The composer asks once it can take text (its conversation is loaded), not before.
        var update = Slice(sam, "private void UpdateSam()", "private SwiftView BuildSamPanel(");
        var composerUpdated = update.IndexOf("SamView.UpdateComposer(", StringComparison.Ordinal);
        var requested = update.IndexOf("composer.FocusRequested = true;", StringComparison.Ordinal);
        Assert.True(composerUpdated >= 0 && requested > composerUpdated);
        Assert.Contains("_samField is { IsEnabled: true } composer", update);

        // The field under Sam gives up its focus before the pages are drawn again: the quiz
        // is rebuilt in the same update, and a rebuilt answer that asks for focus keeps the
        // keyboard under Sam.
        var core = Slice(Source("App/ApplicationScene.cs"), "private void UpdateCore()",
            "private void InstallTabs()");
        var begin = core.IndexOf("BeginSamFocusHandoff();", StringComparison.Ordinal);
        Assert.True(begin >= 0, "BeginSamFocusHandoff();");
        Assert.True(begin < core.IndexOf("_app.RenderDashboardContent(_style)", StringComparison.Ordinal));
        Assert.True(begin < core.IndexOf("UpdateQuiz();", StringComparison.Ordinal));
        Assert.True(begin < core.IndexOf("UpdateSam();", StringComparison.Ordinal));
        Assert.Contains("internal void YieldInputFocusToSam() => _quizInputFocused = false;",
            Source("App/ApplicationController.Quiz.cs"));
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(HeadPath, relative));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string ResourcesPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Shared/Resources/Strings"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
