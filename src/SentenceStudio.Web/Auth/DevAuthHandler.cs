using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SentenceStudio.Web.Auth;

/// <summary>
/// Development-only authentication handler that creates a synthetic identity
/// for all requests. Mirrors the API project's DevAuthHandler so that
/// CoreSync sync requests work without Entra ID tokens during local dev.
/// </summary>
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Dev";

    public DevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "dev-user"),
            new Claim("tenant_id", "dev-tenant"),
            new Claim("user_id", "dev-user"),
            new Claim(ClaimTypes.Name, "Dev User"),
            new Claim(ClaimTypes.Email, "dev@sentencestudio.local")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
