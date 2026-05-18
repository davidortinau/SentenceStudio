namespace SentenceStudio.Services.Plans;

/// <summary>
/// Resolves the active <c>UserProfileId</c> for the current request (server)
/// or device session (client). Implementations MUST fail closed on the
/// server: a missing or empty user id throws <see cref="UnauthorizedAccessException"/>
/// — never fall back to "first profile" or unfiltered data.
/// </summary>
public interface IUserScopeProvider
{
    /// <summary>
    /// The active user profile id. Throws <see cref="UnauthorizedAccessException"/>
    /// when unavailable on the server.
    /// </summary>
    string UserProfileId { get; }

    /// <summary>
    /// Non-throwing variant for code paths that can degrade gracefully
    /// (e.g. anonymous health checks).
    /// </summary>
    bool TryGetUserProfileId(out string userProfileId);
}
