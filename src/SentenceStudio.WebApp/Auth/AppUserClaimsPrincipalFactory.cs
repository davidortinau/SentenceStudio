using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Auth;

/// <summary>
/// Adds the <c>user_profile_id</c> claim to the authenticated principal so the
/// webapp can resolve the active user's <see cref="ApplicationUser.UserProfileId"/>
/// from <see cref="System.Security.Claims.ClaimsPrincipal"/> alone — without a
/// process-wide singleton preferences store.
///
/// This is the source of truth that survives container restarts (cookie carries
/// the claim) and is naturally per-user (each request gets its own principal).
///
/// Replaces the cross-tenant-leaky <c>WebPreferencesService.Set("active_profile_id", ...)</c>
/// pattern that was overwritten by every concurrent user.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public AppUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrEmpty(user.UserProfileId))
        {
            identity.AddClaim(new Claim(AuthClaimTypes.UserProfileId, user.UserProfileId));
        }
        return identity;
    }
}
