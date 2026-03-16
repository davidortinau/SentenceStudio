namespace SentenceStudio.Api.Auth;

public sealed record RegisterRequest(
    string Email,
    string Password,
    string? DisplayName = null,
    string? NativeLanguage = null,
    string? TargetLanguage = null);

public sealed record LoginRequest(
    string Email,
    string Password);

public sealed record RefreshRequest(
    string RefreshToken);

public sealed record ForgotPasswordRequest(
    string Email);

public sealed record ResetPasswordRequest(
    string Email,
    string Token,
    string NewPassword);

public sealed record AuthResponse(
    string Token,
    string RefreshToken,
    DateTime ExpiresAt,
    string? UserName,
    string? UserProfileId);
