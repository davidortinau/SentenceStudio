using System;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// Bridges the MAUI IAuthService with Blazor's AuthenticationStateProvider framework.
/// Wraps IdentityAuthService — does NOT modify or replace it.
///
/// Key design: if a refresh token exists in SecureStorage, the user is treated as
/// authenticated optimistically while the token refresh happens in the background.
/// This prevents login-screen flashes on app resume.
/// </summary>
public class MauiAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IAuthService _authService;
    private readonly ILogger<MauiAuthenticationStateProvider> _logger;
    private ClaimsPrincipal _currentUser = new ClaimsPrincipal(new ClaimsIdentity());

    // Track whether we've ever successfully authenticated in this app session
    private string? _lastKnownUserName;

    public MauiAuthenticationStateProvider(
        IAuthService authService,
        ILogger<MauiAuthenticationStateProvider> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // Fast path: already signed in with a valid cached token
        if (_authService.IsSignedIn)
        {
            _currentUser = CreateClaimsPrincipalFromToken(
                await _authService.GetAccessTokenAsync(Array.Empty<string>())
            );
            _lastKnownUserName = _authService.UserName;
            return new AuthenticationState(_currentUser);
        }

        // Check if we have a stored session (refresh token exists)
        var hasSession = false;
        try
        {
            hasSession = await _authService.HasStoredSessionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HasStoredSessionAsync threw while resolving auth state");
        }

        if (!hasSession)
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
            _lastKnownUserName = null;
            return new AuthenticationState(_currentUser);
        }

        // Create a minimal authenticated principal so AuthorizeRouteView doesn't redirect
        var optimisticPrincipal = CreateOptimisticPrincipal();
        _currentUser = optimisticPrincipal;

        // Kick off the actual refresh in the background
        _ = RefreshInBackgroundAsync();

        return new AuthenticationState(_currentUser);
    }

    /// <summary>
    /// Refreshes tokens in the background without blocking the UI.
    /// On success, notifies Blazor of the updated auth state.
    /// On failure, keeps the current optimistic state — only an explicit
    /// server rejection (401) will clear the session and force re-login.
    /// </summary>
    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            _logger.LogInformation("Starting background token refresh");
            var result = await _authService.SignInAsync(); // Parameterless = silent refresh

            if (result is not null)
            {
                _logger.LogInformation("Background token refresh succeeded");
                _lastKnownUserName = _authService.UserName;
                var updatedPrincipal = CreateClaimsPrincipalFromToken(result.AccessToken);
                _currentUser = updatedPrincipal;
                NotifyAuthenticationStateChanged(
                    Task.FromResult(new AuthenticationState(updatedPrincipal))
                );
            }
            else
            {
                // Refresh returned null — check if it was a hard rejection or transient
                var stillHasSession = await _authService.HasStoredSessionAsync();
                if (!stillHasSession)
                {
                    // Refresh token was explicitly rejected (401) — force re-login
                    _logger.LogWarning("Refresh token was rejected by server — forcing re-login");
                    _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
                    _lastKnownUserName = null;
                    NotifyAuthenticationStateChanged(
                        Task.FromResult(new AuthenticationState(_currentUser))
                    );
                }
                else
                {
                    // Transient failure — keep optimistic state, will retry next time
                    _logger.LogWarning("Token refresh failed transiently — keeping optimistic auth state");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background token refresh failed — keeping optimistic auth state");
        }
    }

    public async Task LogInAsync(string email, string password)
    {
        var loginTask = LogInAsyncCore(email, password);
        NotifyAuthenticationStateChanged(loginTask);
        await loginTask;
    }

    private async Task<AuthenticationState> LogInAsyncCore(string email, string password)
    {
        var result = await _authService.SignInAsync(email, password);

        if (result is not null)
        {
            _currentUser = CreateClaimsPrincipalFromToken(result.AccessToken);
            _lastKnownUserName = _authService.UserName;
        }
        else
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LogInSilentlyAsync()
    {
        var loginTask = LogInSilentlyAsyncCore();
        NotifyAuthenticationStateChanged(loginTask);
        await loginTask;
    }

    /// <summary>
    /// Republishes the authentication state from whatever the auth service currently holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For callers that have <em>already</em> signed in through <see cref="IAuthService"/> and only
    /// need Blazor to be told. <see cref="GetAuthenticationStateAsync"/> takes its
    /// <see cref="IAuthService.IsSignedIn"/> fast path, so this costs no secure-storage read and no
    /// network call.
    /// </para>
    /// <para>
    /// Deliberately not <see cref="LogInSilentlyAsync"/>: that one calls the parameterless
    /// <see cref="IAuthService.SignInAsync()"/>, which ignores the token cache, re-reads secure
    /// storage and can issue a refresh request — and on failure it keeps
    /// <see cref="_currentUser"/> as-is, which for a user who just arrived at the login page is the
    /// <em>anonymous</em> principal. Publishing that straight after a successful credential login
    /// bounces the learner back to the login form with no error shown.
    /// </para>
    /// </remarks>
    public void NotifySignedIn() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private async Task<AuthenticationState> LogInSilentlyAsyncCore()
    {
        try
        {
            var result = await _authService.SignInAsync(); // Parameterless = silent refresh
            if (result is not null)
            {
                _currentUser = CreateClaimsPrincipalFromToken(result.AccessToken);
                _lastKnownUserName = _authService.UserName;
            }
            else
            {
                // Check if we still have a session before clearing state
                var stillHasSession = await _authService.HasStoredSessionAsync();
                if (!stillHasSession)
                {
                    _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
                    _lastKnownUserName = null;
                }
                // else: keep current optimistic state
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Silent sign-in failed during LogInSilentlyAsync");
            // Don't clear state on exceptions — could be transient
        }

        return new AuthenticationState(_currentUser);
    }

    /// <summary>
    /// Signs out and publishes an anonymous principal, whatever the credential store does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things have to be true at once here, and they pull in opposite directions. The UI must
    /// end up signed out and able to navigate — a logout that throws before
    /// <see cref="AuthenticationStateProvider.NotifyAuthenticationStateChanged"/> strands the
    /// learner on an authenticated-looking screen. And the caller must not be told the credentials
    /// were removed when they were not.
    /// </para>
    /// <para>
    /// So the anonymous principal is published in a <c>finally</c>, and the truth comes back in the
    /// return value instead of an exception. The return type change from <c>Task</c> to
    /// <c>Task&lt;SignOutOutcome&gt;</c> is source-compatible: <c>await provider.LogOutAsync();</c>
    /// still compiles at every existing call site.
    /// </para>
    /// <para>
    /// Nothing is silently absorbed. <see cref="IAuthService.SignOutAsync"/> still throws, the
    /// failure is logged at Error with key names only, and the auth service keeps its
    /// pending-cleanup latch set so no cold start can restore from whatever survived.
    /// </para>
    /// </remarks>
    public async Task<SignOutOutcome> LogOutAsync()
    {
        var outcome = SignOutOutcome.Clean;

        try
        {
            await _authService.SignOutAsync();
        }
        catch (AuthTokenCleanupException ex)
        {
            outcome = SignOutOutcome.Failed(ex.AffectedKeys);

            _logger.LogError(
                ex,
                "Sign-out could not confirm removal of {Count} stored credential key(s): {Keys}. " +
                "The session is anonymous in this process and silent restore is blocked, but the " +
                "device may still hold a usable credential.",
                ex.AffectedKeys.Count,
                string.Join(", ", ex.AffectedKeys));
        }
        finally
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
            _lastKnownUserName = null;
            NotifyAuthenticationStateChanged(
                Task.FromResult(new AuthenticationState(_currentUser))
            );
        }

        return outcome;
    }

    /// <summary>
    /// Creates a minimal authenticated ClaimsPrincipal for optimistic auth state
    /// when we know a refresh token exists but haven't refreshed yet.
    /// </summary>
    private ClaimsPrincipal CreateOptimisticPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.AuthenticationMethod, "refresh_token_pending")
        };

        if (!string.IsNullOrEmpty(_lastKnownUserName))
        {
            claims.Add(new Claim(ClaimTypes.Name, _lastKnownUserName));
            claims.Add(new Claim(ClaimTypes.Email, _lastKnownUserName));
        }

        var identity = new ClaimsIdentity(claims, "optimistic");
        return new ClaimsPrincipal(identity);
    }

    private ClaimsPrincipal CreateClaimsPrincipalFromToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return new ClaimsPrincipal(new ClaimsIdentity());

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);

            var identity = new ClaimsIdentity(jwt.Claims, "jwt");
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JWT token");
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
}
