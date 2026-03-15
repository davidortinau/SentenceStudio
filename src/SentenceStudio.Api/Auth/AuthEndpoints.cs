using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Auth;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", Register);
        group.MapPost("/login", Login);
        group.MapPost("/refresh", Refresh);
        group.MapGet("/confirm-email", ConfirmEmail);
        group.MapPost("/forgot-password", ForgotPassword);
        group.MapPost("/reset-password", ResetPassword);

        return app;
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        IAppEmailSender emailSender,
        HttpContext httpContext)
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
        user.UserProfileId = profile.Id;
        await userManager.UpdateAsync(user);
        await db.SaveChangesAsync();

        // Generate email confirmation token and send confirmation email
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
        JwtTokenService tokenService)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!await userManager.IsEmailConfirmedAsync(user))
        {
            return Results.Json(new { error = "Email not confirmed." }, statusCode: 401);
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Unauthorized();
        }

        var jwt = tokenService.GenerateToken(user);
        var refreshTokenValue = JwtTokenService.GenerateRefreshToken();
        var expiryMinutes = tokenService.GetExpiryMinutes();

        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync();

        return Results.Ok(new AuthResponse(
            Token: jwt,
            RefreshToken: refreshTokenValue,
            ExpiresAt: DateTime.UtcNow.AddMinutes(expiryMinutes),
            UserName: user.DisplayName ?? user.UserName));
    }

    private static async Task<IResult> Refresh(
        RefreshRequest request,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        JwtTokenService tokenService)
    {
        var storedToken = await db.RefreshTokens
            .FirstOrDefaultAsync(rt =>
                rt.Token == request.RefreshToken
                && rt.RevokedAt == null
                && rt.ExpiresAt > DateTime.UtcNow);

        if (storedToken is null)
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(storedToken.UserId);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // Revoke old token
        storedToken.RevokedAt = DateTime.UtcNow;

        // Issue new tokens
        var jwt = tokenService.GenerateToken(user);
        var newRefreshTokenValue = JwtTokenService.GenerateRefreshToken();
        var expiryMinutes = tokenService.GetExpiryMinutes();

        var newRefreshToken = new RefreshToken
        {
            UserId = user.Id,
            Token = newRefreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };

        db.RefreshTokens.Add(newRefreshToken);
        await db.SaveChangesAsync();

        return Results.Ok(new AuthResponse(
            Token: jwt,
            RefreshToken: newRefreshTokenValue,
            ExpiresAt: DateTime.UtcNow.AddMinutes(expiryMinutes),
            UserName: user.DisplayName ?? user.UserName));
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
        HttpContext httpContext)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is not null)
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = Uri.EscapeDataString(token);
            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var resetUrl = $"{baseUrl}/Account/ResetPassword?email={Uri.EscapeDataString(request.Email)}&token={encodedToken}";

            await emailSender.SendPasswordResetLinkAsync(user, request.Email, resetUrl);
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
}
