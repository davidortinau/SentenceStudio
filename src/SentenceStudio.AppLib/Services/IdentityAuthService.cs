using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Diagnostics;

namespace SentenceStudio.Services;

/// <summary>
/// Auth service that authenticates against the API's ASP.NET Identity endpoints
/// using email/password credentials and JWT tokens.
/// </summary>
public sealed class IdentityAuthService : IAuthService
{
    private const string JwtKey = AuthTokenStore.JwtKey;
    private const string RefreshKey = AuthTokenStore.RefreshKey;
    private const string ExpiresKey = AuthTokenStore.ExpiresKey;

    private readonly HttpClient _http;
    private readonly ISecureStorageService _secureStorage;
    private readonly IPreferencesService _preferences;
    private readonly ILogger<IdentityAuthService> _logger;
    private readonly ISyncService? _syncService;
    private readonly DataRecoveryService? _dataRecovery;
    private readonly UserProfileRepository? _userProfileRepo;

    /// <summary>
    /// Present only on heads whose keychain service name is shared with other applications
    /// (currently the macOS AppKit head). Sign-out uses it to close pre-namespacing adoption for
    /// good — see the call in <see cref="SignOutAsync"/>.
    /// </summary>
    private readonly SentenceStudio.Abstractions.Keychain.LegacyCredentialAdoption? _legacyAdoption;

    /// <summary>
    /// Owns the persisted credential triple. All three keys are written and cleared through it so
    /// a failure can never leave one account's refresh token beside another's access token.
    /// </summary>
    private readonly AuthTokenStore _tokenStore;

    private string? _cachedToken;
    private DateTimeOffset _cachedExpires;
    private string? _cachedUserName;

    // Single-flight locking to prevent concurrent refresh races
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Task<AuthResult?>? _inflightRefresh;
    private int _consecutiveAuthFailures;

    /// <summary>
    /// One retry of a failed credential cleanup per process. Bounded so a keystore that will never
    /// let go does not make every auth query re-attempt removal.
    /// </summary>
    private int _pendingCleanupRetried;

    public IdentityAuthService(
        IHttpClientFactory httpClientFactory,
        ISecureStorageService secureStorage,
        IPreferencesService preferences,
        ILogger<IdentityAuthService> logger,
        ISyncService? syncService = null,
        DataRecoveryService? dataRecovery = null,
        UserProfileRepository? userProfileRepo = null,
        SentenceStudio.Abstractions.Keychain.LegacyCredentialAdoption? legacyAdoption = null)
    {
        _http = httpClientFactory.CreateClient("AuthClient");
        _secureStorage = secureStorage;
        _preferences = preferences;
        _logger = logger;
        _syncService = syncService;
        _dataRecovery = dataRecovery;
        _userProfileRepo = userProfileRepo;
        _legacyAdoption = legacyAdoption;
        _tokenStore = new AuthTokenStore(secureStorage, preferences, logger);
    }

    public bool IsSignedIn => _cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow;

    public string? UserName => _cachedUserName;

    /// <inheritdoc/>
    public async Task<bool> HasStoredSessionAsync()
    {
        if (IsSignedIn)
            return true;

        if (await IsSilentRestoreBlockedAsync().ConfigureAwait(false))
            return false;

        try
        {
            // NoInteraction: this runs automatically on startup and on every auth-state query.
            // On the macOS AppKit head an interactive read can block forever behind a modal
            // keychain prompt, which wedges the app on "Checking authentication...".
            var result = await _secureStorage
                .TryGetAsync(RefreshKey, SecureStorageAccess.NoInteraction)
                .ConfigureAwait(false);

            if (result.RequiresInteraction)
            {
                // The refresh token is still there — we just may not read it without asking the
                // user. Report "no session" so the UI shows sign-in; nothing is cleared.
                _logger.LogInformation(
                    "Stored session exists but the platform keystore requires user authorisation; " +
                    "treating as signed out without prompting.");
                return false;
            }

            return result.IsFound;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe for a stored session");
            return false;
        }
    }

    /// <summary>
    /// Silent sign-in: first tries to restore a valid JWT from SecureStorage (no network),
    /// then falls back to refresh token if the JWT is expired or expiring soon.
    /// Returns null if no stored tokens or refresh fails (UI should show login).
    /// </summary>
    public async Task<AuthResult?> SignInAsync()
    {
        if (await IsSilentRestoreBlockedAsync().ConfigureAwait(false))
            return null;

        try
        {
            // Automatic (silent) restore path — never allowed to block on a keychain prompt.
            var storedJwtResult = await _secureStorage
                .TryGetAsync(JwtKey, SecureStorageAccess.NoInteraction).ConfigureAwait(false);

            if (storedJwtResult.RequiresInteraction)
            {
                _logger.LogInformation(
                    "Silent sign-in skipped: the platform keystore requires user authorisation. " +
                    "Stored tokens were left untouched.");
                return null;
            }

            var storedJwt = storedJwtResult.IsFound ? storedJwtResult.Value : null;
            var storedExpiresResult = await _secureStorage
                .TryGetAsync(ExpiresKey, SecureStorageAccess.NoInteraction).ConfigureAwait(false);
            var storedExpiresStr = storedExpiresResult.IsFound ? storedExpiresResult.Value : null;

            if (!string.IsNullOrEmpty(storedJwt) && !string.IsNullOrEmpty(storedExpiresStr)
                && DateTimeOffset.TryParse(storedExpiresStr, out var storedExpires)
                && storedExpires > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                // Stored JWT is still valid with comfortable margin — restore without network
                _cachedToken = storedJwt;
                _cachedExpires = storedExpires;
                _cachedUserName = ExtractUserNameFromJwt(storedJwt);

                // Apply saved locale if active_profile_id is set
                await ApplyLocaleFromActiveProfileAsync();

                _logger.LogInformation("Restored session from stored JWT, expires {Expires}", storedExpires);
                return new AuthResult(storedJwt, _cachedUserName, storedExpires);
            }

            // JWT missing or expiring soon — try refresh token with single-flight protection
            var refreshResult = await _secureStorage
                .TryGetAsync(RefreshKey, SecureStorageAccess.NoInteraction).ConfigureAwait(false);
            if (refreshResult.RequiresInteraction)
            {
                _logger.LogInformation(
                    "Silent refresh skipped: the platform keystore requires user authorisation.");
                return null;
            }

            var refreshToken = refreshResult.IsFound ? refreshResult.Value : null;
            if (string.IsNullOrEmpty(refreshToken))
                return null;

            // Single-flight: if a refresh is already in-flight, await it instead of starting a new one
            bool lockAcquired = false;
            try
            {
                await _refreshLock.WaitAsync();
                lockAcquired = true;

                if (_inflightRefresh is not null)
                {
                    _logger.LogInformation("Refresh already in-flight, awaiting existing task");
                    return await _inflightRefresh;
                }

                _inflightRefresh = RefreshTokenAsync(refreshToken);
                return await _inflightRefresh;
            }
            finally
            {
                _inflightRefresh = null;
                if (lockAcquired)
                    _refreshLock.Release();
            }
        }
        catch (AuthTokenPersistenceException ex)
        {
            // The refresh succeeded on the wire but the new triple could not be stored, and the
            // store has already rolled back. Reporting "no session" is the truthful answer; keeping
            // the in-memory token would hand out an access token no cold start could ever renew.
            _logger.LogError(
                ex,
                "Silent sign-in obtained tokens but could not persist them; stored credentials were rolled back.");
            ClearInMemoryAuth();
            return null;
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
                _http.BaseAddress, AuthLogRedaction.MaskEmail(email));

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
            AuthResponseDto? authResponse;
            try
            {
                authResponse = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
                // Registration succeeded but the body is not an auth response (email confirmation
                // required). Deliberately narrow: a catch-all here used to swallow the token-storage
                // failure below and report "check your email" for an account that was in fact
                // signed in but had no persisted credentials.
                _logger.LogInformation(
                    "Registration succeeded without an auth payload — email confirmation is likely required.");
                return null;
            }

            if (authResponse?.Token is not null)
            {
                await StoreTokens(authResponse);
                return ToAuthResult(authResponse);
            }

            return null;
        }
        catch (AuthTokenPersistenceException)
        {
            // Registration succeeded server-side but the credentials could not be stored (already
            // rolled back). Surfacing it beats returning null, which this method uses to mean
            // "check your email" — a message that would send the learner looking for a mail that
            // is never coming.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed");
            return null;
        }
    }

    /// <summary>
    /// Signs out: drops the in-memory session first, then removes every stored credential this app
    /// owns and proves each one is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order is deliberate. The in-memory session is cleared before anything is attempted, so this
    /// process stops being authenticated even if every subsequent step fails; and
    /// <see cref="AuthTokenStore"/> latches its pending-cleanup flag before its first removal, so a
    /// crash part-way through still blocks silent restore on the next launch.
    /// </para>
    /// <para>
    /// This used to call <see cref="ISecureStorageService.Remove"/> three times and log
    /// "Signed out, tokens and profile cleared" unconditionally, discarding all three return values.
    /// On the macOS AppKit head a removal can genuinely fail — an item owned by a previous ad-hoc
    /// signature refuses <c>SecItemDelete</c> with <c>errSecInvalidOwnerEdit</c> — so a learner
    /// could be told they had signed out while a usable refresh token stayed on the machine, ready
    /// for whoever launched the app next.
    /// </para>
    /// </remarks>
    /// <exception cref="AuthTokenCleanupException">
    /// A stored credential could not be proven removed. Thrown only after the in-memory session has
    /// been cleared and every bounded attempt has been made.
    /// </exception>
    public async Task SignOutAsync()
    {
        // 1. In-memory first. Whatever storage does next, this process is no longer signed in.
        ClearInMemoryAuth();

        // 2. The profile pointer. Left behind, it aims every repository at the previous account.
        try
        {
            _preferences.Remove("active_profile_id");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear active_profile_id during sign-out");
        }

        // 3. Close pre-namespacing adoption permanently, BEFORE the credential removal that can
        //    throw. Otherwise a sign-out that fails to verify removal would leave adoption open,
        //    and the next launch could re-adopt a still-present bare triple and sign the learner
        //    back in — the precise thing they just asked not to happen. Recording a decision is
        //    the durable half of sign-out; deleting the bare items is not an option, because this
        //    app cannot prove it owns account names in a machine-global service.
        try
        {
            _legacyAdoption?.Retire();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retire legacy keychain adoption during sign-out");
        }

        // 4. Persistent credentials. Throws when it cannot demonstrate they are gone.
        await _tokenStore.ClearAsync().ConfigureAwait(false);

        _logger.LogInformation("Signed out; stored credentials removed and verified, profile cleared");
    }

    public async Task<bool> DeleteAccountAsync()
    {
        try
        {
            var response = await _http.DeleteAsync("/api/auth/account");
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    await SignOutAsync();
                }
                catch (AuthTokenCleanupException ex)
                {
                    // The remote account really is gone, so reporting failure here would be its own
                    // lie. What matters is that the residue is not silently usable: the store has
                    // latched its pending-cleanup flag, so no cold start will restore from it.
                    _logger.LogError(
                        ex,
                        "Account deleted server-side, but local credential removal could not be confirmed " +
                        "for {Count} key(s). Silent restore is blocked.",
                        ex.AffectedKeys.Count);
                }
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

        if (await IsSilentRestoreBlockedAsync().ConfigureAwait(false))
            return null;

        // Try refresh with single-flight protection
        try
        {
            // NoInteraction: GetAccessTokenAsync is called from background/API paths where a
            // modal keychain prompt would deadlock the caller.
            var storedRefresh = await _secureStorage
                .TryGetAsync(RefreshKey, SecureStorageAccess.NoInteraction).ConfigureAwait(false);
            if (storedRefresh.RequiresInteraction)
            {
                _logger.LogInformation(
                    "Access-token refresh skipped: the platform keystore requires user authorisation.");
                return null;
            }

            var refreshToken = storedRefresh.IsFound ? storedRefresh.Value : null;
            if (string.IsNullOrEmpty(refreshToken))
                return null;

            // Single-flight: if a refresh is already in-flight, await it instead of starting a new one
            bool lockAcquired = false;
            try
            {
                await _refreshLock.WaitAsync();
                lockAcquired = true;

                // Re-check cache after acquiring lock — another caller may have just refreshed
                if (_cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow.AddSeconds(60))
                    return _cachedToken;

                if (_inflightRefresh is not null)
                {
                    _logger.LogInformation("Refresh already in-flight, awaiting existing task");
                    var result = await _inflightRefresh;
                    return result?.AccessToken;
                }

                _inflightRefresh = RefreshTokenAsync(refreshToken);
                var refreshResult = await _inflightRefresh;
                return refreshResult?.AccessToken;
            }
            finally
            {
                _inflightRefresh = null;
                if (lockAcquired)
                    _refreshLock.Release();
            }
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
            _consecutiveAuthFailures = 0; // Reset counter on transient failures
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode == 401 || statusCode == 403)
            {
                // Server explicitly rejected the refresh token — increment failure counter
                _consecutiveAuthFailures++;
                _logger.LogWarning("Token refresh rejected with {Status} (consecutive failures: {Count})", 
                    response.StatusCode, _consecutiveAuthFailures);

                // Only clear the refresh token after 2 consecutive auth failures
                // This defends against transient server errors and race conditions
                if (_consecutiveAuthFailures >= 2)
                {
                    _logger.LogWarning("Clearing stored credentials after {Count} consecutive auth failures", _consecutiveAuthFailures);

                    // All three keys, not just the refresh token. Removing one of a triple leaves
                    // an access token and an expiry belonging to a session the server has already
                    // repudiated, which the next launch would treat as a restorable session.
                    var outcome = await _tokenStore.TryClearAsync().ConfigureAwait(false);
                    if (!outcome.CredentialsCleared)
                    {
                        _logger.LogError(
                            "Could not confirm removal of rejected credentials: {Keys}. Silent restore is blocked.",
                            string.Join(", ", outcome.UnclearedKeys));
                    }

                    ClearInMemoryAuth();
                    _consecutiveAuthFailures = 0; // Reset after clearing
                }
            }
            else
            {
                // Server error (5xx) or other non-auth failure — keep the refresh token
                _logger.LogWarning("Token refresh returned {Status} — keeping refresh token for retry", response.StatusCode);
                _consecutiveAuthFailures = 0; // Reset counter on non-auth failures
            }
            return null;
        }

        // Success — reset the consecutive failure counter
        _consecutiveAuthFailures = 0;

        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        if (authResponse is null)
            return null;

        await StoreTokens(authResponse);
        return ToAuthResult(authResponse);
    }

    private async Task StoreTokens(AuthResponseDto response)
    {
        // Persist first, cache second. The cache used to be populated before the writes, so a
        // failed write left the process holding an access token for an account whose refresh token
        // was never stored — a session that works until the app is closed and then vanishes.
        //
        // PersistAsync is all-or-nothing: on any failure it rolls back all three owned keys and
        // throws, so there is no path from here to a triple that mixes two accounts.
        try
        {
            await _tokenStore.PersistAsync(
                response.Token,
                response.RefreshToken,
                new DateTimeOffset(response.ExpiresAt, TimeSpan.Zero));
        }
        catch (AuthTokenPersistenceException)
        {
            // Roll the profile pointer back too. A pointer at an account whose credentials were
            // just discarded aims every repository at data this process can no longer authenticate
            // for, which is the same mixed-account hazard one layer up.
            ClearInMemoryAuth();
            try
            {
                _preferences.Remove("active_profile_id");
            }
            catch (Exception prefEx)
            {
                _logger.LogWarning(prefEx, "Could not clear active_profile_id after a failed credential write");
            }

            throw;
        }

        _cachedToken = response.Token;
        _cachedExpires = new DateTimeOffset(response.ExpiresAt, TimeSpan.Zero);
        _cachedUserName = response.UserName ?? ExtractUserNameFromJwt(response.Token);

        // A successful triple write puts storage back into a known state for a known account, so
        // any earlier cleanup failure no longer has to block restore.
        _pendingCleanupRetried = 0;

        // Set the active profile so all repositories filter by the correct user
        if (!string.IsNullOrEmpty(response.UserProfileId))
        {
            _preferences.Set("active_profile_id", response.UserProfileId);
            _logger.LogInformation("Active profile set to {ProfileId}", response.UserProfileId);

            // Re-tag any orphaned local data (e.g. after server wipe + re-registration)
            // before sync pushes records to the server.
            // Gated by enable_automatic_data_recovery (default false) — set to true only when
            // a known server wipe has occurred. Safeguards inside the service provide a second
            // line of defence even when the flag is on.
            if (_dataRecovery != null && _preferences.Get("enable_automatic_data_recovery", false))
            {
                try
                {
                    var emailForRecovery = ExtractEmailFromJwt(response.Token);
                    await _dataRecovery.RecoverOrphanedDataAsync(response.UserProfileId!, emailForRecovery);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orphan data recovery failed — sync will proceed without recovery");
                }
            }

            // Backfill Name/Email on the local UserProfile from JWT claims
            // so the mobile app shows them even though Identity lives server-side.
            await BackfillProfileFromJwtAsync(response.Token);

            // Apply the user's saved locale now that active_profile_id is set.
            // This ensures fresh login flow (and JWT restore flow) immediately applies
            // the correct culture without waiting for app relaunch.
            await ApplyLocaleFromActiveProfileAsync();
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
                // Mark sync in-progress synchronously BEFORE handing off to the background
                // task so the post-login navigation (LoginPage → MainLayout) sees the flag
                // on its very first render and can show the overlay instead of routing
                // to /onboarding while server data is still on the wire. (issue #187)
                _syncService.BeginInitialSync();

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

    /// <summary>Drops every trace of the session this process is holding in memory.</summary>
    private void ClearInMemoryAuth()
    {
        _cachedToken = null;
        _cachedExpires = DateTimeOffset.MinValue;
        _cachedUserName = null;
    }

    /// <summary>
    /// Guards every path that would restore a session <i>without</i> the learner presenting
    /// credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sign-out (or a crash during one) that could not prove the stored credentials were removed
    /// leaves a refresh token on the device that still works. Without this guard the very next cold
    /// start would read it, refresh, and silently sign back in as the account the learner just
    /// signed out of — on a shared or handed-on machine, as somebody else.
    /// </para>
    /// <para>
    /// One more bounded cleanup attempt is made per process before giving up, because the condition
    /// that blocked removal (a locked keychain, a keystore not yet ready at launch) is often gone by
    /// the time anything asks about auth state. If that attempt succeeds the latch clears and the
    /// session restores normally.
    /// </para>
    /// <para>
    /// Explicit credential sign-in and registration deliberately do <b>not</b> consult this: a
    /// successful triple write puts storage back into a known state for a known account, and that
    /// is exactly how a learner recovers from a device stuck in this state.
    /// </para>
    /// </remarks>
    private async Task<bool> IsSilentRestoreBlockedAsync()
    {
        if (!_tokenStore.IsCleanupPending)
            return false;

        // Serialised against token persistence. The retry below CLEARS all three credential keys;
        // PersistAsync WRITES all three. Interleaved, the clear can land between the persist's
        // writes and delete a token the learner just signed in with — or clear the latch that the
        // persist was about to satisfy legitimately. Both are silent. The refresh lock is the
        // existing single-flight gate around credential mutation, so reuse it rather than adding a
        // second lock with its own ordering rules.
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-check under the lock: a persist that completed while we were waiting has already
            // put storage into a known state for a known account, which is exactly what clears the
            // latch. Retrying a cleanup on top of that would delete a valid new session.
            if (!_tokenStore.IsCleanupPending)
                return false;

            return await IsSilentRestoreBlockedCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<bool> IsSilentRestoreBlockedCoreAsync()
    {
        if (Interlocked.Exchange(ref _pendingCleanupRetried, 1) == 0)
        {
            _logger.LogWarning(
                "A previous sign-out left credentials that could not be confirmed removed. " +
                "Retrying removal once before refusing to restore a session.");

            var outcome = await _tokenStore.TryClearAsync().ConfigureAwait(false);
            if (outcome.CredentialsCleared)
            {
                _logger.LogInformation("Retry succeeded: stored credentials are now verified absent.");

                // The device is clean, but the credentials that would have been restored are gone
                // for good — there is nothing left to restore, so still report "no session".
                ClearInMemoryAuth();
                return true;
            }

            _logger.LogError(
                "Retry failed; credential key(s) {Keys} may still hold data. Refusing to restore a session.",
                string.Join(", ", outcome.UnclearedKeys));
        }

        ClearInMemoryAuth();
        return true;
    }

    /// <summary>
    /// Extracts Name and Email from the JWT and updates the local UserProfile
    /// if those fields are empty. This ensures the mobile app has the same
    /// profile data the webapp reads from Identity claims server-side.
    /// </summary>
    private async Task BackfillProfileFromJwtAsync(string token)
    {
        if (_userProfileRepo is null)
            return;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);

            var email = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                     ?? jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;

            var name = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value
                    ?? jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value
                    ?? jwt.Claims.FirstOrDefault(c => c.Type == "unique_name")?.Value;

            if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(name))
                return;

            var profile = await _userProfileRepo.GetAsync();
            if (profile is null)
                return;

            bool changed = false;

            if (string.IsNullOrEmpty(profile.Name) && !string.IsNullOrEmpty(name))
            {
                profile.Name = name;
                changed = true;
            }

            if (string.IsNullOrEmpty(profile.Email) && !string.IsNullOrEmpty(email))
            {
                profile.Email = email;
                changed = true;
            }

            if (changed)
            {
                await _userProfileRepo.SaveAsync(profile);
                _logger.LogInformation("Backfilled UserProfile Name/Email from JWT claims");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill UserProfile from JWT — non-fatal");
        }
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

    private static string? ExtractEmailFromJwt(string? token)
    {
        if (token is null) return null;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            return jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                ?? jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the user's saved DisplayLanguage to the process-wide culture after
    /// active_profile_id is set. Called from StoreTokens to ensure fresh login flow
    /// and JWT restore flow both apply the correct locale immediately.
    /// </summary>
    private async Task ApplyLocaleFromActiveProfileAsync()
    {
        if (_userProfileRepo is null)
        {
            _logger.LogDebug("ApplyLocaleFromActiveProfileAsync: UserProfileRepository not available, skipping locale application.");
            return;
        }

        try
        {
            var profile = await _userProfileRepo.GetAsync();
            LocalizationInitializer.ApplyLocaleFromProfile(profile, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApplyLocaleFromActiveProfileAsync: failed to apply locale — non-fatal");
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
