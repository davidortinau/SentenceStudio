using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _profileSlot;
    private SwiftView? _profileDeleteAlert;
    private int _renderedProfileVersion = -1;

    /// <summary>
    /// The Profile page in the Settings detail column (its "unavailable" form with Sign Out
    /// when the profile could not be opened).
    /// </summary>
    private void UpdateProfile(bool appearanceChanged)
    {
        if (_settingsDetailSlot is null || _renderedSettingsPage != NativeSettingsPage.Profile)
            return;
        if (_profileSlot is null)
        {
            _profileSlot = Views.VStack(_app.RenderProfile(_style));
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
            Replace(_settingsDetailSlot, _profileDeleteAlert);
        }
        else if (appearanceChanged
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
