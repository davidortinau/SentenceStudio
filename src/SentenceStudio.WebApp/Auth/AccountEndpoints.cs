using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
        var group = app.MapGroup("/account-action");

        group.MapPost("/Login", async (
            [FromForm] string email,
            [FromForm] string password,
            [FromForm] bool? rememberMe,
            [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IPreferencesService preferences,
            HttpContext httpContext) =>
        {
            returnUrl ??= "/";

            var result = await signInManager.PasswordSignInAsync(
                email, password, rememberMe ?? false, lockoutOnFailure: false);

            if (result.Succeeded)
            {
                var user = await userManager.FindByEmailAsync(email);
                if (user is not null)
                {
                    // Auto-create profile if missing (accounts from before registration fix)
                    if (string.IsNullOrEmpty(user.UserProfileId))
                    {
                        var db = httpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                        var profile = new UserProfile
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = user.DisplayName ?? user.Email ?? email,
                            Email = user.Email ?? email,
                            NativeLanguage = "English",
                            TargetLanguage = "Korean",
                            CreatedAt = DateTime.UtcNow
                        };
                        db.UserProfiles.Add(profile);
                        await db.SaveChangesAsync();
                        user.UserProfileId = profile.Id;
                        await userManager.UpdateAsync(user);
                    }

                    if (user.UserProfileId is not null)
                    {
                        preferences.Set("active_profile_id", user.UserProfileId);
                    }
                }
                return Results.LocalRedirect(returnUrl);
            }

            var errorMsg = result.IsLockedOut
                ? "AccountLocked"
                : result.IsNotAllowed
                    ? "NotAllowed"
                    : "InvalidCredentials";

            return Results.Redirect($"/Account/Login?error={errorMsg}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        })
        .DisableAntiforgery();

        group.MapPost("/Logout", async (
            SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Redirect("/");
        })
        .DisableAntiforgery();

        group.MapPost("/Register", async (
            [FromForm] string email,
            [FromForm] string password,
            [FromForm] string? displayName,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ApplicationDbContext db) =>
        {
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = displayName
            };

            var result = await userManager.CreateAsync(user, password);
            if (result.Succeeded)
            {
                // Create linked UserProfile
                var profile = new UserProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = displayName ?? email,
                    Email = email,
                    NativeLanguage = "English",
                    TargetLanguage = "Korean",
                    CreatedAt = DateTime.UtcNow
                };

                db.UserProfiles.Add(profile);
                await db.SaveChangesAsync();

                user.UserProfileId = profile.Id;
                await userManager.UpdateAsync(user);

                await signInManager.SignInAsync(user, isPersistent: false);
                return Results.Redirect("/");
            }

            var errors = string.Join(",", result.Errors.Select(e => e.Code));
            return Results.Redirect($"/Account/Register?errors={Uri.EscapeDataString(errors)}");
        })
        .DisableAntiforgery();

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
                return Results.Redirect("/Account/Login?error=InvalidLink");
            }

            var valid = await userManager.VerifyUserTokenAsync(
                user, TokenOptions.DefaultProvider, "AutoSignIn", token);
            if (!valid)
            {
                return Results.Redirect("/Account/Login?error=InvalidLink");
            }

            // Auto-create profile if missing (accounts from before registration fix)
            if (string.IsNullOrEmpty(user.UserProfileId))
            {
                var db = httpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
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
                await userManager.UpdateAsync(user);
            }

            await signInManager.SignInAsync(user, isPersistent: true);

            // Set the active profile so Profile page and other features find it
            if (!string.IsNullOrEmpty(user.UserProfileId))
            {
                preferences.Set("active_profile_id", user.UserProfileId);
            }

            return Results.LocalRedirect(returnUrl ?? "/");
        });

        group.MapGet("/ConfirmEmail", async (
            [FromQuery] string userId,
            [FromQuery] string code,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Results.Redirect("/Account/Login?error=InvalidLink");
            }

            var result = await userManager.ConfirmEmailAsync(user, code);
            return result.Succeeded
                ? Results.Redirect("/Account/Login?message=EmailConfirmed")
                : Results.Redirect("/Account/Login?error=ConfirmFailed");
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
            return Results.Redirect("/Account/ForgotPassword?message=ResetSent");
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
                return Results.Redirect("/Account/Login?error=InvalidLink");
            }

            var result = await userManager.ResetPasswordAsync(user, token, newPassword);
            return result.Succeeded
                ? Results.Redirect("/Account/Login?message=PasswordReset")
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
