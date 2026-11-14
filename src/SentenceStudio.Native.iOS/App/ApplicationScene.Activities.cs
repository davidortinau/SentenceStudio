using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _activitySlot;
    // The activity page's overlay, which carries the title and the text under it.
    private SwiftView? _activityPage;
    // What the activity page last drew (a browse page redraws only when these change).
    private long _renderedActivityPageVersion = -1;
    private SourceUiStyle? _renderedActivityStyle;
    private double _renderedActivityWidth;
    private SwiftView? _activityToastLayer;
    private SwiftView? _activityChildToastLayer;
    private SwiftView? _renderedActivityToastLayer;
    private long _renderedActivityToastId;
    private SourceUiStyle? _renderedActivityToastStyle;
    private SwiftView? _activitySheet;
    private SwiftView? _activitySheetSlot;
    private SwiftView? _activityChildSlot;
    private long _renderedActivityChildVersion = -1;
    private SourceUiStyle? _renderedActivityChildStyle;
    private double _renderedActivityChildWidth;

    /// <summary>Returns true when the popped route was the activity (or its child page).</summary>
    private bool OnActivityRoutesChanged(int count)
    {
        if (count == 1 && _activityChildSlot is not null && _activitySlot is not null)
        {
            _activityChildSlot = null;
            ForgetChildToastLayer();
            _app.OnActivityChildPopped();
            return true;
        }
        if (count != 0 || _activitySlot is null)
            return false;
        _activityChildSlot = null;
        ForgetChildToastLayer();
        _activitySlot = null;
        _activityPage = null;
        _activityToastLayer = null;
        _renderedActivityToastLayer = null;
        _activitySheet = null;
        _activitySheetSlot = null;
        _app.OnActivityPopped();
        return true;
    }

    private void UpdateActivity()
    {
        if (_dashboardStack is null)
            return;
        if (_app.IsActivityOpen)
        {
            // A popped activity stays off the stack while its exit runs: pushing it
            // again would re-render a screen whose popped views SwiftUI already disposed.
            if (_activitySlot is null && _dashboardStack.NavigationPath.Count == 0
                && !_app.IsActivityPopPending)
            {
                _activitySlot = Views.VStack(_app.RenderActivity(_style));
                RecordActivityPage();
                // Transient toasts are layered over the page's top edge (clear of input bars); the
                // layer is empty (and shows nothing) between toasts.
                _activityToastLayer = Views.VStack().WithFlexibleWidth();
                _renderedActivityToastLayer = null;
                var page = Views.Overlay(_activitySlot, _activityToastLayer, SwiftOverlayEdge.Top)
                    .WithNavigationTitle(_app.ActivityTitle)
                    .WithNavigationSubtitle(_app.ActivitySubtitle)
                    .WithInlineNavigationTitle()
                    .WithTabBarHidden();
                _activityPage = page;
                if (_app.ActivityHasSheet)
                {
                    // One stable sheet node: re-rendering replaces only its slots.
                    _activitySheetSlot = Views.VStack(_app.RenderActivitySheet(_style));
                    _activitySheet = Views.Sheet(page, _activitySheetSlot,
                            _app.DismissActivitySheet)
                        .WithSheetDetentFraction(_app.ActivitySheetDetent)
                        .WithTabBarHidden();
                    _activitySheet.IsPresented = _app.IsActivitySheetPresented;
                    _dashboardStack.Push("activity",
                        WithSamToolbar(_dashboardStack, "activity", _activitySheet));
                }
                else
                    _dashboardStack.Push("activity",
                        WithSamToolbar(_dashboardStack, "activity", _app.PresentActivityOver(page)));
            }
            else if (_activitySlot is not null)
            {
                if (_app.ActivityBrowsePage is { } browse)
                {
                    // A browse page (Minimal Pairs) redraws only when it shows something new, in
                    // place, so its search field keeps focus and an open swipe stays open.
                    var reshaped = !ReferenceEquals(_renderedActivityStyle, _style)
                        || Math.Abs(_renderedActivityWidth - _style.ViewportWidth) >= 0.5;
                    if (reshaped || _renderedActivityPageVersion != _app.ActivityPageVersion)
                    {
                        UpdateBrowsePage(_activitySlot, _app.RenderActivity(_style), browse, reshaped,
                            _app.ActivitySearchText);
                        RecordActivityPage();
                    }
                }
                else if (!_app.TryUpdateActivityInPlace(_style))
                    Replace(_activitySlot, _app.RenderActivity(_style));
                _activityPage?.WithNavigationSubtitle(_app.ActivitySubtitle);
                if (_activitySheet is not null && _activitySheetSlot is not null)
                {
                    if (_app.IsActivitySheetPresented)
                        Replace(_activitySheetSlot, _app.RenderActivitySheet(_style));
                    _activitySheet.IsPresented = _app.IsActivitySheetPresented;
                }
            }
            if (_activitySlot is not null)
            {
                UpdateActivityChild(_dashboardStack);
                UpdateActivityToast();
            }
        }
        else if (_activitySlot is not null)
        {
            var depth = _dashboardStack.NavigationPath.Count;
            _activityChildSlot = null;
            ForgetChildToastLayer();
            _activitySlot = null;
            _activityPage = null;
            _activityToastLayer = null;
            _renderedActivityToastLayer = null;
            _activitySheet = null;
            _activitySheetSlot = null;
            for (var index = 0; index < depth && _dashboardStack.NavigationPath.Count > 0; index++)
                _dashboardStack.Pop();
        }
    }

    /// <summary>
    /// Mounts the controller's current toast in the overlay layer only when it
    /// changes (or the appearance does), so its VoiceOver announcement is posted
    /// once per toast and an unrelated re-render does not remount it.
    /// </summary>
    private void UpdateActivityToast()
    {
        var layer = _activityChildToastLayer ?? _activityToastLayer;
        if (layer is null)
            return;
        var toast = _app.ActivityToast;
        var id = toast?.Id ?? 0;
        if (ReferenceEquals(layer, _renderedActivityToastLayer) && id == _renderedActivityToastId
            && (toast is null || ReferenceEquals(_renderedActivityToastStyle, _style)))
            return;
        if (_renderedActivityToastLayer is { } previousLayer && !ReferenceEquals(previousLayer, layer))
            ClearToastLayer(previousLayer);
        ClearToastLayer(layer);
        _renderedActivityToastLayer = layer;
        _renderedActivityToastId = id;
        _renderedActivityToastStyle = _style;
        if (toast is null)
            return;
        var gutter = SourceUiLayout.PageGutter(_style.ViewportWidth);
        // A padded wrapper: the toast's own padding is inside its surface.
        layer.Add(Views.VStack(_style.MessageToast(toast, _app.LocalizedText, _app.DismissActivityToast))
            .WithFlexibleWidth()
            .WithPadding(8, gutter, 0, gutter));
    }

    /// <summary>The child page left the stack with its layer; the toast moves back to the activity page.</summary>
    private void ForgetChildToastLayer()
    {
        if (ReferenceEquals(_renderedActivityToastLayer, _activityChildToastLayer))
            _renderedActivityToastLayer = null;
        _activityChildToastLayer = null;
    }

    private static void ClearToastLayer(SwiftView layer)
    {
        while (layer.Children.Count > 0)
        {
            var previous = layer.Children[0];
            layer.RemoveAt(0);
            previous.Dispose();
        }
    }

    /// <summary>
    /// A second-level activity page pushed above the activity page. It is
    /// re-rendered only when the screen's ChildVersion or the appearance
    /// changes, so text input on it is never remounted by unrelated updates.
    /// </summary>
    private void UpdateActivityChild(SwiftView stack)
    {
        if (_app.ActivityHasChildPage)
        {
            if (_activityChildSlot is null)
            {
                if (stack.NavigationPath.Count != 1)
                    return;
                _activityChildSlot = Views.VStack(_app.RenderActivityChild(_style))
                    .WithNavigationTitle(_app.ActivityChildTitle)
                    .WithInlineNavigationTitle()
                    .WithTabBarHidden();
                RecordActivityChild();
                // The toast follows the page on top of the stack.
                _activityChildToastLayer = Views.VStack().WithFlexibleWidth();
                stack.Push("activity-child", WithSamToolbar(stack, "activity-child",
                    Views.Overlay(_activityChildSlot, _activityChildToastLayer, SwiftOverlayEdge.Top)));
                return;
            }
            if (_renderedActivityChildVersion != _app.ActivityChildVersion
                || !ReferenceEquals(_renderedActivityChildStyle, _style)
                || Math.Abs(_renderedActivityChildWidth - _style.ViewportWidth) >= 0.5)
            {
                // Create Pair swaps its Form Sections into the mounted Form, so its scroll
                // position stays; every other child page, or a new style, redraws whole.
                var next = _app.RenderActivityChild(_style);
                var reshaped = !ReferenceEquals(_renderedActivityChildStyle, _style)
                    || Math.Abs(_renderedActivityChildWidth - _style.ViewportWidth) >= 0.5;
                if (reshaped || !TrySwapFormSections(_activityChildSlot, next,
                        NativeMinimalPairsAccess.Create))
                    Replace(_activityChildSlot, next);
                RecordActivityChild();
            }
            _activityChildSlot.WithNavigationTitle(_app.ActivityChildTitle);
        }
        else if (_activityChildSlot is not null)
        {
            // Clear first: the route callback must not report this pop as a person's Back.
            _activityChildSlot = null;
            ForgetChildToastLayer();
            if (stack.NavigationPath.Count > 1)
                stack.Pop();
        }
    }

    private void RecordActivityPage()
    {
        _renderedActivityPageVersion = _app.ActivityPageVersion;
        _renderedActivityStyle = _style;
        _renderedActivityWidth = _style.ViewportWidth;
    }

    private void RecordActivityChild()
    {
        _renderedActivityChildVersion = _app.ActivityChildVersion;
        _renderedActivityChildStyle = _style;
        _renderedActivityChildWidth = _style.ViewportWidth;
    }
}
