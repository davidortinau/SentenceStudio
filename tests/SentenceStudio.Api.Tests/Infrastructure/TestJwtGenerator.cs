using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Generates JWT tokens signed with a test key for integration tests.
/// Mirrors Entra ID claim structure so tests validate real-world token
/// handling without Azure credentials.
/// </summary>
public static class TestJwtGenerator
{
    public const string TestSigningKeyValue = "SuperSecretTestKey-AtLeast32Characters!!";
    public static readonly SymmetricSecurityKey SecurityKey =
        new(Encoding.UTF8.GetBytes(TestSigningKeyValue));

    public const string TestIssuer = "SentenceStudio";
    public const string TestAudience = "SentenceStudio.Api";

    public const string DefaultTenantId = "49c0cd14-bc68-4c6d-b87b-9d65a56fa6df";
    public const string DefaultUserId = "test-user-oid-12345";
    public const string DefaultDisplayName = "Test User";
    public const string DefaultEmail = "testuser@sentencestudio.local";

    public static string GenerateToken(
        string? tenantId = null,
        string? userId = null,
        string? displayName = null,
        string? email = null,
        string? scopes = null,
        TimeSpan? lifetime = null)
    {
        var claims = new List<Claim>
        {
            new("tid", tenantId ?? DefaultTenantId),
            new("oid", userId ?? DefaultUserId),
            new(ClaimTypes.NameIdentifier, userId ?? DefaultUserId),
            new("tenant_id", tenantId ?? DefaultTenantId),
            new(ClaimTypes.Name, displayName ?? DefaultDisplayName),
            new(ClaimTypes.Email, email ?? DefaultEmail),
        };

        if (!string.IsNullOrWhiteSpace(scopes))
        {
            claims.Add(new Claim("scp", scopes));
        }

        var credentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.HmacSha256);
        var expiry = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1));

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            expires: expiry,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string GenerateExpiredToken()
    {
        return GenerateToken(lifetime: TimeSpan.FromSeconds(-60));
    }

    public static TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SecurityKey,
        ValidIssuer = TestIssuer,
        ValidAudience = TestAudience,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(5)
    };
}
