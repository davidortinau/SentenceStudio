using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace SentenceStudio.Services;

public class MsalAuthService : IAuthService
{
    private const string TenantId = "49c0cd14-bc68-4c6d-b87b-9d65a56fa6df";
    private const string ClientId = "68d5abeb-9ca7-46cc-9572-42e33f15a0ba";
    private const string RedirectUri = "msal68d5abeb-9ca7-46cc-9572-42e33f15a0ba://auth";

    private static string[] DefaultScopes => AuthConstants.DefaultScopes;

    private readonly IPublicClientApplication _pca;
    private readonly ILogger<MsalAuthService> _logger;
    private IAccount? _cachedAccount;

    public bool IsSignedIn => _cachedAccount is not null;
    public string? UserName => _cachedAccount?.Username;

    public MsalAuthService(ILogger<MsalAuthService> logger)
    {
        _logger = logger;

        _pca = PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, TenantId)
            .WithRedirectUri(RedirectUri)
            .Build();
    }

    public async Task<AuthResult?> SignInAsync()
    {
        try
        {
            var result = await AcquireTokenAsync(DefaultScopes);
            if (result is not null)
            {
                _cachedAccount = result.Account;
                _logger.LogInformation("Signed in as {User}", result.Account.Username);
                return new AuthResult(
                    result.AccessToken,
                    result.Account.Username,
                    result.ExpiresOn);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in failed");
            return null;
        }
    }

    public async Task SignOutAsync()
    {
        try
        {
            var accounts = await _pca.GetAccountsAsync();
            foreach (var account in accounts)
            {
                await _pca.RemoveAsync(account);
            }
            _cachedAccount = null;
            _logger.LogInformation("Signed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-out failed");
        }
    }

    public async Task<string?> GetAccessTokenAsync(string[] scopes)
    {
        try
        {
            var result = await AcquireTokenAsync(scopes);
            return result?.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire access token");
            return null;
        }
    }

    private async Task<AuthenticationResult?> AcquireTokenAsync(string[] scopes)
    {
        // Try silent acquisition first
        var accounts = await _pca.GetAccountsAsync();
        var account = _cachedAccount ?? accounts.FirstOrDefault();

        if (account is not null)
        {
            try
            {
                return await _pca.AcquireTokenSilent(scopes, account).ExecuteAsync();
            }
            catch (MsalUiRequiredException)
            {
                _logger.LogDebug("Silent token acquisition failed, falling back to interactive");
            }
        }

        // Fall back to interactive (system browser with PKCE)
        try
        {
            return await _pca.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync();
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            _logger.LogInformation("User cancelled authentication");
            return null;
        }
    }
}
