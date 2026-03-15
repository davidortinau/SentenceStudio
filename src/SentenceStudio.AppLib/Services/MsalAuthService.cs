using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace SentenceStudio.Services;

public class MsalAuthService : IAuthService
{
    private readonly string[] _defaultScopes;
    private readonly IPublicClientApplication _pca;
    private readonly ILogger<MsalAuthService> _logger;
    private IAccount? _cachedAccount;

    public bool IsSignedIn => _cachedAccount is not null;
    public string? UserName => _cachedAccount?.Username;

    public MsalAuthService(IConfiguration configuration, ILogger<MsalAuthService> logger)
    {
        _logger = logger;

        var tenantId = configuration["AzureAd:TenantId"]
            ?? throw new InvalidOperationException("AzureAd:TenantId must be configured.");
        var clientId = configuration["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("AzureAd:ClientId must be configured.");
        var redirectUri = configuration["AzureAd:RedirectUri"]
            ?? $"msal{clientId}://auth";

        _defaultScopes = configuration.GetSection("AzureAd:Scopes").Get<string[]>()
            ?? throw new InvalidOperationException(
                "AzureAd:Scopes must be configured. Add an array of API scopes to appsettings.json or user-secrets.");

        _pca = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithRedirectUri(redirectUri)
            .Build();
    }

    public async Task<AuthResult?> SignInAsync()
    {
        try
        {
            var result = await AcquireTokenAsync(_defaultScopes);
            if (result is not null)
            {
                _cachedAccount = result.Account;
                _logger.LogInformation("Signed in as {User}", result.Account.Username);
            }
            return result is not null
                ? new AuthResult(result.AccessToken, result.Account.Username, result.ExpiresOn)
                : null;
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
                var result = await _pca.AcquireTokenSilent(scopes, account).ExecuteAsync();
                _cachedAccount = result.Account;
                return result;
            }
            catch (MsalUiRequiredException)
            {
                _logger.LogDebug("Silent token acquisition failed, falling back to interactive");
            }
        }

        // Fall back to interactive (system browser with PKCE)
        try
        {
            var result = await _pca.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync();
            _cachedAccount = result.Account;
            return result;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            _logger.LogInformation("User cancelled authentication");
            return null;
        }
    }
}
