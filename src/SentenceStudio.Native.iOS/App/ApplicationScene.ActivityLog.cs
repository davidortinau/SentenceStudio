using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.ActivityLog;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _activityLogSlot;
    private SwiftView? _activityLogToolbar;
    private int _renderedActivityLogVersion = -1;

    private void OnActivityLogRoutesChanged(int count)
    {
        if (count != 0 || _activityLogSlot is null)
            return;
        DropActivityLogReferences();
        _app.OnActivityLogPopped();
    }

    private void UpdateActivityLog(bool appearanceChanged)
    {
        if (_dashboardStack is null || !_app.IsActivityLogOpen)
            return;
        if (_activityLogSlot is null && _dashboardStack.NavigationPath.Count == 0)
        {
            // Keep the mounted toolbar and navigation destination stable. Replace
            // only its inner SwiftUI scroll child when owned data changes.
            _activityLogSlot = Views.VStack(_app.RenderActivityLog(_style))
                .WithNavigationTitle(_app.Localize("ActivityLog_Title"))
                .WithInlineNavigationTitle();
            var state = _app.ActivityLogState;
            _activityLogToolbar = Views.Toolbar(
                _activityLogSlot,
                ActivityLogView.Refresh(_style, _app.Localize,
                    _app.RefreshActivityLog),
                ActivityLogView.Filters(state, _style, _app.Localize,
                    _app.FilterActivityLog));
            _renderedActivityLogVersion = _app.ActivityLogViewVersion;
            _dashboardStack.Push("activity-log",
                _activityLogToolbar.WithTabBarHidden());
        }
        if (_activityLogSlot is null || _activityLogToolbar is null)
            return;
        if (appearanceChanged
            || _renderedActivityLogVersion != _app.ActivityLogViewVersion)
        {
            Replace(_activityLogSlot, _app.RenderActivityLog(_style));
            _renderedActivityLogVersion = _app.ActivityLogViewVersion;
        }
        ActivityLogView.UpdateToolbar(
            _activityLogToolbar, _app.ActivityLogState, _style);
        Find(_activityLogToolbar, "native.activity-log.refresh")?.WithTintColor(
            _style.Appearance.Primary.Light, _style.Appearance.Primary.Dark);
    }

    private void DropActivityLogReferences()
    {
        _activityLogSlot = null;
        _activityLogToolbar = null;
        _renderedActivityLogVersion = -1;
    }
}
