using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _activitySlot;

    /// <summary>Returns true when the popped route was the activity.</summary>
    private bool OnActivityRoutesChanged(int count)
    {
        if (count != 0 || _activitySlot is null)
            return false;
        _activitySlot = null;
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
                _dashboardStack.Push("activity", _activitySlot);
            }
            else if (_activitySlot is not null)
                Replace(_activitySlot, _app.RenderActivity(_style));
        }
        else if (_activitySlot is not null)
        {
            _activitySlot = null;
            if (_dashboardStack.NavigationPath.Count > 0)
                _dashboardStack.Pop();
        }
    }
}
