using Microsoft.Identity.Client;

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

    public Task<AuthenticationResult?> SignInAsync() => Task.FromResult<AuthenticationResult?>(null);
    public Task SignOutAsync() => Task.CompletedTask;
    public Task<string?> GetAccessTokenAsync(string[] scopes) => Task.FromResult<string?>(null);
}
