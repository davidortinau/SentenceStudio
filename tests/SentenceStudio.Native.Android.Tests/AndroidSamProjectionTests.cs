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
    public void ChipsShowOnlyForAnEmptyEditableThread()
    {
        var empty = Open() with { CanEditPlan = true };
        Assert.Contains(AndroidSamProjection.Rows(empty), row => row.Kind == AndroidSamRowKind.Chips);
        Assert.DoesNotContain(AndroidSamProjection.Rows(empty with { CanEditPlan = false }),
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
    [InlineData(NativeDestination.Dashboard, false, false, true, false, true)]
    [InlineData(NativeDestination.TodaysPlan, false, false, true, false, true)]
    [InlineData(NativeDestination.Resources, false, false, true, false, false)]
    [InlineData(NativeDestination.VocabularyQuiz, false, false, true, false, false)]
    [InlineData(NativeDestination.Dashboard, true, false, true, false, false)]
    [InlineData(NativeDestination.Dashboard, false, true, true, false, false)]
    [InlineData(NativeDestination.Dashboard, false, false, false, false, false)]
    [InlineData(NativeDestination.Dashboard, false, false, true, true, false)]
    public void EntryAppearsOnlyOnTheAuthenticatedDashboard(NativeDestination destination, bool preview,
        bool overlay, bool visible, bool open, bool expected)
    {
        var state = new NativeSamState(IsVisible: visible, IsOpen: open);
        Assert.Equal(expected, AndroidSamProjection.IsEntryVisible(destination, preview, overlay, state));
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
