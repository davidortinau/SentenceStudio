using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// Auth service that authenticates against the API's ASP.NET Identity endpoints
/// using email/password credentials and JWT tokens.
/// </summary>
public sealed class IdentityAuthService : IAuthService
{
    private const string JwtKey = "auth_jwt";
    private const string RefreshKey = "auth_refresh";
    private const string ExpiresKey = "auth_expires";

    private readonly HttpClient _http;
    private readonly ISecureStorageService _secureStorage;
    private readonly IPreferencesService _preferences;
    private readonly ILogger<IdentityAuthService> _logger;
    private readonly ISyncService? _syncService;

    private string? _cachedToken;
    private DateTimeOffset _cachedExpires;
    private string? _cachedUserName;

    public IdentityAuthService(
        IHttpClientFactory httpClientFactory,
        ISecureStorageService secureStorage,
        IPreferencesService preferences,
        ILogger<IdentityAuthService> logger,
        ISyncService? syncService = null)
    {
        _http = httpClientFactory.CreateClient("AuthClient");
        _secureStorage = secureStorage;
        _preferences = preferences;
        _logger = logger;
        _syncService = syncService;
    }

    public bool IsSignedIn => _cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow;

    public string? UserName => _cachedUserName;

    /// <inheritdoc/>
    public async Task<bool> HasStoredSessionAsync()
    {
        Console.WriteLine($"[AUTH-DIAG] HasStoredSessionAsync: IsSignedIn={IsSignedIn}");
        if (IsSignedIn)
            return true;
        try
        {
            var refreshToken = await _secureStorage.GetAsync(RefreshKey);
            Console.WriteLine($"[AUTH-DIAG] HasStoredSessionAsync: refreshToken={(refreshToken is null ? "NULL" : refreshToken.Length > 0 ? $"exists({refreshToken.Length} chars)" : "EMPTY")}");
            return !string.IsNullOrEmpty(refreshToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUTH-DIAG] HasStoredSessionAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Silent sign-in: first tries to restore a valid JWT from SecureStorage (no network),
    /// then falls back to refresh token if the JWT is expired or expiring soon.
    /// Returns null if no stored tokens or refresh fails (UI should show login).
    /// </summary>
    public async Task<AuthResult?> SignInAsync()
    {
        try
        {
            // First, try to restore from stored JWT without a network call
            var storedJwt = await _secureStorage.GetAsync(JwtKey);
            var storedExpiresStr = await _secureStorage.GetAsync(ExpiresKey);

            if (!string.IsNullOrEmpty(storedJwt) && !string.IsNullOrEmpty(storedExpiresStr)
                && DateTimeOffset.TryParse(storedExpiresStr, out var storedExpires)
                && storedExpires > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                // Stored JWT is still valid with comfortable margin — restore without network
                _cachedToken = storedJwt;
                _cachedExpires = storedExpires;
                _cachedUserName = ExtractUserNameFromJwt(storedJwt);

                // Restore active profile from preferences if available
                _logger.LogInformation("Restored session from stored JWT, expires {Expires}", storedExpires);
                return new AuthResult(storedJwt, _cachedUserName, storedExpires);
            }

            // JWT missing or expiring soon — try refresh token
            var refreshToken = await _secureStorage.GetAsync(RefreshKey);
            if (string.IsNullOrEmpty(refreshToken))
                return null;

            return await RefreshTokenAsync(refreshToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Silent sign-in failed");
            return null;
        }
    }

    /// <summary>
    /// Sign in with email and password against POST /api/auth/login.
    /// Returns null only for genuine auth failures (wrong credentials).
    /// Throws for connectivity/infrastructure errors so the UI can show a distinct message.
    /// </summary>
    public async Task<AuthResult?> SignInAsync(string email, string password)
    {
        try
        {
            _logger.LogInformation("Attempting login to {BaseAddress}/api/auth/login for {Email}",
                _http.BaseAddress, email);

            var response = await _http.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Login failed with status {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var authResponse = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
            if (authResponse is null)
                return null;

            await StoreTokens(authResponse);
            return ToAuthResult(authResponse);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Login HTTP error — cannot reach API at {BaseAddress}", _http.BaseAddress);
            throw; // Let UI show a connectivity-specific message
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in with credentials failed unexpectedly");
            throw; // Let UI show a generic error
        }
    }

    /// <summary>
    /// Register a new account via POST /api/auth/register.
    /// On success returns an AuthResult if the API auto-logs-in, or null
    /// if the user needs to confirm their email first.
    /// </summary>
    public async Task<AuthResult?> RegisterAsync(string email, string password, string displayName)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/register", new
            {
                Email = email,
                Password = password,
                DisplayName = displayName
            });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Registration failed with status {Status}", response.StatusCode);
                return null;
            }

            // Some APIs return tokens on register; try to read them
            try
            {
                var authResponse = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
                if (authResponse?.Token is not null)
                {
                    await StoreTokens(authResponse);
                    return ToAuthResult(authResponse);
                }
            }
            catch
            {
                // Registration succeeded but no token returned (email confirmation required)
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed");
            return null;
        }
    }

    public async Task SignOutAsync()
    {
        _cachedToken = null;
        _cachedExpires = DateTimeOffset.MinValue;
        _cachedUserName = null;

        _secureStorage.Remove(JwtKey);
        _secureStorage.Remove(RefreshKey);
        _secureStorage.Remove(ExpiresKey);
        _preferences.Remove("active_profile_id");

        _logger.LogInformation("Signed out, tokens and profile cleared");
    }

    public async Task<bool> DeleteAccountAsync()
    {
        try
        {
            var response = await _http.DeleteAsync("/api/auth/account");
            if (response.IsSuccessStatusCode)
            {
                await SignOutAsync();
                return true;
            }
            _logger.LogWarning("Account deletion failed: {Status}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account deletion failed");
            return false;
        }
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/change-password",
                new { CurrentPassword = currentPassword, NewPassword = newPassword });

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Password changed successfully");
                return true;
            }

            _logger.LogWarning("Password change failed: {Status}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password change failed");
            return false;
        }
    }

    /// <summary>
    /// Returns a valid JWT access token. If the cached token is expired,
    /// attempts a refresh. Returns null if no valid token is available.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(string[] scopes)
    {
        // Return cached token if still valid (with 60s buffer)
        if (_cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow.AddSeconds(60))
            return _cachedToken;

        // Try refresh
        try
        {
            var refreshToken = await _secureStorage.GetAsync(RefreshKey);
            if (string.IsNullOrEmpty(refreshToken))
                return null;

            var result = await RefreshTokenAsync(refreshToken);
            return result?.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token refresh failed");
            return null;
        }
    }

    private async Task<AuthResult?> RefreshTokenAsync(string refreshToken)
    {
        HttpResponseMessage response;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            response = await _http.PostAsJsonAsync("/api/auth/refresh",
                new { RefreshToken = refreshToken }, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Transient failure (network error, timeout) — keep the refresh token
            // so the next attempt can try again. Do NOT destroy the session.
            _logger.LogWarning(ex, "Token refresh failed due to transient error — keeping refresh token for retry");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode == 401 || statusCode == 403)
            {
                // Server explicitly rejected the refresh token — it's invalid/revoked
                _logger.LogWarning("Token refresh rejected with {Status} — clearing refresh token", response.StatusCode);
                _secureStorage.Remove(RefreshKey);
                _cachedToken = null;
                _cachedExpires = DateTimeOffset.MinValue;
                _cachedUserName = null;
            }
            else
            {
                // Server error (5xx) or other non-auth failure — keep the refresh token
                _logger.LogWarning("Token refresh returned {Status} — keeping refresh token for retry", response.StatusCode);
            }
            return null;
        }

        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        if (authResponse is null)
            return null;

        await StoreTokens(authResponse);
        return ToAuthResult(authResponse);
    }

    private async Task StoreTokens(AuthResponseDto response)
    {
        _cachedToken = response.Token;
        _cachedExpires = new DateTimeOffset(response.ExpiresAt, TimeSpan.Zero);
        _cachedUserName = response.UserName ?? ExtractUserNameFromJwt(response.Token);

        await _secureStorage.SetAsync(JwtKey, response.Token);
        await _secureStorage.SetAsync(RefreshKey, response.RefreshToken);
        await _secureStorage.SetAsync(ExpiresKey, response.ExpiresAt.ToString("O"));

        // Set the active profile so all repositories filter by the correct user
        if (!string.IsNullOrEmpty(response.UserProfileId))
        {
            _preferences.Set("active_profile_id", response.UserProfileId);
            _logger.LogInformation("Active profile set to {ProfileId}", response.UserProfileId);
        }
        else
        {
            _logger.LogWarning("Login response missing UserProfileId — data queries may return empty");
        }

        _logger.LogInformation("Tokens stored, expires at {Expires}", _cachedExpires);

        // Trigger sync after successful login to pull down server data
        if (_syncService != null)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _syncService.TriggerSyncAsync();
                        _logger.LogInformation("[CoreSync] Post-login sync completed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[CoreSync] Post-login sync failed");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CoreSync] Failed to start post-login sync");
            }
        }
    }

    private AuthResult ToAuthResult(AuthResponseDto response)
    {
        return new AuthResult(
            response.Token,
            response.UserName ?? ExtractUserNameFromJwt(response.Token),
            new DateTimeOffset(response.ExpiresAt, TimeSpan.Zero));
    }

    private static string? ExtractUserNameFromJwt(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            return jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                ?? jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value
                ?? jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value
                ?? jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the API's AuthResponse JSON shape.
    /// </summary>
    private sealed record AuthResponseDto(
        string Token,
        string RefreshToken,
        DateTime ExpiresAt,
        string? UserName,
        string? UserProfileId);
}
