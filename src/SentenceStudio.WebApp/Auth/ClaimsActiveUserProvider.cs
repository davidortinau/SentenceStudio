using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using SentenceStudio.Abstractions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Auth;

/// <summary>
/// WebApp implementation: resolves the active user's profile ID from
/// the authenticated HTTP context, NOT from a shared preferences file.
/// Prevents cross-user data leaks on the multi-user server.
/// </summary>
public class ClaimsActiveUserProvider : IActiveUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceProvider _serviceProvider;

    public ClaimsActiveUserProvider(
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider)
    {
        _httpContextAccessor = httpContextAccessor;
        _serviceProvider = serviceProvider;
    }

    public string? GetActiveProfileId()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        // Check for user_profile_id claim first (present in JWT-authenticated scenarios)
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
