using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Auth;

public static class AuthEndpoints
{
    private class AuthLog { }
    private class PasswordResetLogger { }

    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", Register);
        group.MapPost("/login", Login);
        group.MapPost("/refresh", Refresh);
        group.MapGet("/confirm-email", ConfirmEmail);
        group.MapPost("/forgot-password", ForgotPassword);
        group.MapPost("/reset-password", ResetPassword);
        group.MapDelete("/account", DeleteAccount).RequireAuthorization();
        group.MapPost("/change-password", ChangePassword).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        IAppEmailSender emailSender,
        IWebHostEnvironment env,
        JwtTokenService tokenService,
        HttpContext httpContext,
        ILogger<AuthLog> logger)
    {
        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.Email
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        }

        // Create a linked UserProfile
        var profile = new UserProfile
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.DisplayName ?? request.Email,
            Email = request.Email,
            NativeLanguage = request.NativeLanguage ?? "English",
            TargetLanguage = request.TargetLanguage ?? "Korean",
            CreatedAt = DateTime.UtcNow
        };

        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        user.UserProfileId = profile.Id;
        await userManager.UpdateAsync(user);

        if (env.IsDevelopment())
        {
            // Auto-confirm email in development so devs aren't blocked
            var confirmToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedConfirmToken = Uri.EscapeDataString(confirmToken);
            var devBaseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var devConfirmUrl = $"{devBaseUrl}/api/auth/confirm-email?userId={user.Id}&token={encodedConfirmToken}";

            logger.LogInformation(
                "--- EMAIL CONFIRMATION LINK (dev auto-confirmed) ---\nFor: {Email}\nConfirm URL: {ConfirmUrl}\n--- END ---",
                request.Email, devConfirmUrl);

            await userManager.ConfirmEmailAsync(user, confirmToken);

            // Issue tokens so the client auto-logs in
            var jwt = tokenService.GenerateToken(user);
            var refreshTokenValue = JwtTokenService.GenerateRefreshToken();
            var expiryMinutes = tokenService.GetExpiryMinutes();

            var refreshToken = new RefreshToken
            {
                UserId = user.Id,
                Token = refreshTokenValue,
                ExpiresAt = DateTime.UtcNow.AddDays(tokenService.GetRefreshTokenLifetimeDays()),
                CreatedAt = DateTime.UtcNow
            };

            db.RefreshTokens.Add(refreshToken);
            await db.SaveChangesAsync();

            return Results.Ok(new AuthResponse(
                Token: jwt,
                RefreshToken: refreshTokenValue,
                ExpiresAt: DateTime.UtcNow.AddMinutes(expiryMinutes),
                UserName: user.DisplayName ?? user.UserName,
                UserProfileId: user.UserProfileId));
        }

        // Production: send confirmation email
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = Uri.EscapeDataString(token);
        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var confirmUrl = $"{baseUrl}/api/auth/confirm-email?userId={user.Id}&token={encodedToken}";

        await emailSender.SendConfirmationLinkAsync(user, request.Email, confirmUrl);

        return Results.Ok(new { message = "Check your email to confirm your account.", userId = user.Id });
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        JwtTokenService tokenService,
        IWebHostEnvironment env,
        ILogger<AuthLog> logger,
        HttpContext httpContext)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Unauthorized();
        }

        if (!await userManager.IsEmailConfirmedAsync(user))
        {
            logger.LogInformation("Login blocked for {Email}: email not confirmed", request.Email);
            return Results.Unauthorized();
        }

        // Link or create a UserProfile if one doesn't exist (accounts from before registration fix, or migrated data)
        if (string.IsNullOrEmpty(user.UserProfileId))
        {
            // First, try to find an existing profile matching this user's email (e.g., migrated data)
            var existing = await db.UserProfiles
                .FirstOrDefaultAsync(p => p.Email == (user.Email ?? request.Email));
            if (existing is not null)
            {
                user.UserProfileId = existing.Id;
                logger.LogInformation("Linked existing UserProfile {ProfileId} to user {Email}",
                    existing.Id, request.Email);
            }
            else
            {
                var profile = new UserProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = user.DisplayName ?? user.Email ?? request.Email,
                    Email = user.Email ?? request.Email,
                    NativeLanguage = "English",
                    TargetLanguage = "Korean",
                    CreatedAt = DateTime.UtcNow
                };
                db.UserProfiles.Add(profile);
                await db.SaveChangesAsync();
                user.UserProfileId = profile.Id;

                logger.LogInformation("Created missing UserProfile {ProfileId} for user {Email}",
                    profile.Id, request.Email);
            }
            await userManager.UpdateAsync(user);
        }

        var jwt = tokenService.GenerateToken(user);
        var refreshTokenValue = JwtTokenService.GenerateRefreshToken();
        var expiryMinutes = tokenService.GetExpiryMinutes();

        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(tokenService.GetRefreshTokenLifetimeDays()),
            CreatedAt = DateTime.UtcNow
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync();

        return Results.Ok(new AuthResponse(
            Token: jwt,
            RefreshToken: refreshTokenValue,
            ExpiresAt: DateTime.UtcNow.AddMinutes(expiryMinutes),
            UserName: user.DisplayName ?? user.UserName,
            UserProfileId: user.UserProfileId));
    }

    private static async Task<IResult> Refresh(
        RefreshRequest request,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        JwtTokenService tokenService,
        ILogger<AuthLog> logger)
    {
        var storedToken = await db.RefreshTokens
            .FirstOrDefaultAsync(rt =>
                rt.Token == request.RefreshToken
                && rt.RevokedAt == null
                && rt.ExpiresAt > DateTime.UtcNow);

        if (storedToken is null)
        {
            // Token not found as active. Check grace window for recently-revoked tokens.
            var revokedToken = await db.RefreshTokens
                .FirstOrDefaultAsync(rt =>
                    rt.Token == request.RefreshToken
                    && rt.RevokedAt != null
                    && rt.ExpiresAt > DateTime.UtcNow);

            if (revokedToken is not null)
            {
                var graceWindowSeconds = tokenService.GetRefreshTokenGraceWindowSeconds();
                var graceWindowExpiry = DateTime.UtcNow.AddSeconds(-graceWindowSeconds);

                // If revoked within grace window and has a successor, return the successor's credentials
                if (revokedToken.RevokedAt > graceWindowExpiry && !string.IsNullOrEmpty(revokedToken.ReplacedByToken))
                {
                    var successorToken = await db.RefreshTokens
                        .FirstOrDefaultAsync(rt =>
                            rt.Token == revokedToken.ReplacedByToken
                            && rt.RevokedAt == null
                            && rt.ExpiresAt > DateTime.UtcNow);

                    if (successorToken is not null)
                    {
                        var user = await userManager.FindByIdAsync(successorToken.UserId);
                        if (user is not null)
                        {
                            logger.LogWarning(
                                "Grace-window replay detected for user {UserId}. " +
                                "Revoked token reused within {GraceWindowSeconds}s. " +
                                "Returning successor credentials (no new rotation).",
                                user.Id, graceWindowSeconds);

                            // Return existing successor's credentials (do NOT rotate again)
                            var jwt = tokenService.GenerateToken(user);
                            var expiryMinutes = tokenService.GetExpiryMinutes();

                            return Results.Ok(new AuthResponse(
                                Token: jwt,
                                RefreshToken: successorToken.Token,
                                ExpiresAt: DateTime.UtcNow.AddMinutes(expiryMinutes),
                                UserName: user.DisplayName ?? user.UserName,
                                UserProfileId: user.UserProfileId));
                        }
                    }
                }
            }

            return Results.Unauthorized();
        }

        var activeUser = await userManager.FindByIdAsync(storedToken.UserId);
        if (activeUser is null)
        {
            return Results.Unauthorized();
        }

        // Issue new tokens
        var newJwt = tokenService.GenerateToken(activeUser);
        var newRefreshTokenValue = JwtTokenService.GenerateRefreshToken();
        var newExpiryMinutes = tokenService.GetExpiryMinutes();

        var newRefreshToken = new RefreshToken
        {
            UserId = activeUser.Id,
            Token = newRefreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(tokenService.GetRefreshTokenLifetimeDays()),
            CreatedAt = DateTime.UtcNow
        };

        db.RefreshTokens.Add(newRefreshToken);

        // Revoke old token and link to successor
        storedToken.RevokedAt = DateTime.UtcNow;
        storedToken.ReplacedByToken = newRefreshTokenValue;

        await db.SaveChangesAsync();

        return Results.Ok(new AuthResponse(
            Token: newJwt,
            RefreshToken: newRefreshTokenValue,
            ExpiresAt: DateTime.UtcNow.AddMinutes(newExpiryMinutes),
            UserName: activeUser.DisplayName ?? activeUser.UserName,
            UserProfileId: activeUser.UserProfileId));
    }

    private static async Task<IResult> ConfirmEmail(
        string userId,
        string token,
        UserManager<ApplicationUser> userManager)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return Results.BadRequest(new { error = "Invalid user." });
        }

        var result = await userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            return Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        }

        return Results.Ok(new { message = "Email confirmed." });
    }

    private static async Task<IResult> ForgotPassword(
        ForgotPasswordRequest request,
        UserManager<ApplicationUser> userManager,
        IAppEmailSender emailSender,
        HttpContext httpContext,
        IWebHostEnvironment env,
        ILogger<PasswordResetLogger> logger)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is not null)
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = Uri.EscapeDataString(token);
            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var resetUrl = $"{baseUrl}/Account/ResetPassword?email={Uri.EscapeDataString(request.Email)}&token={encodedToken}";

            await emailSender.SendPasswordResetLinkAsync(user, request.Email, resetUrl);

            if (env.IsDevelopment())
            {
                logger.LogInformation(
                    "--- PASSWORD RESET LINK ---\nFor: {Email}\nReset URL: {ResetUrl}\n--- Copy and paste this URL into your browser ---",
                    request.Email, resetUrl);
            }
        }

        return Results.Ok(new { message = "If that email is registered, a reset link has been sent." });
    }

    private static async Task<IResult> ResetPassword(
        ResetPasswordRequest request,
        UserManager<ApplicationUser> userManager)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return Results.BadRequest(new { error = "Invalid request." });
        }

        var result = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            return Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        }

        return Results.Ok(new { message = "Password has been reset." });
    }

    private static async Task<IResult> DeleteAccount(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db)
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return Results.NotFound(new { error = "Account not found." });

        // Delete UserProfile if linked
        if (!string.IsNullOrEmpty(user.UserProfileId))
        {
            var profile = await db.UserProfiles.FindAsync(user.UserProfileId);
            if (profile is not null)
            {
                db.UserProfiles.Remove(profile);
                await db.SaveChangesAsync();
            }
        }

        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded)
            return Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        return Results.Ok(new { message = "Account deleted." });
    }

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        HttpContext context,
        UserManager<ApplicationUser> userManager)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return Results.BadRequest(new { error = "Current password and new password are required." });

        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return Results.NotFound(new { error = "Account not found." });

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        return Results.Ok(new { message = "Password changed successfully." });
    }
}
