using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _activitySlot;
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
            _app.OnActivityChildPopped();
            return true;
        }
        if (count != 0 || _activitySlot is null)
            return false;
        _activityChildSlot = null;
        _activitySlot = null;
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
            if (_activitySlot is null && _dashboardStack.NavigationPath.Count == 0)
            {
                _activitySlot = Views.VStack(_app.RenderActivity(_style))
                    .WithNavigationTitle(_app.ActivityTitle)
                    .WithInlineNavigationTitle()
                    .WithTabBarHidden();
                if (_app.ActivityHasSheet)
                {
                    // One stable sheet node: re-rendering replaces only its slots.
                    _activitySheetSlot = Views.VStack(_app.RenderActivitySheet(_style));
                    _activitySheet = Views.Sheet(_activitySlot, _activitySheetSlot,
                            _app.DismissActivitySheet)
                        .WithSheetDetentFraction(_app.ActivitySheetDetent)
                        .WithTabBarHidden();
                    _activitySheet.IsPresented = _app.IsActivitySheetPresented;
                    _dashboardStack.Push("activity", _activitySheet);
                }
                else
                    _dashboardStack.Push("activity", _activitySlot);
            }
            else if (_activitySlot is not null)
            {
                Replace(_activitySlot, _app.RenderActivity(_style));
                if (_activitySheet is not null && _activitySheetSlot is not null)
                {
                    if (_app.IsActivitySheetPresented)
                        Replace(_activitySheetSlot, _app.RenderActivitySheet(_style));
                    _activitySheet.IsPresented = _app.IsActivitySheetPresented;
                }
            }
            if (_activitySlot is not null)
                UpdateActivityChild(_dashboardStack);
        }
        else if (_activitySlot is not null)
        {
            var depth = _dashboardStack.NavigationPath.Count;
            _activityChildSlot = null;
            _activitySlot = null;
            _activitySheet = null;
            _activitySheetSlot = null;
            for (var index = 0; index < depth && _dashboardStack.NavigationPath.Count > 0; index++)
                _dashboardStack.Pop();
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
                stack.Push("activity-child", _activityChildSlot);
                return;
            }
            if (_renderedActivityChildVersion != _app.ActivityChildVersion
                || !ReferenceEquals(_renderedActivityChildStyle, _style)
                || Math.Abs(_renderedActivityChildWidth - _style.ViewportWidth) >= 0.5)
            {
                Replace(_activityChildSlot, _app.RenderActivityChild(_style));
                RecordActivityChild();
            }
            _activityChildSlot.WithNavigationTitle(_app.ActivityChildTitle);
        }
        else if (_activityChildSlot is not null)
        {
            // Clear first: the route callback must not report this pop as a person's Back.
            _activityChildSlot = null;
            if (stack.NavigationPath.Count > 1)
                stack.Pop();
        }
    }

    private void RecordActivityChild()
    {
        _renderedActivityChildVersion = _app.ActivityChildVersion;
        _renderedActivityChildStyle = _style;
        _renderedActivityChildWidth = _style.ViewportWidth;
    }
}
