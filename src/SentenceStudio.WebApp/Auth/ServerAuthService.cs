using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Diagnostics;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebApp.Platform;

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
    private readonly CircuitUserStateAccessor _circuitUserState;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerAuthService> _logger;

    public ServerAuthService(
        IServiceScopeFactory scopeFactory,
        IHttpContextAccessor httpContextAccessor,
        CircuitUserStateAccessor circuitUserState,
        IConfiguration configuration,
        ILogger<ServerAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpContextAccessor = httpContextAccessor;
        _circuitUserState = circuitUserState;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsSignedIn =>
        (_httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false)
        || !string.IsNullOrEmpty(_circuitUserState.Current?.NameIdentifier);

    public string? UserName =>
        _httpContextAccessor.HttpContext?.User?.Identity?.Name;

    public async Task<AuthResult?> RegisterAsync(string email, string password, string displayName)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            // The log gets codes; the caller gets the descriptions. Identity builds a description
            // by interpolating the offending value ("User name 'x@y.test' is already taken."), so
            // a description in the log is the address in the log. The thrown message is unchanged
            // because RegisterPage surfaces it to the person who typed it.
            _logger.LogWarning("Registration failed for {Email}: {ErrorCodes}",
                AuthLogRedaction.MaskEmail(email),
                AuthLogRedaction.DescribeIdentityErrors(result.Errors));

            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));
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

        if (env.IsDevelopment())
        {
            var confirmToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
            await userManager.ConfirmEmailAsync(user, confirmToken);
            _logger.LogInformation("Auto-confirmed {Email} for development WebApp registration", AuthLogRedaction.MaskEmail(email));
        }

        // Generate a one-time auto-sign-in token (cookie can't be set from Blazor Server)
        var token = await userManager.GenerateUserTokenAsync(
            user, TokenOptions.DefaultProvider, "AutoSignIn");

        _logger.LogInformation("Registered {Email} as user {UserId}, generated auto-sign-in token",
            AuthLogRedaction.MaskEmail(email), user.Id);

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
            _logger.LogWarning("Sign-in failed for {Email}", AuthLogRedaction.MaskEmail(email));
            return null;
        }

        // Generate a one-time auto-sign-in token
        var token = await userManager.GenerateUserTokenAsync(
            user, TokenOptions.DefaultProvider, "AutoSignIn");

        _logger.LogInformation("Validated {Email} as user {UserId}, generated auto-sign-in token",
            AuthLogRedaction.MaskEmail(email), user.Id);

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
            // The Identity id is stable, opaque, and already the join key an operator would use to
            // follow this account through the rest of the log. The user name is the address.
            _logger.LogInformation("Deleted Identity account for user {UserId}", user.Id);
        }
        else
        {
            _logger.LogWarning("Failed to delete Identity account for user {UserId}: {ErrorCodes}",
                user.Id, AuthLogRedaction.DescribeIdentityErrors(result.Errors));
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
            _logger.LogInformation("Password changed for user {UserId}", user.Id);
        }
        else
        {
            // Same split as registration: codes to the log, descriptions to the person. A password
            // policy description names the rule, but Identity's duplicate/user-scoped describers
            // interpolate the account, so descriptions are never safe to log as a class.
            _logger.LogWarning("Password change failed for user {UserId}: {ErrorCodes}",
                user.Id, AuthLogRedaction.DescribeIdentityErrors(result.Errors));

            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        return result.Succeeded;
    }

    public async Task<string?> GetAccessTokenAsync(string[] scopes)
    {
        var signingKey = _configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            _logger.LogDebug("No JWT signing key configured; skipping token generation");
            return null;
        }

        var caller = ResolveCaller();
        if (caller is null)
        {
            return null;
        }

        var (identityUserId, callerName, callerEmail) = caller.Value;

        // Look up the ApplicationUser once. It supplies user_profile_id — which the coach,
        // feedback, channel and import endpoints all require — and, on the circuit path, the
        // display name and address that the HTTP principal would otherwise have carried.
        string? userProfileId = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(identityUserId);
            if (appUser is not null)
            {
                userProfileId = appUser.UserProfileId;
                callerName ??= appUser.UserName;
                callerEmail ??= appUser.Email;
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identityUserId),
            new(ClaimTypes.Name, callerName ?? ""),
            new(ClaimTypes.Email, callerEmail ?? ""),
            new("user_id", identityUserId),
        };

        if (!string.IsNullOrEmpty(userProfileId))
        {
            claims.Add(new(AuthClaimTypes.UserProfileId, userProfileId));
        }

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
        return jwt;
    }

    /// <summary>
    /// Resolves who this call is being made for, from the HTTP request or from the circuit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two tiers, in the same order and for the same reason as
    /// <c>WebPreferencesService.ResolveActiveUserProfileId</c>:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <see cref="IHttpContextAccessor"/> — plain HTTP requests and the static SSR pass.
    /// </description></item>
    /// <item><description>
    /// <see cref="CircuitUserStateAccessor"/> — the Blazor InteractiveServer circuit pass, where
    /// <c>HttpContext</c> is null.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Tier 2 is what makes a server-side API call work after the circuit takes over.</b>
    /// Without it this method returned null for every call made from an interactive component,
    /// <c>AuthenticatedHttpMessageHandler</c> sent those requests with no Authorization header, and
    /// the API saw an anonymous caller. On the Development operator surface that is not a visible
    /// 401: the API's development auth fallback admits the anonymous request as a principal with no
    /// <c>user_profile_id</c>, which then fails the operator cohort check and is answered with the
    /// same 404 the surface uses for "not available". The page therefore rendered its rows during
    /// prerender and blanked to the unavailable state the moment the circuit re-ran initialisation.
    /// </para>
    /// <para>
    /// Neither tier widens anything. Both name the already-signed-in learner, and the token carries
    /// that learner's own claims — the cohort check on the far side is unchanged.
    /// </para>
    /// </remarks>
    private (string IdentityUserId, string? Name, string? Email)? ResolveCaller()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            return (
                user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown",
                user.Identity.Name,
                user.FindFirst(ClaimTypes.Email)?.Value);
        }

        var circuit = _circuitUserState.Current;
        if (!string.IsNullOrEmpty(circuit?.NameIdentifier))
        {
            // The circuit snapshot carries identifiers only; the name and address are filled in
            // from the ApplicationUser by the caller.
            return (circuit.NameIdentifier, null, null);
        }

        return null;
    }

    public Task<bool> HasStoredSessionAsync() =>
        Task.FromResult(IsSignedIn);
}

/// <summary>
/// Test seam for <see cref="ServerAuthService"/>'s log redaction.
/// </summary>
/// <remarks>
/// The rule itself now lives in <see cref="AuthLogRedaction"/> so the API, the WebApp, the shared
/// Blazor UI and the MAUI client all mask the same way — a private copy per project is how one of
/// them ends up a version behind. This type stays only so the existing webapp-facing tests keep a
/// name to bind to.
/// </remarks>
public static class ServerAuthLogRedaction
{
    /// <inheritdoc cref="AuthLogRedaction.MaskEmail"/>
    public static string MaskEmail(string? email) => AuthLogRedaction.MaskEmail(email);
}
