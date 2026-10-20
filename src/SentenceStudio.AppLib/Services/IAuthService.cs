using System.Threading.Tasks;

namespace SentenceStudio.Services;

public interface IAuthService
{
    Task<AuthResult?> SignInAsync();
    Task<AuthResult?> SignInAsync(string email, string password);
    Task<AuthResult?> RegisterAsync(string email, string password, string displayName);
    Task<AuthRegistrationResult> RegisterWithOutcomeAsync(
        string email, string password, string displayName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AuthRegistrationResult(AuthRegistrationOutcome.Unavailable));
    Task<AuthPasswordRecoveryResult> RequestPasswordRecoveryAsync(
        string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AuthPasswordRecoveryResult(AuthPasswordRecoveryOutcome.Unavailable));
    Task SignOutAsync();
    Task<bool> DeleteAccountAsync();
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword);
    Task<string?> GetAccessTokenAsync(string[] scopes);
    bool IsSignedIn { get; }
    string? UserName { get; }

    /// <summary>
    /// Returns true if a refresh token exists in persistent storage,
    /// meaning the user has an active session even if the JWT is expired.
    /// </summary>
    Task<bool> HasStoredSessionAsync();
}
