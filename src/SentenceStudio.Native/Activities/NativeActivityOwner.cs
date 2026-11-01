using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Native;

/// <summary>
/// The active owner exists only when the device user scope and the persisted
/// active profile agree. Activities check it before every read and write and
/// fail closed when it is absent or has changed.
/// </summary>
public sealed class NativeActivityOwner(
    DeviceUserScopeProvider scope, IPreferencesService preferences)
{
    public bool TryCurrent(out string owner)
    {
        if (scope.TryGetUserProfileId(out owner)
            && !string.IsNullOrWhiteSpace(owner)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal))
            return true;
        owner = string.Empty;
        return false;
    }

    public bool IsActive(string? owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && TryCurrent(out var current)
        && string.Equals(current, owner, StringComparison.Ordinal);
}
