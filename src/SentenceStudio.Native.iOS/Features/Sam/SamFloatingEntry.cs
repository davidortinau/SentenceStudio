using Microsoft.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Sam;

/// <summary>
/// The floating Sam button on one scrolling screen (the Dashboard or Resources), driven by the
/// shared <see cref="NativeSamEntryAutoHide"/> rule (docs/sam-entry.md). SwiftUI reports the
/// screen's scroll geometry, the on-screen keyboard and VoiceOver; the button slides down and
/// fades away (it only fades with Reduce Motion), keeps its room in the bottom inset so nothing
/// moves, and while away cannot be tapped and is hidden from VoiceOver.
/// </summary>
/// <remarks>
/// One instance per screen. A page that is built and then discarded (the Resources incremental
/// update keeps the mounted page) changes nothing: the rule moves only on the mounted page's
/// reports, and a page that appears (a new page, a tab switch, a return from a pushed screen)
/// starts with the button shown.
/// </remarks>
internal sealed class SamFloatingEntry
{
    private readonly NativeSamEntryAutoHide _rule = new();

    internal bool IsShown => _rule.IsShown;

    /// <summary>
    /// Wires a <see cref="Microsoft.SwiftUI.SwiftUI.BottomInset"/> whose first child is the
    /// screen's scroll and whose second is the floating button's row.
    /// </summary>
    internal SwiftView Bind(SwiftView inset)
    {
        ArgumentNullException.ThrowIfNull(inset);
        if (inset.Kind != "bottomInset" || inset.Children.Count != 2
            || inset.Children[0].Kind != "scroll")
            throw new ArgumentException("Expected a bottom inset around a scroll.", nameof(inset));
        var binding = new Binding(_rule, inset.Children[1]);
        inset.Children[0].OnScrollGeometryChanged(binding.Scrolled);
        inset.OnAppear(binding.Appeared)
            .OnKeyboardOverlapChanged(binding.KeyboardOverlapChanged)
            .OnAccessibilitySettingsChanged(binding.AccessibilitySettingsChanged);
        return inset;
    }

    /// <summary>One mounted page: its button and the last scroll offset it reported.</summary>
    internal sealed class Binding
    {
        private readonly NativeSamEntryAutoHide _rule;
        private readonly SwiftView _entry;
        private double? _lastOffset;

        internal Binding(NativeSamEntryAutoHide rule, SwiftView entry)
        {
            _rule = rule;
            _entry = entry;
            // Set before the first render so the button's identity never changes later.
            _entry.WithHidden(!_rule.IsShown, SwiftHideTransition.SlideDown);
        }

        internal void Appeared()
        {
            _lastOffset = null;
            // The keyboard state is kept on purpose. Returning to Resources gives its search
            // field focus back, and the keyboard with it, at the same overlap the bridge last
            // reported, so no new report arrives; clearing the flag here showed the button
            // behind the keyboard (observed on the simulator, 2026-11-04). A keyboard that
            // closes while the page is visible is always reported.
            _rule.Reset();
            Apply();
        }

        internal void Scrolled(SwiftScrollGeometry geometry)
        {
            // Positive when the content moved toward its end (the learner scrolled down).
            var delta = _lastOffset is { } last ? geometry.ContentOffsetY - last : 0;
            _lastOffset = geometry.ContentOffsetY;
            _rule.OnScroll(delta, geometry.IsAtTop, geometry.IsAtBottom);
            Apply();
        }

        internal void KeyboardOverlapChanged(double points)
        {
            _rule.SetKeyboardVisible(points > 0);
            Apply();
        }

        internal void AccessibilitySettingsChanged(SwiftAccessibilitySettings settings)
        {
            _rule.SetScreenReaderRunning(settings.HasFlag(SwiftAccessibilitySettings.VoiceOver));
            Apply();
        }

        /// <summary>Idempotent: the button always ends up where the rule says.</summary>
        private void Apply()
        {
            var hidden = !_rule.IsShown;
            if (_entry.IsHidden != hidden)
                _entry.WithHidden(hidden, SwiftHideTransition.SlideDown);
        }
    }
}
