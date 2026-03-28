using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Auth;

/// <summary>
/// Server-side IAuthService that uses ASP.NET Identity directly.
/// Used by the shared Blazor UI pages (/auth/register, /auth/login)
/// when running inside the WebApp (server-side Blazor).
///
/// Because Blazor Server runs over WebSocket, we cannot set cookies here.
/// Instead, we create users/validate passwords and return a one-time token
/// that the page uses to redirect to an HTTP endpoint for cookie sign-in.
/// </summary>
public class ServerAuthService : IAuthService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerAuthService> _logger;

    public ServerAuthService(
        IServiceScopeFactory scopeFactory,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<ServerAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsSignedIn =>
        _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;

    public string? UserName =>
        _httpContextAccessor.HttpContext?.User?.Identity?.Name;

    public async Task<AuthResult?> RegisterAsync(string email, string password, string displayName)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Registration failed: {Errors}", errors);
            throw new InvalidOperationException(errors);
        }

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

        // Generate a one-time auto-sign-in token (cookie can't be set from Blazor Server)
        var token = await userManager.GenerateUserTokenAsync(
            user, TokenOptions.DefaultProvider, "AutoSignIn");

        _logger.LogInformation("Registered {Email}, generated auto-sign-in token", email);

        // Encode userId|token in AccessToken so the page can redirect to the sign-in endpoint
        return new AuthResult(
            AccessToken: $"{user.Id}|{token}",
            UserName: user.DisplayName ?? user.UserName,
            ExpiresOn: DateTimeOffset.UtcNow.AddMinutes(5));
    }

    public async Task<AuthResult?> SignInAsync(string email, string password)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByEmailAsync(email);
        if (user is null || !await userManager.CheckPasswordAsync(user, password))
        {
            _logger.LogWarning("Sign-in failed for {Email}", email);
            return null;
        }

        // Generate a one-time auto-sign-in token
        var token = await userManager.GenerateUserTokenAsync(
            user, TokenOptions.DefaultProvider, "AutoSignIn");

        _logger.LogInformation("Validated {Email}, generated auto-sign-in token", email);

        return new AuthResult(
            AccessToken: $"{user.Id}|{token}",
            UserName: user.DisplayName ?? user.UserName,
            ExpiresOn: DateTimeOffset.UtcNow.AddMinutes(5));
    }

    public Task<AuthResult?> SignInAsync() => Task.FromResult<AuthResult?>(null);

    public async Task SignOutAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        await signInManager.SignOutAsync();
        _logger.LogInformation("Signed out");
    }

    public async Task<bool> DeleteAccountAsync()
    {
        var userName = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
        if (string.IsNullOrEmpty(userName)) return false;

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();

        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return false;

        await signInManager.SignOutAsync();
        var result = await userManager.DeleteAsync(user);

        if (result.Succeeded)
        {
            _logger.LogInformation("Deleted Identity account for {User}", userName);
        }
        else
        {
            _logger.LogWarning("Failed to delete Identity account for {User}: {Errors}",
                userName, string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        return result.Succeeded;
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var userName = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
        if (string.IsNullOrEmpty(userName)) return false;

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return false;

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);

        if (result.Succeeded)
        {
            _logger.LogInformation("Password changed for {User}", userName);
        }
        else
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Password change failed for {User}: {Errors}", userName, errors);
            throw new InvalidOperationException(errors);
        }

        return result.Succeeded;
    }

    public Task<string?> GetAccessTokenAsync(string[] scopes)
    {
        var signingKey = _configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            _logger.LogDebug("No JWT signing key configured; skipping token generation");
            return Task.FromResult<string?>(null);
        }

        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult<string?>(null);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown"),
            new(ClaimTypes.Name, user.Identity.Name ?? ""),
            new(ClaimTypes.Email, user.FindFirst(ClaimTypes.Email)?.Value ?? ""),
            new("user_id", user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown"),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiryMinutes = int.TryParse(_configuration["Jwt:ExpiryMinutes"], out var mins) ? mins : 120;

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "SentenceStudio",
            audience: _configuration["Jwt:Audience"] ?? "SentenceStudio.Api",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: creds);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return Task.FromResult<string?>(jwt);
    }
}
