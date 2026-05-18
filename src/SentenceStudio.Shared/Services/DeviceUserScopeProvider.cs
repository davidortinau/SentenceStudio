using SentenceStudio.Services.Plans;

namespace SentenceStudio.Services;

/// <summary>
/// MAUI client implementation of <see cref="IUserScopeProvider"/>. Wraps the
/// single-active-user model: returns the currently active profile id stored
/// by the app (set after sign-in, cleared on sign-out). Throws
/// <see cref="UnauthorizedAccessException"/> when no user is active.
/// </summary>
public sealed class DeviceUserScopeProvider : IUserScopeProvider
{
    private string? _activeUserProfileId;

    /// <summary>
    /// Set by the auth / post-login flow once the active profile is known.
    /// Pass <c>null</c> on sign-out.
    /// </summary>
    public void SetActiveUser(string? userProfileId)
    {
        _activeUserProfileId = string.IsNullOrWhiteSpace(userProfileId) ? null : userProfileId;
    }

    public string UserProfileId
    {
        get
        {
            if (TryGetUserProfileId(out var id))
            {
                return id;
            }
            throw new UnauthorizedAccessException(
                "No active user profile is set on the device.");
        }
    }

    public bool TryGetUserProfileId(out string userProfileId)
    {
        if (string.IsNullOrWhiteSpace(_activeUserProfileId))
        {
            userProfileId = string.Empty;
            return false;
        }
        userProfileId = _activeUserProfileId!;
        return true;
    }
}
