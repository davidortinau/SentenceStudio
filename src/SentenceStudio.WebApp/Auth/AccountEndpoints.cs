using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Auth;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/account-action");

        group.MapPost("/Login", async (
            [FromForm] string email,
            [FromForm] string password,
            [FromForm] bool? rememberMe,
            [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signInManager) =>
        {
            returnUrl ??= "/";

            var result = await signInManager.PasswordSignInAsync(
                email, password, rememberMe ?? false, lockoutOnFailure: false);

            if (result.Succeeded)
            {
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
            SignInManager<ApplicationUser> signInManager) =>
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
                await signInManager.SignInAsync(user, isPersistent: false);
                return Results.Redirect("/");
            }

            var errors = string.Join(",", result.Errors.Select(e => e.Code));
            return Results.Redirect($"/Account/Register?errors={Uri.EscapeDataString(errors)}");
        })
        .DisableAntiforgery();

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
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is not null)
            {
                // In production, send the reset token via email.
                // For now, just redirect with a confirmation message.
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                // TODO: Send email with reset link containing token
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
    }
}
