using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Auth;

public static class AccountEndpoints
{
    // Logger category for password reset logging
    private class PasswordResetLogger { }

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/account-action").AllowAnonymous();

        group.MapPost("/Logout", async (
            SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Redirect("/");
        })
        .DisableAntiforgery();

        // GET sign-out for Blazor NavigateTo(forceLoad:true) — clears server cookie
        group.MapGet("/SignOut", async (
            SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Redirect("/auth/login");
        });

        // One-time auto-sign-in via token (used by Blazor Server interactive pages
        // that can't set cookies directly over WebSocket)
        group.MapGet("/AutoSignIn", async (
            [FromQuery] string userId,
            [FromQuery] string token,
            [FromQuery] string? returnUrl,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IPreferencesService preferences,
            HttpContext httpContext) =>
        {
            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Results.Redirect("/auth/login?error=InvalidLink");
            }

            var valid = await userManager.VerifyUserTokenAsync(
                user, TokenOptions.DefaultProvider, "AutoSignIn", token);
            if (!valid)
            {
                return Results.Redirect("/auth/login?error=InvalidLink");
            }

            // Link or create profile if missing (accounts from before registration fix, or migrated data)
            if (string.IsNullOrEmpty(user.UserProfileId))
            {
                var db = httpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AutoSignIn");
                
                // Try to find an existing profile: by email first, then by display name, then first available
                var userEmail = user.Email ?? "";
                var userName = user.DisplayName ?? user.UserName ?? "";
                
                var existing = await db.UserProfiles
                    .FirstOrDefaultAsync(p => p.Email == userEmail && userEmail != "");
                if (existing is null && !string.IsNullOrEmpty(userName))
                {
                    existing = await db.UserProfiles
                        .FirstOrDefaultAsync(p => p.Name == userName);
                }
                // Last resort: if only one profile exists, it's almost certainly this user's
                if (existing is null)
                {
                    var allProfiles = await db.UserProfiles.Take(2).ToListAsync();
                    if (allProfiles.Count == 1)
                        existing = allProfiles[0];
                }

                if (existing is not null)
                {
                    logger.LogInformation("Linked existing UserProfile {ProfileId} (Name={Name}, Email={PEmail}) to user {Email}",
                        existing.Id, existing.Name, existing.Email, userEmail);
                    user.UserProfileId = existing.Id;
                }
                else
                {
                    var profile = new UserProfile
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = user.DisplayName ?? user.Email ?? "User",
                        Email = user.Email ?? "",
                        NativeLanguage = "English",
                        TargetLanguage = "Korean",
                        CreatedAt = DateTime.UtcNow
                    };
                    db.UserProfiles.Add(profile);
                    await db.SaveChangesAsync();
                    user.UserProfileId = profile.Id;
                    logger.LogInformation("Created new UserProfile {ProfileId} for user {Email} (no match found among {Count} profiles)",
                        profile.Id, userEmail, await db.UserProfiles.CountAsync());
                }
                await userManager.UpdateAsync(user);
            }

            await signInManager.SignInAsync(user, isPersistent: true);

            // Set the active profile so Profile page and other features find it
            var isOnboarded = false;
            if (!string.IsNullOrEmpty(user.UserProfileId))
            {
                preferences.Set("active_profile_id", user.UserProfileId);

                // Auto-mark returning users as onboarded so they skip the onboarding flow
                var db2 = httpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                var profile = await db2.UserProfiles.FindAsync(user.UserProfileId);
                if (profile is not null
                    && !string.IsNullOrEmpty(profile.TargetLanguage)
                    && !string.IsNullOrEmpty(profile.Name)
                    && !string.IsNullOrEmpty(profile.NativeLanguage))
                {
                    preferences.Set("is_onboarded", true);
                    isOnboarded = true;
                }
            }

            // Override returnUrl if user is onboarded but was sent to onboarding
            // (happens when preferences file was wiped by container deploy)
            var destination = returnUrl ?? "/";
            if (isOnboarded && destination.Contains("onboarding", StringComparison.OrdinalIgnoreCase))
            {
                destination = "/";
            }

            return Results.LocalRedirect(destination);
        });

        group.MapGet("/ConfirmEmail", async (
            [FromQuery] string userId,
            [FromQuery] string code,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Results.Redirect("/auth/login?error=InvalidLink");
            }

            var result = await userManager.ConfirmEmailAsync(user, code);
            return result.Succeeded
                ? Results.Redirect("/auth/login?message=EmailConfirmed")
                : Results.Redirect("/auth/login?error=ConfirmFailed");
        });

        group.MapPost("/ForgotPassword", async (
            [FromForm] string email,
            UserManager<ApplicationUser> userManager,
            IAppEmailSender emailSender,
            HttpContext httpContext,
            IWebHostEnvironment env,
            ILogger<PasswordResetLogger> logger) =>
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is not null)
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                var encodedToken = Uri.EscapeDataString(token);
                var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
                var resetUrl = $"{baseUrl}/Account/ResetPassword?email={Uri.EscapeDataString(email)}&token={encodedToken}";

                await emailSender.SendPasswordResetLinkAsync(user, email, resetUrl);

                if (env.IsDevelopment())
                {
                    logger.LogInformation(
                        "--- PASSWORD RESET LINK ---\nFor: {Email}\nReset URL: {ResetUrl}\n--- Copy and paste this URL into your browser ---",
                        email, resetUrl);
                }
            }

            // Always redirect to avoid email enumeration
            return Results.Redirect("/auth/forgot-password?message=ResetSent");
        })
        .DisableAntiforgery();

        group.MapPost("/ResetPassword", async (
            [FromForm] string email,
            [FromForm] string token,
            [FromForm] string newPassword,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                return Results.Redirect("/auth/login?error=InvalidLink");
            }

            var result = await userManager.ResetPasswordAsync(user, token, newPassword);
            return result.Succeeded
                ? Results.Redirect("/auth/login?message=PasswordReset")
                : Results.Redirect($"/Account/ResetPassword?error=ResetFailed&email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}");
        })
        .DisableAntiforgery();

        group.MapGet("/DeleteAccount", async (
            HttpContext context,
            [FromQuery] string? profileId,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ApplicationDbContext db,
            IPreferencesService preferences) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is not null)
            {
                // Delete linked UserProfile
                if (!string.IsNullOrEmpty(user.UserProfileId))
                {
                    var profile = await db.UserProfiles.FindAsync(user.UserProfileId);
                    if (profile is not null)
                    {
                        db.UserProfiles.Remove(profile);
                        await db.SaveChangesAsync();
                    }
                }

                await signInManager.SignOutAsync();
                await userManager.DeleteAsync(user);
            }
            else if (!string.IsNullOrEmpty(profileId))
            {
                // No Identity user — delete profile by explicit ID
                var profile = await db.UserProfiles.FindAsync(profileId);
                if (profile is not null)
                {
                    db.UserProfiles.Remove(profile);
                    await db.SaveChangesAsync();
                }
            }

            preferences.Remove("active_profile_id");
            preferences.Remove("app_is_authenticated");
            preferences.Set("is_onboarded", false);

            return Results.Redirect("/auth");
        });
    }
}
