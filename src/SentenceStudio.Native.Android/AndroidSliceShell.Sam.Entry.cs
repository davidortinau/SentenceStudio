using Android.Content;
using Android.Views.Accessibility;
using AndroidX.Compose;
using AndroidX.Compose.Animation;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Input.NestedScroll;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The floating Sam button never covers content (docs/sam-entry.md): its screens leave
/// room after their last item, and the shared <see cref="NativeSamEntryAutoHide"/> rule
/// slides it away on scroll down, while the keyboard is up, and never while TalkBack runs.
/// Scrolling is observed through Compose nested scroll only, without consuming it.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private readonly NativeSamEntryAutoHide _samDashboardEntry = new();
    private readonly NativeSamEntryAutoHide _samResourcesEntry = new();

    /// <summary>Bumped when a scroll or TalkBack moves the button; read only by its own box.</summary>
    private readonly MutableState<int> _samEntryMotion = new(0);

    private AndroidSamFloatingScreen _samFloatingScreen;
    private INestedScrollConnection? _samEntryScroll;
    private float? _samEntryOffset;
    private PaddingValues? _samFloatingRoom;
    private EnterTransition? _samEntryEnter;
    private ExitTransition? _samEntryExit;

    // The Resources list or grid on screen; null while neither is composed.
    private LazyListState? _resourcesListScroll;
    private LazyGridState? _resourcesGridScroll;

    /// <summary>
    /// A C# class cannot implement Compose's NestedScrollConnection: every member has a
    /// Kotlin-mangled JVM name (onPostScroll-DzOQY0M), which the Java callable wrapper
    /// generator rejects (XA4217). Material 3's pinned top app bar behavior is the stock
    /// connection that only records the content's scroll offset and consumes nothing, so
    /// it observes the screen here; no top app bar uses it.
    /// </summary>
    private void RememberSamEntryMotion(IComposer composer)
    {
        var offsets = composer.RememberTopAppBarState();
        var observer = composer.PinnedScrollBehavior(offsets);
        _samEntryScroll = composer.Remember(() => observer.NestedScrollConnection);
        composer.LaunchedEffect(offsets, async cancellationToken =>
        {
            try
            {
                await foreach (var offset in ComposeExtensions.SnapshotFlow(() => offsets.ContentOffset)
                    .WithCancellation(cancellationToken))
                    activity.RunOnUiThread(() => OnSamEntryScrolled(offset));
            }
            catch (OperationCanceledException)
            {
            }
        });
        composer.DisposableEffect("native.sam.entry.screenreader", () =>
        {
            var manager = activity.GetSystemService(Context.AccessibilityService) as AccessibilityManager;
            if (manager is null)
                return () => { };
            var listener = new SamTouchExplorationListener(running => Post(() => SetSamScreenReader(running)));
            manager.AddTouchExplorationStateChangeListener(listener);
            SetSamScreenReader(manager.IsTouchExplorationEnabled);
            return () =>
            {
                manager.RemoveTouchExplorationStateChangeListener(listener);
                listener.Dispose();
            };
        });
    }

    /// <summary>
    /// Records which floating screen is composing; a new screen or tab starts with the
    /// button shown (keyboard and TalkBack state belong to the device and are kept).
    /// </summary>
    private AndroidSamFloatingScreen ShowSamFloatingScreen(AndroidSamEntryPlacement placement)
    {
        var floating = AndroidSamProjection.FloatingScreen(Destination, placement);
        if (floating != _samFloatingScreen)
        {
            _samFloatingScreen = floating;
            if (floating != AndroidSamFloatingScreen.None)
                SamEntry(floating).Reset();
        }
        return floating;
    }

    private NativeSamEntryAutoHide SamEntry(AndroidSamFloatingScreen floating) =>
        floating == AndroidSamFloatingScreen.Resources ? _samResourcesEntry : _samDashboardEntry;

    /// <summary>Whether the floating screen is at its top and at its end; one that cannot scroll is at both.</summary>
    private (bool AtStart, bool AtEnd) SamEntryEdges(AndroidSamFloatingScreen floating) => floating switch
    {
        AndroidSamFloatingScreen.Dashboard when _dashboardScroll is { } scroll =>
            (!scroll.CanScrollBackward, !scroll.CanScrollForward),
        AndroidSamFloatingScreen.Resources when _resourcesGridScroll is { } grid =>
            (!grid.CanScrollBackward, !grid.CanScrollForward),
        AndroidSamFloatingScreen.Resources when _resourcesListScroll is { } list =>
            (!list.CanScrollBackward, !list.CanScrollForward),
        _ => (true, true),
    };

    private void OnSamEntryScrolled(float offset)
    {
        if (_disposed)
            return;
        var previous = _samEntryOffset;
        _samEntryOffset = offset;
        if (previous is not { } last || _samFloatingScreen == AndroidSamFloatingScreen.None)
            return;
        var (atStart, atEnd) = SamEntryEdges(_samFloatingScreen);
        var density = activity.Resources?.DisplayMetrics?.Density ?? 1f;
        if (SamEntry(_samFloatingScreen).OnScroll(
                AndroidSamProjection.ScrollDeltaDp(last, offset, density), atStart, atEnd))
            MoveSamEntry();
    }

    private void SetSamScreenReader(bool running)
    {
        // Both screens: the one not showing keeps the state for when it returns.
        var changed = _samDashboardEntry.SetScreenReaderRunning(running)
            | _samResourcesEntry.SetScreenReaderRunning(running);
        if (changed)
            MoveSamEntry();
    }

    private void MoveSamEntry() => _samEntryMotion.Value = unchecked(_samEntryMotion.Value + 1);

    /// <summary>Slide off the bottom edge and fade, about 200 ms each way (docs/sam-entry.md).</summary>
    /// <remarks>
    /// Compose scales every animation by the system animator duration scale, so with
    /// "Remove animations" on the button switches instead of sliding.
    /// </remarks>
    private (EnterTransition Enter, ExitTransition Exit) SamEntryTransitions()
    {
        _samEntryEnter ??= Transitions.SlideInVertically(height => height,
                AnimationSpecs.Tween(AndroidSamProjection.EntryMotionMillis))
            .Plus(Transitions.FadeIn(0f, AnimationSpecs.Tween(AndroidSamProjection.EntryMotionMillis)));
        _samEntryExit ??= Transitions.SlideOutVertically(height => height,
                AnimationSpecs.Tween(AndroidSamProjection.EntryMotionMillis))
            .Plus(Transitions.FadeOut(0f, AnimationSpecs.Tween(AndroidSamProjection.EntryMotionMillis)));
        return (_samEntryEnter, _samEntryExit);
    }

    /// <summary>The bottom page padding of a screen that may show the floating button.</summary>
    private int SamFloatingBottomRoom(int otherwise) =>
        SamEntryPlacement == AndroidSamEntryPlacement.Floating
            ? AndroidSamProjection.FloatingBottomRoomDp : otherwise;

    /// <summary>Lazy list and grid content padding that leaves room for the floating button.</summary>
    private PaddingValues? SamFloatingContentPadding() =>
        SamEntryPlacement == AndroidSamEntryPlacement.Floating
            ? _samFloatingRoom ??= new PaddingValues(bottom: AndroidSamProjection.FloatingBottomRoomDp)
            : null;

    /// <summary>TalkBack's touch exploration started or stopped (a non-UI system service callback).</summary>
    private sealed class SamTouchExplorationListener(Action<bool> changed)
        : Java.Lang.Object, AccessibilityManager.ITouchExplorationStateChangeListener
    {
        public void OnTouchExplorationStateChanged(bool enabled) => changed(enabled);
    }
}
