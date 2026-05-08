namespace SentenceStudio.Contracts;

/// <summary>
/// Centralized claim type constants used across API endpoints, auth handlers,
/// and any token issuer (Api JwtTokenService, WebApp ServerAuthService, etc.).
/// Lives in <c>SentenceStudio.Contracts</c> because claim names are part of the
/// wire contract between issuer and consumer — both producers and consumers
/// must agree on the exact string.
/// Replaces magic-string literals like <c>"user_profile_id"</c> scattered through
/// endpoint files.
/// </summary>
public static class AuthClaimTypes
{
    /// <summary>
    /// Claim carrying the authenticated user's <c>UserProfile.Id</c>. Issued by
    /// the API's JwtTokenService / DevAuthHandler and the WebApp's
    /// ServerAuthService. Consumed by every endpoint that scopes data to the
    /// calling user.
    /// </summary>
    public const string UserProfileId = "user_profile_id";
}
