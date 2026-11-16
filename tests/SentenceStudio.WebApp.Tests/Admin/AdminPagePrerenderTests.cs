using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentenceStudio.WebApp.Admin;
using SentenceStudio.WebApp.Tests.Infrastructure;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.WebApp.Tests.Admin;

/// <summary>
/// The admin pages through the WebApp's real pipeline, as a browser's first request sees them.
/// </summary>
/// <remarks>
/// An admin page decides nothing while it prerenders: the HTML a browser (or a cache, or
/// view-source) receives is the same empty shell for an admin and for a learner, and the API is
/// asked only once the live circuit is running. What the circuit then shows is covered by
/// <see cref="AdminClientAndStateTests"/> and by the running check on the local stack.
/// </remarks>
public sealed class AdminPagePrerenderTests : IAsyncLifetime
{
    private const string Password = "IntegrationTest1!";

    // Production, because the admin portal ships there, unlike the Development-only operator page.
    private readonly OperatorWebAppFactory _factory =
        new(nameof(AdminPagePrerenderTests), Environments.Production);

    public Task InitializeAsync() => _factory.InitializeAsync();

    public Task DisposeAsync() => _factory.DisposeAsync();

    [WebAppPostgresTheory]
    [InlineData("/admin")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/sync-health")]
    [InlineData("/admin/sync-health/some-profile-id")]
    [InlineData("/admin/usage")]
    public async Task An_anonymous_visitor_is_sent_to_sign_in(string path)
    {
        var client = _factory.CreateBrowserClient();

        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/auth/login");
        body.Should().NotContain("admin-home").And.NotContain("admin-audit").And.NotContain("Signed in as");
        _factory.Operator.Requests.Should().NotContain(r => r.Path.StartsWith("/api/admin", StringComparison.Ordinal));
    }

    [WebAppPostgresTheory]
    [InlineData("/admin")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/sync-health")]
    [InlineData("/admin/sync-health/some-profile-id")]
    [InlineData("/admin/usage")]
    public async Task The_first_response_holds_no_admin_content_and_asks_the_API_nothing(string path)
    {
        var learner = await _factory.SeedLearnerAsync($"admin-prerender-{Guid.NewGuid():N}@sentencestudio.test", Password);
        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an admin route must not sign anyone out");
        body.Should().Contain("app-safe-area-top", "the page must have rendered inside the app's layout");
        body.Should().Contain("id=\"page-loading\"", "everyone gets the same waiting mark until the circuit asks");
        body.Should().NotContain("admin-gate", "the first HTML must not say what the mark is waiting for");
        body.Should().NotContain("admin-home")
            .And.NotContain("admin-audit-table")
            .And.NotContain("admin-sync")
            .And.NotContain("admin-usage")
            .And.NotContain("<title>AI usage")
            .And.NotContain("<title>Sync")
            .And.NotContain("Signed in as")
            .And.NotContain("Password confirmation")
            .And.NotContain("<title>Admin")
            .And.NotContain("<title>Audit log")
            .And.NotContain("Page not found", "the page says nothing either way until the API has answered")
            .And.NotContain("could not be loaded");
        body.Should().NotContain("href=\"/admin\"", "the sidebar link is added only once the API has said yes");
        _factory.Operator.Requests.Should().NotContain(
            r => r.Path.StartsWith("/api/admin", StringComparison.Ordinal),
            "prerendering must not spend an admin request, audited or not");
    }

    [WebAppPostgresFact]
    public void The_admin_client_and_the_sidebar_provider_are_registered_in_Production()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetService<AdminApiClient>().Should().NotBeNull();
        scope.ServiceProvider.GetService<AdminSessionState>().Should().NotBeNull();
        scope.ServiceProvider.GetServices<IHostNavItemProvider>()
            .Should().ContainSingle().Which.Should().BeOfType<AdminNavItemProvider>();
    }
}
