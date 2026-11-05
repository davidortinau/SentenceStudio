using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Source contracts for the Compose Sam head: Compose-only presentation, the
/// shared presenter (no forked business logic), iOS-matching native.sam ids,
/// en/ko resources for every key it resolves, and owner-boundary clearing.
/// </summary>
public sealed class AndroidSamContractTests
{
    private static readonly string[] SamFiles =
    [
        "AndroidSliceShell.Sam.cs",
        "AndroidSliceShell.Sam.Compose.cs",
        "AndroidSliceShell.Sam.Entry.cs",
        "AndroidSliceShell.Sam.Cards.Compose.cs",
        "AndroidSliceShell.Sam.Settings.Compose.cs",
        "AndroidSamProjection.cs",
        "AndroidSamCards.cs",
    ];

    /// <summary>Copy uses the reviewed non-UI Essentials Clipboard, isolated in one file.</summary>
    [Fact]
    public void CopyUsesTheEssentialsClipboardAndNeverLogsTheText()
    {
        var clipboard = Code("AndroidSamClipboard.cs");
        Assert.Contains("using Microsoft.Maui.ApplicationModel.DataTransfer;", clipboard);
        Assert.Contains("Clipboard.Default.SetTextAsync(text)", clipboard);
        Assert.DoesNotMatch(@"Android\.Widget|AndroidX\.AppCompat|Microsoft\.Maui\.Controls", clipboard);
        var copy = Between(Code("AndroidSliceShell.Sam.cs"), "private void CopySamMessage(", "\n    });");
        Assert.Contains("sam.ReportCopyResult(messageId, copied);", copy);
        Assert.DoesNotMatch(@"Log\.\w+\([^)]*\btext\b", copy);
        var project = File.ReadAllText(Path.Combine(Root, "SentenceStudio.Native.Android.csproj"));
        Assert.Contains("<PackageReference Include=\"Microsoft.Maui.Essentials\" />", project);
        Assert.DoesNotContain("Microsoft.Maui.Controls", project);
    }

    /// <summary>The hard-write confirm passes no value from the head: the presenter holds it.</summary>
    [Fact]
    public void HardWriteConfirmCarriesNoValueThroughTheHead()
    {
        var shell = Code("AndroidSliceShell.Sam.cs");
        Assert.Contains("sam.ConfirmWriteAsync(_lifetime.Token)", shell);
        Assert.DoesNotMatch(@"CoachWriteConfirmation|ConfirmationToken|ConfirmationValue",
            string.Concat(SamFiles.Select(Code)));
    }

    /// <summary>Settings shows the Learning Coach and memory cards and loads them on open.</summary>
    [Fact]
    public void SettingsHostsTheSamSectionsAndLoadsThemOnOpen()
    {
        Assert.Contains("RenderSamSettings(palette)", Code("AndroidSliceShell.Appearance.cs"));
        Assert.Contains("OpenSamSettings();",
            Between(Code("AndroidSliceShell.More.cs"), "case MoreScreen.Settings:", "break;"));
        var open = Between(Code("AndroidSliceShell.Sam.cs"), "private void OpenSamSettings()", "\n    }");
        Assert.Contains("RefreshAvailabilityAsync", open);
        Assert.Contains("LoadMemoryAsync", open);
    }

    [Fact]
    public void SendPushesTheLatestTypedTextAndAccountChangeClearsTheDraft()
    {
        var shell = File.ReadAllText(Path.Combine(Root, "AndroidSliceShell.Sam.cs"));
        var send = Between(shell, "private void SendSam()", "\n    }");
        Assert.True(send.IndexOf("_sam?.SetDraft(typed)", StringComparison.Ordinal)
            < send.IndexOf("SamState.CanSend", StringComparison.Ordinal));
        var clear = Between(shell, "private void ClearSamForAccountBoundary()", "\n    }");
        Assert.Contains("_samDraftSynced = string.Empty;", clear);
        Assert.Contains("NewTextFieldValue(string.Empty)", clear);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return text[from..to];
    }

    [Fact]
    public void SamIsComposeOnlyAndBindsTheSharedPresenter()
    {
        foreach (var file in SamFiles)
        {
            var source = Code(file);
            Assert.DoesNotMatch(@"Android\.Widget|AndroidX\.AppCompat|\bAndroidView\b|Android\.App\.Dialog|Microsoft\.Maui", source);
        }
        var compose = Code("AndroidSliceShell.Sam.Compose.cs");
        Assert.Contains("new ModalBottomSheet(onDismissRequest: CloseSam", compose);
        Assert.Contains("new LazyColumn<AndroidSamRow>", compose);
        Assert.Contains("new FloatingActionButton(OpenSam)", compose);
        Assert.Contains("new BackHandler(ToggleSamConversations", compose);
        var wiring = Code("AndroidSliceShell.Sam.cs");
        Assert.Contains("GetRequiredService<NativeSamPresenter>()", wiring);
        Assert.DoesNotContain("ICoachApiClient", wiring + compose);
        Assert.Contains("services.AddOwnerBoundNativeSam(apiUri);", Code("AndroidServiceHost.cs"));
    }

    /// <summary>
    /// Captain 2026-11-04: the floating entry is round and shows only Sam's icon, like
    /// every other icon entry; its accessible name stays "Ask {coach}" (docs/sam-entry.md).
    /// </summary>
    [Fact]
    public void FloatingEntryIsARoundIconOnlyButtonNamedForTheCoach()
    {
        var compose = Code("AndroidSliceShell.Sam.Compose.cs");
        Assert.DoesNotContain("ExtendedFloatingActionButton", compose);
        var fab = Between(compose, "private ComposableNode RenderSamFab(", "\n    }\n");
        Assert.Contains("new FloatingActionButton(OpenSam)", fab);
        Assert.Contains("new Icon(Icons.Default.Face, null)", fab);
        Assert.DoesNotContain("new Text(", fab);
        Assert.Contains("Shape = Shape.Circle(),", fab);
        Assert.Contains("AndroidSamProjection.EntryLabel(", fab);
        Assert.Contains("semantics.ContentDescription(label);", fab);
        Assert.Contains("semantics.Role(SemanticsRole.Button);", fab);
        Assert.Contains(".TestTag(\"native.sam.fab\")", fab);
        Assert.Contains("ContainerColor = palette.Primary,", fab);
        Assert.Contains("ContentColor = palette.OnPrimary,", fab);
    }

    /// <summary>
    /// The floating button never covers content: it rides the shared rule (scroll,
    /// keyboard, TalkBack), leaves composition when hidden, and slides and fades.
    /// </summary>
    [Fact]
    public void FloatingEntryFollowsTheSharedHideOnScrollRule()
    {
        var compose = Code("AndroidSliceShell.Sam.Compose.cs");
        var fab = Between(compose, "private ComposableNode RenderSamFab(", "\n    }\n");
        Assert.Contains("new AnimatedVisibility(entry.IsShown, enter, exit) { fab }", fab);
        Assert.Contains("entry.SetKeyboardVisible(composer.ImeInsets().AsPaddingValues(composer).Bottom.Value > 0f);", fab);
        Assert.Contains("entry.OnScroll(0, atStart, atEnd);", fab);
        Assert.Contains("_ = _samEntryMotion.Value;", fab);
        Assert.Contains(".ClipToBounds()", fab);
        var withSam = Between(compose, "private ComposableNode WithSam(", "\n    }\n");
        Assert.Contains("Modifier.FillMaxSize().NestedScroll(observer)", withSam);
        Assert.Contains("RenderSamFab(floating, palette)", withSam);

        var entry = Code("AndroidSliceShell.Sam.Entry.cs");
        // One shared rule per floating screen: Dashboard (with Today's Plan) and Resources.
        Assert.Equal(2, Regex.Matches(entry, @"readonly NativeSamEntryAutoHide _sam\w+Entry = new\(\);").Count);
        // C# cannot implement NestedScrollConnection (Kotlin-mangled names, XA4217):
        // the pinned top app bar behavior observes the scroll and consumes nothing.
        Assert.DoesNotMatch(@"\b(class|record)\s+\w+[^{;]*INestedScrollConnection", entry);
        Assert.Contains("composer.PinnedScrollBehavior(offsets)", entry);
        Assert.Contains("ComposeExtensions.SnapshotFlow(() => offsets.ContentOffset)", entry);
        Assert.Contains("AndroidSamProjection.ScrollDeltaDp(last, offset, density)", entry);
        Assert.Contains("SamEntry(floating).Reset();", entry);
        Assert.Contains("manager.AddTouchExplorationStateChangeListener(listener);", entry);
        Assert.Contains("manager.RemoveTouchExplorationStateChangeListener(listener);", entry);
        Assert.Contains("SetSamScreenReader(manager.IsTouchExplorationEnabled);", entry);
        Assert.Contains("Transitions.SlideOutVertically(height => height,", entry);
        Assert.Contains("Transitions.FadeOut(0f,", entry);
        Assert.Contains("Transitions.SlideInVertically(height => height,", entry);
        Assert.Contains("Transitions.FadeIn(0f,", entry);
    }

    /// <summary>The Dashboard (with Today's Plan) and Resources leave room after their last item.</summary>
    [Fact]
    public void FloatingScreensLeaveRoomForTheButtonAfterTheirLastItem()
    {
        var dashboard = Between(Code("AndroidSliceShell.Compose.cs"),
            "private ComposableNode RenderDashboard(", "\n    }\n");
        Assert.Contains(".VerticalScroll(_dashboardScroll)", dashboard);
        Assert.Contains("bottom: SamFloatingBottomRoom(16))", dashboard);

        var resources = Between(Code("AndroidSliceShell.Resources.Compose.cs"),
            "private ComposableNode RenderResources(", "\n    }\n");
        Assert.Contains("var room = SamFloatingContentPadding();", resources);
        Assert.Equal(2, Regex.Matches(resources, @"ContentPadding = room,").Count);
        Assert.Contains("composer.RememberLazyGridState()", resources);
        Assert.Contains("composer.RememberLazyListState()", resources);
        Assert.Equal(2, Regex.Matches(resources, @"State = scroll,").Count);
        Assert.True(resources.IndexOf("_resourcesListScroll = null;", StringComparison.Ordinal)
            < resources.IndexOf("EnsureResources()", StringComparison.Ordinal));
    }

    /// <summary>
    /// Snackbars never sit under or over the floating button: the only snackbar host
    /// is the activity toast, and every activity uses the top-bar entry, not the FAB.
    /// </summary>
    [Fact]
    public void NoSnackbarSharesAScreenWithTheFloatingButton()
    {
        var head = Directory.GetFiles(Root, "*.cs").Select(File.ReadAllText).ToArray();
        Assert.Single(head, source => source.Contains("new SnackbarHost(", StringComparison.Ordinal));
        Assert.Contains("new SnackbarHost(", Code("AndroidSliceShell.Messages.Compose.cs"));
        var shell = Code("AndroidSliceShell.Compose.cs");
        Assert.Single(Regex.Matches(shell, @"WithActivityToast\("));
        Assert.Contains("NativeDestination.Activity => WithActivityToast(composer, RenderActivity(palette)),", shell);
        Assert.Equal(AndroidSamEntryPlacement.TopBar, AndroidSamProjection.EntryPlacement(
            NativeDestination.Activity, preview: false, screenOverTab: false,
            new NativeSamState(IsVisible: true)));
    }

    [Theory]
    [InlineData("native.sam.fab")]
    [InlineData("native.sam.panel")]
    [InlineData("native.sam.stream")]
    [InlineData("native.sam.composer")]
    [InlineData("native.sam.send")]
    [InlineData("native.sam.counter")]
    [InlineData("native.sam.close")]
    [InlineData("native.sam.size")]
    [InlineData("native.sam.conversations.toggle")]
    [InlineData("native.sam.conversations.new")]
    [InlineData("native.sam.conversation.")]
    [InlineData("native.sam.new")]
    [InlineData("native.sam.suggestion.accept")]
    [InlineData("native.sam.suggestion.reject")]
    [InlineData("native.sam.receipt.undo")]
    [InlineData("native.sam.vocabset.approve")]
    [InlineData("native.sam.vocabset.decline")]
    [InlineData("native.sam.vocabset.start")]
    [InlineData("native.sam.chip.")]
    [InlineData("native.sam.status")]
    [InlineData("native.sam.retry")]
    [InlineData("native.sam.stop")]
    [InlineData("native.sam.reloadplan")]
    [InlineData("native.sam.keepplan")]
    [InlineData("native.sam.startnew")]
    [InlineData("native.sam.repair.")]
    [InlineData("native.sam.write.")]
    [InlineData("native.sam.copy.")]
    [InlineData("native.sam.report.")]
    [InlineData("native.sam.reported.")]
    [InlineData("native.sam.report.panel")]
    [InlineData("native.sam.report.reason.")]
    [InlineData("native.sam.report.submit")]
    [InlineData("native.sam.report.cancel")]
    [InlineData("native.sam.memory.candidate")]
    [InlineData("native.sam.memory.save")]
    [InlineData("native.sam.memory.edit")]
    [InlineData("native.sam.memory.onlythischat")]
    [InlineData("native.sam.memory.notnow")]
    [InlineData("native.sam.memory.editfield")]
    [InlineData("native.sam.memory.saveedited")]
    [InlineData("native.sam.memory.notice")]
    [InlineData("native.sam.dispute")]
    [InlineData("native.sam.dispute.state")]
    [InlineData("native.sam.conversations.outcome")]
    [InlineData("native.sam.conversations.dialog")]
    [InlineData("native.sam.conversations.dialog.confirm")]
    [InlineData("native.sam.conversations.dialog.cancel")]
    [InlineData("native.sam.conversations.rename.field")]
    [InlineData("native.sam.settings")]
    [InlineData("native.sam.settings.coach")]
    [InlineData("native.sam.settings.delete")]
    [InlineData("native.sam.settings.delete.confirm")]
    [InlineData("native.sam.settings.notice")]
    [InlineData("native.sam.memory.panel")]
    [InlineData("native.sam.memory.tab.active")]
    [InlineData("native.sam.memory.tab.candidates")]
    [InlineData("native.sam.memory.forgetall")]
    [InlineData("native.sam.memory.paused")]
    public void EverySamSurfaceHasTheIosTestTag(string id) =>
        Assert.Contains($"\"{id}", string.Concat(SamFiles.Select(Code)));

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void EveryResourceKeyTheComposeSamResolvesExistsInEnglishAndKorean(string file)
    {
        var names = XDocument.Load(Path.Combine(Root, "..", "SentenceStudio.Shared", "Resources",
                "Strings", file)).Descendants("data")
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Element("value")?.Value))
            .Select(entry => entry.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
        var literal = Regex.Matches(string.Concat(SamFiles.Select(Code)),
                "\"((?:Coach|NativeSam|Common)_[A-Za-z_]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(key => !key.EndsWith('_'))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(literal);
        var composed = Enum.GetNames<CoachAnswerBlockKind>().Where(kind => kind != "Answer")
            .Select(kind => "Coach_AnswerBlock_" + kind);
        Assert.Empty(literal.Concat(composed).Where(key => !names.Contains(key)));
    }

    /// <summary>Every owner change and sign-out drops the previous owner's Sam state.</summary>
    [Fact]
    public void AccountBoundariesClearSam()
    {
        var shell = Code("AndroidSliceShell.cs");
        Assert.Equal(3, Regex.Matches(shell, @"ClearSamForAccountBoundary\(\);").Count);
        Assert.Contains("DetachSam();", shell);
        Assert.Contains("Post(DetachSam);", shell);
        Assert.Contains("Post(RefreshSamAvailability);", shell);
    }

    private static string Code(string name) => string.Join('\n', File.ReadAllText(Path.Combine(Root, name))
        .Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
            && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    private static string Root => typeof(AndroidSamContractTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
}
