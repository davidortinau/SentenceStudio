using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Profile;
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

    internal void OpenProfile()
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != 0
            || NavigationBlocked || _settingsOpen || IsQuizOpen
            || IsResourcesFormOpen || IsSkillsOpen || IsVocabularyOpen
            || _services is null || IsProfileOpen)
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
            scope, _services.Provider.GetRequiredService<ILogger<ProfileCoordinator>>());
        _profile.Changed -= Publish;
        _profile.Changed += Publish;
        Run(async () => await _profile.OpenAsync(expectedOwner, _lifetime.Token));
    }

    internal SwiftView RenderProfile(SourceUiStyle style) =>
        _profile is { IsOpen: true } profile
            ? ProfileView.Page(profile.State, style, Text,
                new ProfileFormActions(
                    profile.SetName, profile.SetEmail,
                    profile.SetNativeLanguage, profile.SetTargetLanguage,
                    profile.SetDisplayLanguage, profile.SetSessionMinutes,
                    profile.SetTargetCefrLevel,
                    () => Run(() => profile.ReloadAsync(_lifetime.Token)),
                    profile.RequestDelete))
            : Views.Text(Text("Common_Loading"));

    internal void CancelProfileDelete() => _profile?.CancelDelete();

    internal void OnProfilePopped()
    {
        _profile?.Close();
        Publish();
    }
}
