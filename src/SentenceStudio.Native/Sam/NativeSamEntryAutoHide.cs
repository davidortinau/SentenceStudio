namespace SentenceStudio.Native;

/// <summary>
/// Whether the floating Sam button is shown on a scrolling screen (docs/sam-entry.md).
/// It slides away when the learner scrolls down, and comes back when they scroll up or
/// reach the top or the end of the screen. It stays away while the keyboard is up, and
/// stays shown while a screen reader runs.
/// </summary>
/// <remarks>
/// One instance per floating screen. Heads report scroll movement in their own units
/// (points, dp); the threshold is the same number in each. The web follows the same
/// rule in its Sam script, so a change here must change docs/sam-entry.md and the web.
/// </remarks>
public sealed class NativeSamEntryAutoHide
{
    /// <summary>
    /// How far a scroll must travel in one direction before the button moves (points, dp
    /// or CSS pixels), so small jitters and slow drags do not make it flicker.
    /// </summary>
    public const double Threshold = 24;

    private double _travel;
    private bool _scrolledAway;
    private bool _keyboardVisible;
    private bool _screenReaderRunning;

    /// <summary>True when the floating button should be on screen.</summary>
    public bool IsShown => !_keyboardVisible && (_screenReaderRunning || !_scrolledAway);

    /// <summary>
    /// Reports one scroll step. <paramref name="delta"/> is positive when the content
    /// moved toward its end (the learner scrolled down). <paramref name="atStart"/> and
    /// <paramref name="atEnd"/> say whether the screen is now at its top or its end; a
    /// screen that cannot scroll is at both.
    /// </summary>
    /// <returns>True when <see cref="IsShown"/> changed.</returns>
    public bool OnScroll(double delta, bool atStart, bool atEnd)
    {
        var before = IsShown;
        if (atStart || atEnd)
        {
            // The end has room left for the button, so it never covers the last item there.
            _scrolledAway = false;
            _travel = 0;
        }
        else if (double.IsFinite(delta) && delta != 0)
        {
            if (Math.Sign(delta) != Math.Sign(_travel))
                _travel = 0;
            _travel += delta;
            if (_travel >= Threshold)
                _scrolledAway = true;
            else if (_travel <= -Threshold)
                _scrolledAway = false;
        }
        return before != IsShown;
    }

    /// <summary>The on-screen keyboard appeared or went away.</summary>
    /// <returns>True when <see cref="IsShown"/> changed.</returns>
    public bool SetKeyboardVisible(bool visible)
    {
        var before = IsShown;
        _keyboardVisible = visible;
        return before != IsShown;
    }

    /// <summary>
    /// A screen reader (VoiceOver, TalkBack) started or stopped. While one runs the button
    /// does not slide away: those learners move through the screen by swiping, and a
    /// button that keeps leaving is hard to find.
    /// </summary>
    /// <returns>True when <see cref="IsShown"/> changed.</returns>
    public bool SetScreenReaderRunning(bool running)
    {
        var before = IsShown;
        _screenReaderRunning = running;
        return before != IsShown;
    }

    /// <summary>
    /// A new screen or tab is showing: the button starts shown. The keyboard and screen
    /// reader states belong to the device, so they are kept.
    /// </summary>
    /// <returns>True when <see cref="IsShown"/> changed.</returns>
    public bool Reset()
    {
        var before = IsShown;
        _scrolledAway = false;
        _travel = 0;
        return before != IsShown;
    }
}
