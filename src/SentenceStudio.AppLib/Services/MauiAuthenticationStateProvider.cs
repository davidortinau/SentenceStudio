using System;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Bridges the MAUI IAuthService with Blazor's AuthenticationStateProvider framework.
/// Wraps IdentityAuthService — does NOT modify or replace it.
/// </summary>
public class MauiAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IAuthService _authService;
    private readonly ILogger<MauiAuthenticationStateProvider> _logger;
    private ClaimsPrincipal _currentUser = new ClaimsPrincipal(new ClaimsIdentity());

    public MauiAuthenticationStateProvider(
        IAuthService authService,
        ILogger<MauiAuthenticationStateProvider> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // On app startup, attempt silent sign-in from SecureStorage with a short timeout
        // to avoid blocking the UI while the API is unreachable.
        if (!_authService.IsSignedIn)
        {
            _logger.LogInformation("Not signed in, attempting silent refresh (5s timeout)");
            try
            {
                var signInTask = _authService.SignInAsync(); // Parameterless = silent refresh
                if (await Task.WhenAny(signInTask, Task.Delay(5_000)) != signInTask)
                {
                    _logger.LogWarning("Silent sign-in timed out after 10s, proceeding as unauthenticated");
                }
                else
                {
                    await signInTask; // propagate any exceptions
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Silent sign-in failed");
            }
        }

        if (_authService.IsSignedIn)
        {
            _currentUser = CreateClaimsPrincipalFromToken(
                await _authService.GetAccessTokenAsync(Array.Empty<string>())
            );
        }
        else
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
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

    private async Task<AuthenticationState> LogInSilentlyAsyncCore()
    {
        try
        {
            var result = await _authService.SignInAsync(); // Parameterless = silent refresh
            if (result is not null)
            {
                _currentUser = CreateClaimsPrincipalFromToken(result.AccessToken);
            }
            else
            {
                _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Silent sign-in failed during LogInSilentlyAsync");
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LogOutAsync()
    {
        await _authService.SignOutAsync();
        _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(
            Task.FromResult(new AuthenticationState(_currentUser))
        );
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
