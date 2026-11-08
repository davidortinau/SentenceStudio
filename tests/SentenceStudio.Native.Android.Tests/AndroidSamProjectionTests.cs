using System.Reflection;
using System.Collections.Immutable;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Native;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// The Compose Sam head's platform-neutral projection of the shared presenter
/// state: iOS/source stream order, status actions, entry gating, adaptive size,
/// and the follow-newest scroll rule (no Android UI types involved).
/// </summary>
public sealed class AndroidSamProjectionTests
{
    private static readonly string HeadRoot = Path.GetFullPath(typeof(AndroidSamProjectionTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);

    private static NativeSamEntry Entry(string id, NativeSamEntryKind kind, string text = "x",
        bool pending = false) =>
        new(id, kind, text, DateTime.UtcNow, 1, [], [], null, [], IsPending: pending);

    private static NativeSamState Open(params NativeSamEntry[] entries) => new(
        IsVisible: true, IsOpen: true, Status: NativeSamStatus.Ready, ConversationId: "c1",
        Entries: [.. entries]);

    [Fact]
    public void RowsFollowTheSourceOrderWithUniqueKeys()
    {
        var state = Open(Entry("a", NativeSamEntryKind.Learner), Entry("b", NativeSamEntryKind.Receipt),
                Entry("b", NativeSamEntryKind.Sam)) with
            {
                NoticeKey = "Coach_Stopped",
                UnreadableCount = 2,
                CanUndo = true,
                Status = NativeSamStatus.Failed,
                CanRetry = true,
                ConversationClosed = true,
            };

        var rows = AndroidSamProjection.Rows(state);

        Assert.Equal(
            [AndroidSamRowKind.Unreadable, AndroidSamRowKind.Message, AndroidSamRowKind.Message,
                AndroidSamRowKind.Message, AndroidSamRowKind.Outcome, AndroidSamRowKind.Closed,
                AndroidSamRowKind.Status],
            rows.Select(row => row.Kind));
        Assert.Equal(rows.Length, rows.Select(row => row.Key).Distinct().Count());
        // Undo sits on the latest receipt message only when no live receipt card exists.
        Assert.True(rows[2].ShowUndo);
        Assert.False(rows[1].ShowUndo);
    }

    [Fact]
    public void StartersShowForAnEmptyThreadWithOrWithoutAPlan()
    {
        var empty = Open() with { CanEditPlan = true };
        Assert.Contains(AndroidSamProjection.Rows(empty), row => row.Kind == AndroidSamRowKind.Chips);
        // Starters are general requests to Sam, so they do not depend on a plan to edit.
        Assert.Contains(AndroidSamProjection.Rows(empty with { CanEditPlan = false }),
            row => row.Kind == AndroidSamRowKind.Chips);
        Assert.DoesNotContain(AndroidSamProjection.Rows(empty with { HasLearnerSpoken = true }),
            row => row.Kind == AndroidSamRowKind.Chips);
    }

    [Theory]
    [InlineData(NativeSamStatus.Running, true, true, false, false, "Stop")]
    [InlineData(NativeSamStatus.Failed, false, false, true, false, "Retry,KeepPlan")]
    [InlineData(NativeSamStatus.Incomplete, false, false, true, true, "Recover,KeepPlan")]
    [InlineData(NativeSamStatus.PlanChangedElsewhere, false, false, false, false, "ReloadPlan,KeepChatting")]
    [InlineData(NativeSamStatus.Expired, false, false, false, false, "StartNew")]
    [InlineData(NativeSamStatus.Offline, false, false, true, false, "Retry")]
    [InlineData(NativeSamStatus.Limited, false, false, false, false, "")]
    public void StatusOffersTheSourceActions(NativeSamStatus status, bool busy, bool canStop,
        bool canRetry, bool canRecover, string expected)
    {
        var state = Open() with
        {
            Status = status, Busy = busy, CanStop = canStop, CanRetry = canRetry, CanRecover = canRecover
        };

        var projected = AndroidSamProjection.Status(state, "쌤", key => key);

        Assert.NotNull(projected);
        Assert.Equal(expected, string.Join(",", projected.Actions));
        Assert.Equal(status == NativeSamStatus.Failed, projected.Danger);
        Assert.Equal(busy, projected.Progress);
    }

    [Fact]
    public void ReadyHasNoStatusRow()
    {
        Assert.Null(AndroidSamProjection.Status(Open(), "Sam", key => key));
        Assert.DoesNotContain(AndroidSamProjection.Rows(Open()), row => row.Kind == AndroidSamRowKind.Status);
    }

    [Theory]
    // Tab shell trial: a screen with the tab bar reaches Sam through his tab slot only.
    [InlineData(NativeDestination.Dashboard, false, true, true, false, "None")]
    [InlineData(NativeDestination.TodaysPlan, false, true, true, false, "None")]
    [InlineData(NativeDestination.Resources, false, true, true, false, "None")]
    // Screens without the tab bar (editors, the quiz, activities): the top app bar.
    [InlineData(NativeDestination.Dashboard, false, false, true, false, "TopBar")]
    [InlineData(NativeDestination.Resources, false, false, true, false, "TopBar")]
    [InlineData(NativeDestination.VocabularyQuiz, false, false, true, false, "TopBar")]
    [InlineData(NativeDestination.QuizSummary, false, false, true, false, "TopBar")]
    [InlineData(NativeDestination.Activity, false, false, true, false, "TopBar")]
    // Never before sign-in finishes, in the preview, while unavailable or already open.
    [InlineData(NativeDestination.SignIn, false, false, true, false, "None")]
    [InlineData(NativeDestination.Onboarding, false, false, true, false, "None")]
    [InlineData(NativeDestination.Dashboard, true, false, true, false, "None")]
    [InlineData(NativeDestination.Dashboard, false, false, false, false, "None")]
    [InlineData(NativeDestination.Activity, false, false, true, true, "None")]
    public void EntryIsTheTabSlotOrTheTopBar(NativeDestination destination, bool preview,
        bool tabBarShown, bool visible, bool open, string expectedPlacement)
    {
        var state = new NativeSamState(IsVisible: visible, IsOpen: open);
        var expected = Enum.Parse<AndroidSamEntryPlacement>(expectedPlacement);
        Assert.Equal(expected, AndroidSamProjection.EntryPlacement(destination, preview, tabBarShown, state));
        Assert.Equal(expected != AndroidSamEntryPlacement.None,
            AndroidSamProjection.IsEntryVisible(destination, preview, tabBarShown, state));
    }

    [Theory]
    // Today's Plan is the Dashboard's own mode on Android: one screen, one hide state.
    [InlineData(NativeDestination.Dashboard, "Floating", "Dashboard")]
    [InlineData(NativeDestination.TodaysPlan, "Floating", "Dashboard")]
    [InlineData(NativeDestination.Resources, "Floating", "Resources")]
    // A screen over a tab, the quiz and activities use the top bar; nothing floats.
    [InlineData(NativeDestination.Dashboard, "TopBar", "None")]
    [InlineData(NativeDestination.Resources, "TopBar", "None")]
    [InlineData(NativeDestination.Activity, "TopBar", "None")]
    [InlineData(NativeDestination.Dashboard, "None", "None")]
    public void EachFloatingScreenKeepsItsOwnHideState(NativeDestination destination,
        string placement, string expected) =>
        Assert.Equal(Enum.Parse<AndroidSamFloatingScreen>(expected), AndroidSamProjection.FloatingScreen(
            destination, Enum.Parse<AndroidSamEntryPlacement>(placement)));

    [Fact]
    public void FloatingScreensLeaveTheButtonItsMarginAndAGap()
    {
        Assert.Equal(56, AndroidSamProjection.FabSizeDp);
        Assert.Equal(16, AndroidSamProjection.FabMarginDp);
        Assert.Equal(88, AndroidSamProjection.FloatingBottomRoomDp);
        Assert.Equal(200, AndroidSamProjection.EntryMotionMillis);
    }

    [Theory]
    // Compose content offset falls (content moves up) when the learner scrolls down.
    [InlineData(0f, -48f, 2f, 24d)]
    [InlineData(-48f, 0f, 2f, -24d)]
    [InlineData(-100f, -100f, 3f, 0d)]
    [InlineData(0f, -10f, 0f, 0d)]
    [InlineData(0f, float.NaN, 2f, 0d)]
    [InlineData(float.NegativeInfinity, 0f, 2f, 0d)]
    public void ScrollDeltaIsDpAndPositiveTowardTheEnd(float previous, float offset, float density,
        double expected) =>
        Assert.Equal(expected, AndroidSamProjection.ScrollDeltaDp(previous, offset, density), 3);

    [Fact]
    public void ScrollingDownTheThresholdInDpHidesTheButtonOnAnyDensity()
    {
        foreach (var density in new[] { 1f, 2.625f, 3.5f })
        {
            var entry = new NativeSamEntryAutoHide();
            var offset = 0f;
            var down = (float)(NativeSamEntryAutoHide.Threshold * density / 4);
            for (var step = 0; step < 4; step++)
            {
                var next = offset - down;
                entry.OnScroll(AndroidSamProjection.ScrollDeltaDp(offset, next, density), false, false);
                offset = next;
            }
            Assert.False(entry.IsShown);
            var up = offset + (float)(NativeSamEntryAutoHide.Threshold * density);
            entry.OnScroll(AndroidSamProjection.ScrollDeltaDp(offset, up, density), false, false);
            Assert.True(entry.IsShown);
        }
    }

    [Theory]
    // Tab roots start the review.
    [InlineData(NativeDestination.Dashboard, false, false)]
    [InlineData(NativeDestination.TodaysPlan, false, false)]
    [InlineData(NativeDestination.Resources, false, false)]
    // A More screen (Settings, Profile, Skills, Vocabulary, Activity Log) or the
    // resource form, an activity or the quiz must close first, as on iOS.
    [InlineData(NativeDestination.Dashboard, true, true)]
    [InlineData(NativeDestination.Resources, true, true)]
    [InlineData(NativeDestination.Activity, false, true)]
    [InlineData(NativeDestination.VocabularyQuiz, false, true)]
    [InlineData(NativeDestination.QuizSummary, false, true)]
    public void SamReviewStartsOnlyFromATabRoot(NativeDestination destination, bool screenOverTab,
        bool blocked) =>
        Assert.Equal(blocked, AndroidSamProjection.ReviewBlockedByScreen(destination, screenOverTab));

    [Fact]
    public void ShellUsesTheReviewGuardAndDocumentsTheFab()
    {
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.Sam.cs"));
        Assert.Contains("AndroidSamProjection.ReviewBlockedByScreen(Destination,\n"
            + "                IsMoreScreenOpen || IsResourceFormOpen)", shell);
        var compose = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.Sam.Compose.cs"));
        Assert.Contains("    /// Source SamFab: the one entry, bottom trailing, round and icon only, named \"Ask {name}\".\n", compose);
        Assert.Contains("    /// </summary>\n    private ComposableNode RenderSamFab(", compose);
        Assert.Equal(1, compose.Split("private ComposableNode WithSam(").Length - 1);
        var withSam = compose[..compose.IndexOf("private ComposableNode WithSam(", StringComparison.Ordinal)];
        var lastSummary = withSam.LastIndexOf("/// <summary>", StringComparison.Ordinal);
        Assert.DoesNotContain("Source SamFab", withSam[lastSummary..]);
    }

    [Theory]
    [InlineData(NativeDestination.Dashboard, false, true)]
    [InlineData(NativeDestination.Resources, false, true)]
    [InlineData(NativeDestination.VocabularyQuiz, false, true)]
    [InlineData(NativeDestination.Activity, false, true)]
    [InlineData(NativeDestination.Onboarding, false, false)]
    [InlineData(NativeDestination.SignIn, false, false)]
    [InlineData(NativeDestination.Activity, true, false)]
    public void PanelStaysOpenAcrossSignedInScreens(NativeDestination destination, bool preview, bool expected)
    {
        var state = new NativeSamState(IsVisible: true, IsOpen: true);
        Assert.Equal(expected, AndroidSamProjection.IsPresented(destination, preview, state));
    }

    [Fact]
    public void EntryLabelUsesTheStudyLanguagePersona()
    {
        var korean = new NativeSamState(PersonaCulture: "ko");
        var persona = AndroidSamProjection.Persona(korean,
            (key, culture) => culture.Name == "ko" ? "쌤" : "Sam");
        Assert.Equal("Ask 쌤", AndroidSamProjection.EntryLabel(persona, _ => "Ask {0}"));
    }

    [Theory]
    [InlineData(411, NativeSamPanelSize.Compact, true)]
    [InlineData(599, NativeSamPanelSize.Compact, true)]
    [InlineData(600, NativeSamPanelSize.Expanded, false)]
    [InlineData(1280, NativeSamPanelSize.Expanded, false)]
    public void PhonesOpenCompactAndTabletsOpenFull(int widthDp, NativeSamPanelSize size, bool resizable)
    {
        Assert.Equal(size, AndroidSamProjection.DefaultSize(widthDp));
        Assert.Equal(resizable, AndroidSamProjection.IsResizable(widthDp));
        var compactState = new NativeSamState(Size: NativeSamPanelSize.Compact);
        Assert.Equal(resizable, AndroidSamProjection.UsesCompactHeight(compactState, widthDp));
    }

    [Fact]
    public void FirstContentAndThreadSwitchJumpToTheNewestRow()
    {
        Assert.Equal(AndroidSamFollow.Jump,
            AndroidSamProjection.Follow(null, null, "m:1", "c1", false, false, false));
        Assert.Equal(AndroidSamFollow.Jump,
            AndroidSamProjection.Follow("m:1", "c1", "m:9", "c2", false, false, false));
    }

    [Fact]
    public void NewRowsFollowOnlyWhenAtTheEndOrAfterTheLearnersOwnSend()
    {
        Assert.Equal(AndroidSamFollow.Animate,
            AndroidSamProjection.Follow("m:1", "c1", "m:2", "c1", wasAtEnd: true, false, false));
        // Scrolled up to reread: a new answer never pulls the learner down.
        Assert.Equal(AndroidSamFollow.None,
            AndroidSamProjection.Follow("m:1", "c1", "m:2", "c1", wasAtEnd: false, false, false));
        Assert.Equal(AndroidSamFollow.None,
            AndroidSamProjection.Follow("m:1", "c1", "m:2", "c1", wasAtEnd: true, userScrolling: true, false));
        // The learner's own send always follows.
        Assert.Equal(AndroidSamFollow.Animate,
            AndroidSamProjection.Follow("m:1", "c1", "m:local", "c1", false, false, newestIsOwnSend: true));
        Assert.Equal(AndroidSamFollow.None,
            AndroidSamProjection.Follow("m:2", "c1", "m:2", "c1", true, false, false));
        Assert.Equal(AndroidSamFollow.None,
            AndroidSamProjection.Follow("m:2", "c1", null, "c1", true, false, false));
    }

    [Fact]
    public void FollowTracksContentNotTheTrailingStatusRow()
    {
        var loading = Open(Entry("a", NativeSamEntryKind.Sam)) with { Status = NativeSamStatus.Resuming };
        var rows = AndroidSamProjection.Rows(loading);
        Assert.Equal(AndroidSamRowKind.Status, rows[^1].Kind);
        Assert.Equal("m:a", AndroidSamProjection.NewestContentKey(rows));
    }

    [Fact]
    public void PendingLearnerEchoIsTheOwnSend()
    {
        var state = Open(Entry("a", NativeSamEntryKind.Sam),
            Entry("local:1", NativeSamEntryKind.Learner, pending: true)) with
        { Status = NativeSamStatus.Running, Busy = true };
        Assert.True(AndroidSamProjection.NewestIsOwnSend(state, AndroidSamProjection.Rows(state)));
        Assert.False(AndroidSamProjection.NewestIsOwnSend(Open(Entry("a", NativeSamEntryKind.Learner)),
            AndroidSamProjection.Rows(Open(Entry("a", NativeSamEntryKind.Learner)))));
    }

    [Fact]
    public void ComposerIsUsableOnlyInAnOpenWritableThread()
    {
        Assert.True(AndroidSamProjection.ComposerEnabled(Open()));
        Assert.False(AndroidSamProjection.ComposerEnabled(Open() with { ConversationClosed = true }));
        Assert.False(AndroidSamProjection.ComposerEnabled(Open() with { ConversationId = null }));
        Assert.False(AndroidSamProjection.ComposerEnabled(Open() with { Status = NativeSamStatus.Limited }));
        Assert.Equal("5 / 500", AndroidSamProjection.Counter(Open() with { Draft = "hello" }, _ => "{0} / {1}"));
    }
}
