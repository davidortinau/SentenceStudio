using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
    private readonly ILogger<ServerAuthService> _logger;

    public ServerAuthService(
        IServiceScopeFactory scopeFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ServerAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpContextAccessor = httpContextAccessor;
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

    public Task<string?> GetAccessTokenAsync(string[] scopes) =>
        Task.FromResult<string?>("cookie-auth");
}
