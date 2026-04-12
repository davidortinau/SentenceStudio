using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using SentenceStudio.Abstractions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Auth;

/// <summary>
/// WebApp implementation: resolves the active user's profile ID from
/// the authenticated user, NOT from a shared preferences file.
/// Prevents cross-user data leaks on the multi-user server.
///
/// Uses TWO strategies because Blazor Server has different auth contexts:
/// 1. IHttpContextAccessor — works during initial HTTP request + API endpoints
/// 2. AuthenticationStateProvider — works during Blazor SignalR circuit (where HttpContext is null)
/// </summary>
public class ClaimsActiveUserProvider : IActiveUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationStateProvider? _authStateProvider;
    private readonly IServiceProvider _serviceProvider;

    public ClaimsActiveUserProvider(
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider,
        AuthenticationStateProvider? authStateProvider = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _serviceProvider = serviceProvider;
        _authStateProvider = authStateProvider;
    }

    public string? GetActiveProfileId()
    {
        // Strategy 1: HttpContext (works during initial HTTP request + API endpoints)
        var user = _httpContextAccessor.HttpContext?.User;

        // Strategy 2: AuthenticationStateProvider (works during Blazor SignalR circuit)
        if (user?.Identity?.IsAuthenticated != true && _authStateProvider != null)
        {
            try
            {
                var authState = _authStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
                user = authState?.User;
            }
            catch
            {
                // AuthenticationStateProvider may not be ready during startup
            }
        }

        if (user?.Identity?.IsAuthenticated != true)
            return null;

        // Check for user_profile_id claim first (present in cookie/JWT-authenticated scenarios)
        var profileClaim = user.FindFirst("user_profile_id")?.Value;
        if (!string.IsNullOrEmpty(profileClaim))
            return profileClaim;

        // Fall back to looking up ApplicationUser.UserProfileId from the database
        var identityUserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(identityUserId))
            return null;

        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var appUser = userManager.FindByIdAsync(identityUserId).GetAwaiter().GetResult();
        return appUser?.UserProfileId;
    }

    // Server is multi-user; NEVER fall back to another user's profile
    public bool ShouldFallbackToFirstProfile => false;
}
