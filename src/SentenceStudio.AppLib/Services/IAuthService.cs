using System.Threading.Tasks;

namespace SentenceStudio.Services;

public interface IAuthService
{
    Task<AuthResult?> SignInAsync();
    Task<AuthResult?> SignInAsync(string email, string password);
    Task<AuthResult?> RegisterAsync(string email, string password, string displayName);
    Task SignOutAsync();
    Task<string?> GetAccessTokenAsync(string[] scopes);
    bool IsSignedIn { get; }
    string? UserName { get; }
}
