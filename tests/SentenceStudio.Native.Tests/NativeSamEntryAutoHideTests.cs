using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The floating Sam button slides away on scroll down and returns on scroll up, at the
/// top and at the end (Captain 2026-11-04; docs/sam-entry.md).
/// </summary>
public sealed class NativeSamEntryAutoHideTests
{
    [Fact]
    public void StartsShown() => Assert.True(new NativeSamEntryAutoHide().IsShown);

    [Fact]
    public void ScrollingDownPastTheThreshold_HidesIt()
    {
        var entry = new NativeSamEntryAutoHide();

        Assert.False(entry.OnScroll(10, atStart: false, atEnd: false));
        Assert.True(entry.IsShown);
        Assert.True(entry.OnScroll(NativeSamEntryAutoHide.Threshold - 10, atStart: false, atEnd: false));

        Assert.False(entry.IsShown);
    }

    [Fact]
    public void ScrollingUpPastTheThreshold_BringsItBack()
    {
        var entry = Hidden();

        Assert.False(entry.OnScroll(-10, atStart: false, atEnd: false));
        Assert.False(entry.IsShown);
        Assert.True(entry.OnScroll(-(NativeSamEntryAutoHide.Threshold - 10), atStart: false, atEnd: false));

        Assert.True(entry.IsShown);
    }

    [Fact]
    public void ChangingDirection_StartsTheTravelAgain()
    {
        var entry = new NativeSamEntryAutoHide();

        entry.OnScroll(20, atStart: false, atEnd: false);
        entry.OnScroll(-2, atStart: false, atEnd: false);
        entry.OnScroll(20, atStart: false, atEnd: false);

        Assert.True(entry.IsShown, "20 + 20 down with a 2 up between never travels 24 in one direction");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AtTheTopOrTheEnd_ItIsShown(bool atStart, bool atEnd)
    {
        var entry = Hidden();

        Assert.True(entry.OnScroll(40, atStart, atEnd));

        Assert.True(entry.IsShown);
    }

    [Fact]
    public void LeavingTheEnd_NeedsAFreshScrollDown()
    {
        var entry = new NativeSamEntryAutoHide();
        entry.OnScroll(20, atStart: false, atEnd: false);
        entry.OnScroll(10, atStart: false, atEnd: true);

        entry.OnScroll(10, atStart: false, atEnd: false);

        Assert.True(entry.IsShown, "the 20 travelled before the end does not count after it");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    public void ANonMovingOrBadStep_ChangesNothing(double delta)
    {
        var entry = new NativeSamEntryAutoHide();
        entry.OnScroll(20, atStart: false, atEnd: false);

        Assert.False(entry.OnScroll(delta, atStart: false, atEnd: false));
        entry.OnScroll(4, atStart: false, atEnd: false);

        Assert.False(entry.IsShown, "the bad step did not reset the 20 already travelled");
    }

    [Fact]
    public void TheKeyboard_HidesItWhereverTheScrollIs()
    {
        var entry = new NativeSamEntryAutoHide();

        Assert.True(entry.SetKeyboardVisible(true));
        Assert.False(entry.IsShown);
        Assert.False(entry.OnScroll(-40, atStart: true, atEnd: false));
        Assert.False(entry.IsShown);

        Assert.True(entry.SetKeyboardVisible(false));
        Assert.True(entry.IsShown);
    }

    [Fact]
    public void WithAScreenReader_ScrollingNeverHidesIt()
    {
        var entry = new NativeSamEntryAutoHide();
        entry.SetScreenReaderRunning(true);

        entry.OnScroll(100, atStart: false, atEnd: false);

        Assert.True(entry.IsShown);
    }

    [Fact]
    public void TheScreenReaderStopping_LeavesItShownUntilTheNextScrollDown()
    {
        var entry = new NativeSamEntryAutoHide();
        entry.SetScreenReaderRunning(true);
        entry.OnScroll(100, atStart: false, atEnd: false);

        Assert.False(entry.SetScreenReaderRunning(false), "nothing scrolled, so nothing moves");
        Assert.True(entry.IsShown);

        entry.OnScroll(NativeSamEntryAutoHide.Threshold, atStart: false, atEnd: false);
        Assert.False(entry.IsShown);
    }

    [Fact]
    public void TheScreenReaderStarting_BringsAHiddenButtonBack()
    {
        var entry = Hidden();

        Assert.True(entry.SetScreenReaderRunning(true));

        Assert.True(entry.IsShown);
    }

    [Fact]
    public void ScrollWhileAScreenReaderRuns_DoesNotCountAfterItStops()
    {
        var entry = new NativeSamEntryAutoHide();
        entry.SetScreenReaderRunning(true);
        entry.OnScroll(20, atStart: false, atEnd: false);
        entry.SetScreenReaderRunning(false);

        entry.OnScroll(10, atStart: false, atEnd: false);

        Assert.True(entry.IsShown, "the 20 scrolled with the screen reader on do not count");
    }

    [Fact]
    public void WithAScreenReader_TheKeyboardStillHidesIt()
    {
        var entry = new NativeSamEntryAutoHide();
        entry.SetScreenReaderRunning(true);

        entry.SetKeyboardVisible(true);

        Assert.False(entry.IsShown);
    }

    [Fact]
    public void Reset_ShowsItAndKeepsTheKeyboard()
    {
        var entry = Hidden();

        Assert.True(entry.Reset());
        Assert.True(entry.IsShown);

        entry.SetKeyboardVisible(true);
        entry.Reset();
        Assert.False(entry.IsShown);
    }

    [Fact]
    public void Reset_ClearsHalfFinishedTravel()
    {
        var entry = new NativeSamEntryAutoHide();
        entry.OnScroll(20, atStart: false, atEnd: false);

        entry.Reset();
        entry.OnScroll(10, atStart: false, atEnd: false);

        Assert.True(entry.IsShown);
    }

    private static NativeSamEntryAutoHide Hidden()
    {
        var entry = new NativeSamEntryAutoHide();
        entry.OnScroll(NativeSamEntryAutoHide.Threshold, atStart: false, atEnd: false);
        Assert.False(entry.IsShown);
        return entry;
    }
}
