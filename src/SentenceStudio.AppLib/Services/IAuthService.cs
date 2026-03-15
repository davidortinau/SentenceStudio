using System.Threading.Tasks;

namespace SentenceStudio.Services;

public interface IAuthService
{
    Task<AuthResult?> SignInAsync();
    Task SignOutAsync();
    Task<string?> GetAccessTokenAsync(string[] scopes);
    bool IsSignedIn { get; }
    string? UserName { get; }
}
