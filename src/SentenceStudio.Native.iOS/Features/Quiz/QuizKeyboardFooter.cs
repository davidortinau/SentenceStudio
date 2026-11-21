using Microsoft.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Quiz;

/// <summary>
/// The quiz's counts footer gives its room to the question while the on-screen keyboard is up
/// (issue 254, Captain 2026-11-20): a bottom inset rides above the keyboard, and on a
/// landscape phone that left no room for the answer field and its buttons. The rows leave and
/// return in place, so the page is not rebuilt and the field keeps its focus.
/// </summary>
internal static class QuizKeyboardFooter
{
    /// <summary>
    /// Wires a <see cref="Microsoft.SwiftUI.SwiftUI.BottomInset"/> whose second child is the
    /// footer's stack. <paramref name="keyboardVisible"/> is what the last page reported, so a
    /// page built while the keyboard is up opens without the footer.
    /// </summary>
    internal static SwiftView Bind(
        SwiftView inset, bool keyboardVisible, Action<bool> keyboardVisibleChanged)
    {
        ArgumentNullException.ThrowIfNull(inset);
        ArgumentNullException.ThrowIfNull(keyboardVisibleChanged);
        if (inset.Kind != "bottomInset" || inset.Children.Count != 2
            || inset.Children[1].Kind != "vstack")
            throw new ArgumentException("Expected a bottom inset with a stacked footer.", nameof(inset));
        var footer = inset.Children[1];
        var rows = footer.Children.ToArray();
        var shown = true;
        void Apply(bool visible)
        {
            if (shown != visible)
                return;
            shown = !visible;
            if (visible)
                footer.Clear();
            else
                foreach (var row in rows)
                    footer.Add(row);
        }
        Apply(keyboardVisible);
        return inset.OnKeyboardOverlapChanged(points =>
        {
            var visible = points > 0;
            keyboardVisibleChanged(visible);
            Apply(visible);
        });
    }
}
