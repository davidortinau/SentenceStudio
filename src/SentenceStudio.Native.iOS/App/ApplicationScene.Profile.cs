using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _profileSlot;
    private SwiftView? _profileDeleteAlert;
    private int _renderedProfileVersion = -1;

    private void OnProfileRoutesChanged(int count)
    {
        if (count != 0 || _profileSlot is null)
            return;
        DropProfileReferences();
        _app.OnProfilePopped();
    }

    private void UpdateProfile(bool appearanceChanged)
    {
        if (_dashboardStack is null || !_app.IsProfileOpen)
            return;
        if (_profileSlot is null && _dashboardStack.NavigationPath.Count == 0)
        {
            _profileSlot = Views.VStack(_app.RenderProfile(_style))
                .WithNavigationTitle(_app.Localize("Profile_PageTitle"))
                .WithInlineNavigationTitle();
            var cancel = Views.Button(_app.Localize("Profile_Cancel"),
                    _app.CancelProfileDelete)
                .WithAccessibilityIdentifier("native.profile.delete.cancel");
            var delete = Views.Button(_app.Localize("Profile_Delete"),
                    _app.CancelProfileDelete)
                .WithAccessibilityIdentifier("native.profile.delete.confirm");
            delete.IsEnabled = false;
            _profileDeleteAlert = Views.Alert(_profileSlot,
                    _app.Localize("Profile_ConfirmDelete"),
                    _app.Localize("Profile_DeleteConfirmationMessage"),
                    cancel, delete)
                .WithAccessibilityIdentifier("native.profile.delete.confirmation");
            _renderedProfileVersion = _app.ProfileViewVersion;
            _dashboardStack.Push("profile",
                _profileDeleteAlert.WithTabBarHidden());
        }
        if (_profileSlot is null)
            return;
        if (appearanceChanged
            || _renderedProfileVersion != _app.ProfileViewVersion)
        {
            Replace(_profileSlot, _app.RenderProfile(_style));
            _renderedProfileVersion = _app.ProfileViewVersion;
        }
        if (_profileDeleteAlert is not null)
            _profileDeleteAlert.IsPresented = _app.IsProfileDeleteRequested;
    }

    private void DropProfileReferences()
    {
        _profileSlot = null;
        _profileDeleteAlert = null;
        _renderedProfileVersion = -1;
    }
}
