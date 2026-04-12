using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using SentenceStudio.Abstractions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Auth;

/// <summary>
/// WebApp implementation: resolves the active user's profile ID from
/// the authenticated user, NOT from a shared preferences file.
///
/// Strategy: HttpContext.User for the current request (works during
/// prerender AND API endpoints). Falls back to IPreferencesService
/// for Blazor SignalR calls where HttpContext is null — the login
/// endpoint writes active_profile_id to preferences after sign-in.
///
/// Registered as SINGLETON to avoid Scoped resolution issues with
/// Singleton repositories.
/// </summary>
public class ClaimsActiveUserProvider : IActiveUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPreferencesService? _preferences;

    public ClaimsActiveUserProvider(
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider,
        IPreferencesService? preferences = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _serviceProvider = serviceProvider;
        _preferences = preferences;
    }

    public string? GetActiveProfileId()
    {
        // Strategy 1: HttpContext (works during HTTP requests, prerender, API endpoints)
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var profileId = ResolveFromClaims(user);
            if (!string.IsNullOrEmpty(profileId))
                return profileId;
        }

        // Strategy 2: Preferences fallback (Blazor SignalR — HttpContext is null)
        // The login endpoint writes active_profile_id to preferences after sign-in,
        // so this value is current for the logged-in user.
        return _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;
    }

    private string? ResolveFromClaims(ClaimsPrincipal user)
    {
        // Check for user_profile_id claim (present in cookie auth)
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

    // Server: don't fall back to random first profile. But preferences fallback is OK
    // because login explicitly sets it.
    public bool ShouldFallbackToFirstProfile => false;
}
