namespace SentenceStudio.Api;

/// <summary>
/// Centralized claim type constants used across API endpoints and auth handlers.
/// Replaces magic-string literals like <c>"user_profile_id"</c> scattered through
/// endpoint files. Adding new claims here ensures token issuance and consumption
/// stay in sync.
/// </summary>
public static class AuthClaimTypes
{
    /// <summary>
    /// Claim carrying the authenticated user's <c>UserProfile.Id</c>. Issued by
    /// <see cref="Auth.JwtTokenService"/> and <see cref="Auth.DevAuthHandler"/>.
    /// Consumed by every endpoint that scopes data to the calling user.
    /// </summary>
    public const string UserProfileId = "user_profile_id";
}
