using System.Net;
using System.Net.Http.Json;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Auth;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for DevAuthHandler mode.
/// Validates that all requests are auto-authenticated with dev claims.
/// This is the local development authentication path.
/// </summary>
public class DevAuthHandlerTests : IClassFixture<DevAuthApiFactory>
{
    private const string CoreSyncStoreIdPath = "/api/sync-agent/store-id";
    private readonly HttpClient _client;

    public DevAuthHandlerTests(DevAuthApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DevAuthHandler_WorksWhenEntraIdDisabled()
    {
        // No token needed — DevAuthHandler auto-authenticates
        var response = await _client.GetAsync("/api/v1/auth/bootstrap");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "DevAuthHandler should auto-authenticate all requests");
    }

    [Fact]
    public async Task DevAuthHandler_ProvidesDevClaims()
    {
        var response = await _client.GetAsync("/api/v1/auth/bootstrap");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bootstrap = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        bootstrap.Should().NotBeNull();
        bootstrap!.TenantId.Should().Be("dev-tenant");
        bootstrap.UserId.Should().Be("dev-user");
        bootstrap.DisplayName.Should().Be("Dev User");
        bootstrap.Email.Should().Be("dev@sentencestudio.local");
    }

    [Fact]
    public async Task DevAuthHandler_AllProtectedEndpointsAccessible()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/plans/generate", new
        {
            Minutes = 30
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "DevAuthHandler should allow access to all protected endpoints");
    }

    [Fact]
    public async Task DevAuthHandler_CoreSyncEndpointsAccessible()
    {
        var response = await _client.GetAsync(CoreSyncStoreIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "DevAuthHandler should keep CoreSync usable during local development");
    }

    [Fact]
    public async Task DevAuthHandler_TenantContextPopulated()
    {
        var response = await _client.GetAsync("/api/v1/auth/bootstrap");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bootstrap = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        bootstrap.Should().NotBeNull();

        // TenantContext should be populated from the DevAuthHandler claims
        bootstrap!.TenantId.Should().NotBeNullOrEmpty("TenantContextMiddleware should extract tenant_id");
        bootstrap.UserId.Should().NotBeNullOrEmpty("TenantContextMiddleware should extract user id");
        bootstrap.DisplayName.Should().NotBeNullOrEmpty("TenantContextMiddleware should extract display name");
        bootstrap.Email.Should().NotBeNullOrEmpty("TenantContextMiddleware should extract email");
    }
}
