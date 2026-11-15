using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Profile;
using SentenceStudio.Native.iOS.Features.Settings;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationController
{
    private ProfileCoordinator? _profile;

    internal bool IsProfileOpen => _profile?.IsOpen == true;
    internal int ProfileViewVersion => _profile?.ViewVersion ?? -1;
    internal bool IsProfileDeleteRequested =>
        _profile?.State.ShowDeleteConfirmation == true;

    /// <summary>The profile is open with edits not saved yet (or a save running).</summary>
    internal bool IsProfileEditing => _profile?.HasUnsavedChanges == true;

    private void OpenProfile()
    {
        // The Settings sidebar's profile row (SelectSettingsPage).
        if (_destination != NativeDestination.Dashboard || _selectedTab != NativeTabs.Settings
            || NavigationBlocked || IsQuizOpen || _services is null || IsProfileOpen)
            return;
        var expectedOwner = _authState is {
            Phase: NativePhase.Ready,
            Destination: NativeDestination.Dashboard,
            UserId: { Length: > 0 } authenticated
        } ? authenticated : null;
#if DEBUG
        if (expectedOwner is null && _preview
            && NSBundle.MainBundle.BundleIdentifier
                == "com.simplyprofound.sentencestudio.native.fixture")
            expectedOwner = NativePreviewFixture.FromAssembly()?.Owner;
#endif
        var scope = _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !scope.TryGetUserProfileId(out var activeOwner)
            || !string.Equals(expectedOwner, activeOwner, StringComparison.Ordinal))
        {
            _logger?.LogWarning("Profile navigation refused an absent or untrusted owner.");
            return;
        }
        _profile ??= new ProfileCoordinator(
            _services.Provider.GetRequiredService<UserProfileRepository>(),
            scope, _services.Provider.GetRequiredService<ILogger<ProfileCoordinator>>(),
            _services.Provider.GetRequiredService<INativeProfileRemoteStore>());
        _profile.Changed -= Publish;
        _profile.Changed += Publish;
        Run(async () => await _profile.OpenAsync(expectedOwner, _lifetime.Token));
    }

    /// <summary>
    /// The Profile page, Sign Out last; when the profile could not be opened it says so, with
    /// Retry, above Sign Out.
    /// </summary>
    internal SwiftView RenderProfile(SourceUiStyle style) =>
        _profile is { IsOpen: true } profile
            ? ProfileView.Page(profile.State, style, Text,
                new ProfileFormActions(
                    profile.SetName,
                    profile.SetNativeLanguage, profile.SetTargetLanguage,
                    profile.SetDisplayLanguage, profile.SetSessionMinutes,
                    profile.SetTargetCefrLevel,
                    () => Run(() => profile.ReloadAsync(_lifetime.Token)),
                    profile.RequestDelete,
                    () => Run(async () =>
                    {
                        // A saved language creates its smart resources on this device.
                        if (await profile.SaveAsync(_lifetime.Token))
                        {
                            _resources?.NoteResourcesWritten();
                            // The sidebar's profile row shows beside the page: it follows the save.
                            await RefreshProfileSummaryAsync();
                        }
                    })), SignOutRow(style))
            : ProfileView.Unavailable(style, Text, RetryProfile, SignOutRow(style));

    /// <summary>Sign Out; off while the profile saves (the save finishes first).</summary>
    internal SwiftView SignOutRow(SourceUiStyle style) =>
        SettingsView.SignOut(style, SignOutTitle,
            enabled: _profile?.State.IsSaving != true, SignOut);

    private void RetryProfile()
    {
        OpenProfile();
        Publish();
    }

    internal void CancelProfileDelete() => _profile?.CancelDelete();
}
