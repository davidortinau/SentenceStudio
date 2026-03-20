using System.Threading.Tasks;

namespace SentenceStudio.Services;

/// <summary>
/// No-op auth service for local development. Always reports as signed in
/// so UI flows aren't blocked, but returns null tokens (the server's
/// DevAuthHandler takes care of creating a synthetic identity).
/// </summary>
public class DevAuthService : IAuthService
{
    public bool IsSignedIn => true;
    public string? UserName => "dev@localhost";

    public Task<AuthResult?> SignInAsync() => Task.FromResult<AuthResult?>(null);
    public Task<AuthResult?> SignInAsync(string email, string password) => Task.FromResult<AuthResult?>(null);
    public Task<AuthResult?> RegisterAsync(string email, string password, string displayName) => Task.FromResult<AuthResult?>(null);
    public Task SignOutAsync() => Task.CompletedTask;
    public Task<bool> DeleteAccountAsync() => Task.FromResult(true);
    public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) => Task.FromResult(true);
    public Task<string?> GetAccessTokenAsync(string[] scopes) => Task.FromResult<string?>(null);
}
