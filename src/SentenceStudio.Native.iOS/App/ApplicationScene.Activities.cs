using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _activitySlot;
    private SwiftView? _activitySheet;
    private SwiftView? _activitySheetSlot;

    /// <summary>Returns true when the popped route was the activity.</summary>
    private bool OnActivityRoutesChanged(int count)
    {
        if (count != 0 || _activitySlot is null)
            return false;
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
                if (!_app.TryUpdateActivityInPlace(_style))
                    Replace(_activitySlot, _app.RenderActivity(_style));
                if (_activitySheet is not null && _activitySheetSlot is not null)
                {
                    if (_app.IsActivitySheetPresented)
                        Replace(_activitySheetSlot, _app.RenderActivitySheet(_style));
                    _activitySheet.IsPresented = _app.IsActivitySheetPresented;
                }
            }
        }
        else if (_activitySlot is not null)
        {
            _activitySlot = null;
            _activitySheet = null;
            _activitySheetSlot = null;
            if (_dashboardStack.NavigationPath.Count > 0)
                _dashboardStack.Pop();
        }
    }
}
