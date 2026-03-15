using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Auth;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for JWT Bearer authentication mode.
/// Uses JwtBearerApiFactory to simulate Auth:UseEntraId=true.
/// All tests run without real Entra ID credentials.
/// </summary>
public class JwtBearerAuthTests : IClassFixture<JwtBearerApiFactory>
{
    private readonly HttpClient _client;

    public JwtBearerAuthTests(JwtBearerApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task API_RejectsUnauthenticatedGetRequest()
    {
        var response = await _client.GetAsync("/api/v1/auth/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "GET /auth/bootstrap should reject requests without a token");
    }

    [Fact]
    public async Task API_RejectsUnauthenticatedPostRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/plans/generate", new
        {
            Minutes = 30
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "POST /plans/generate should reject requests without a token");
    }

    [Fact]
    public async Task API_AcceptsValidJwtToken()
    {
        var token = TestJwtGenerator.GenerateToken();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/auth/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a valid JWT token should be accepted");
    }

    [Fact]
    public async Task API_RejectsExpiredToken()
    {
        var token = TestJwtGenerator.GenerateExpiredToken();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/auth/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an expired token should be rejected");
    }

    [Fact]
    public async Task API_RejectsGarbageToken()
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not.a.real.token");

        var response = await _client.GetAsync("/api/v1/auth/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a malformed token should be rejected");
    }

    [Fact]
    public async Task API_ExtractsTenantContext()
    {
        var tenantId = "custom-tenant-42";
        var userId = "custom-user-99";
        var displayName = "Jayne Cobb";
        var email = "jayne@serenity.local";

        var token = TestJwtGenerator.GenerateToken(
            tenantId: tenantId,
            userId: userId,
            displayName: displayName,
            email: email);

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/auth/bootstrap");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bootstrap = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        bootstrap.Should().NotBeNull();
        bootstrap!.TenantId.Should().Be(tenantId);
        bootstrap.UserId.Should().Be(userId);
        bootstrap.DisplayName.Should().Be(displayName);
        bootstrap.Email.Should().Be(email);
    }

    [Fact]
    public async Task API_PlansEndpointWorksWithValidToken()
    {
        var token = TestJwtGenerator.GenerateToken();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.PostAsJsonAsync("/api/v1/plans/generate", new
        {
            Minutes = 30
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
