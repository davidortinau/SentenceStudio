using Microsoft.Identity.Client;

namespace SentenceStudio.Services;

public interface IAuthService
{
    Task<AuthenticationResult?> SignInAsync();
    Task SignOutAsync();
    Task<string?> GetAccessTokenAsync(string[] scopes);
    bool IsSignedIn { get; }
    string? UserName { get; }
}
