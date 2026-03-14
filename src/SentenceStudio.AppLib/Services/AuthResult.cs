using System;

namespace SentenceStudio.Services;

/// <summary>
/// Abstraction over authentication results so the IAuthService interface
/// does not leak any identity-provider-specific types (e.g. MSAL).
/// </summary>
public sealed record AuthResult(
    string AccessToken,
    string? UserName,
    DateTimeOffset ExpiresOn);
