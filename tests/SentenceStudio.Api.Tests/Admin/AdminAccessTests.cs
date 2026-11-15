using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// Who gets into <c>/api/admin</c>: an account that holds the role and is on the list, and nobody
/// else. Everyone else gets the 404 the API gives a path that does not exist.
/// </summary>
public sealed class AdminAccessTests : IDisposable
{
    private readonly AdminApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>Every admin route, plus a wrong verb, the bare prefix and a path nobody mapped.</summary>
    public static TheoryData<string, string> AdminRequests => new()
    {
        { "GET", "/api/admin/access" },
        { "GET", "/api/admin/session" },
        { "POST", "/api/admin/step-up" },
        { "GET", "/api/admin/audit" },
        { "POST", "/api/admin/session" },
        { "DELETE", "/api/admin/audit" },
        { "GET", "/api/admin" },
        { "GET", "/api/admin/" },
        { "GET", "/api/admin/learners/abc/words" },
        { "PUT", "/api/admin/anything/at/all" }
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { password = AdminApiFactory.Password });
        }

        return request;
    }

    private static async Task AssertBareNotFoundAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty(
            "a refusal must not carry a body that an unmapped path would not");
        response.Headers.WwwAuthenticate.Should().BeEmpty(
            "a challenge header says the route exists and wants credentials");
    }

    [Theory]
    [MemberData(nameof(AdminRequests))]
    public async Task An_anonymous_caller_gets_404_on_every_admin_route(string method, string path)
    {
        using var client = _factory.CreateClient();

        await AssertBareNotFoundAsync(await client.SendAsync(Request(method, path)));
    }

    [Theory]
    [MemberData(nameof(AdminRequests))]
    public async Task A_learner_gets_404_on_every_admin_route(string method, string path)
    {
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(learner);

        await AssertBareNotFoundAsync(await client.SendAsync(Request(method, path)));
        admin.Should().NotBeNull("an admin existing must not open the routes to anyone else");
    }

    [Theory]
    [MemberData(nameof(AdminRequests))]
    public async Task The_role_without_the_list_gets_404_on_every_admin_route(string method, string path)
    {
        var user = await _factory.SeedAccountAsync("role-only@admin-tests.local");
        await _factory.GrantRoleAsync(user);
        _factory.SetAdminEmails("somebody-else@admin-tests.local");
        using var client = _factory.CreateClientFor(user);

        await AssertBareNotFoundAsync(await client.SendAsync(Request(method, path)));
    }

    [Theory]
    [MemberData(nameof(AdminRequests))]
    public async Task The_list_without_the_role_gets_404_on_every_admin_route(string method, string path)
    {
        var user = await _factory.SeedAccountAsync("listed-only@admin-tests.local");
        _factory.SetAdminEmails(user.Email!);
        using var client = _factory.CreateClientFor(user);

        await AssertBareNotFoundAsync(await client.SendAsync(Request(method, path)));
    }

    [Fact]
    public async Task A_different_role_is_not_the_admin_role()
    {
        var user = await _factory.SeedAccountAsync("other-role@admin-tests.local");
        await _factory.GrantRoleAsync(user, "Administrator");
        _factory.SetAdminEmails(user.Email!);
        using var client = _factory.CreateClientFor(user);

        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/access"));
    }

    [Fact]
    public async Task An_empty_list_means_nobody_is_an_admin()
    {
        var user = await _factory.SeedAccountAsync("no-list@admin-tests.local");
        await _factory.GrantRoleAsync(user);
        _factory.SetAdminEmails();
        using var client = _factory.CreateClientFor(user);

        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/access"));

        // A blank entry is not a wildcard, and must not match an account with no email either.
        _factory.SetAdminEmails("", "   ");
        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/access"));
    }

    [Fact]
    public async Task An_admin_gets_in()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var probe = await client.GetAsync("/api/admin/access");
        probe.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var session = await client.GetFromJsonAsync<AdminSessionResponse>("/api/admin/session");
        session.Should().NotBeNull();
        session!.UserId.Should().Be(admin.Id);
        session.Email.Should().Be(admin.Email);
        session.StepUpExpiresAtUtc.Should().BeNull("no password has been confirmed yet");
    }

    [Fact]
    public async Task The_list_matches_the_address_whatever_its_case_or_padding()
    {
        var user = await _factory.SeedAccountAsync("Mixed.Case@Admin-Tests.local");
        await _factory.GrantRoleAsync(user);
        _factory.SetAdminEmails("  mixed.case@admin-tests.LOCAL ");
        using var client = _factory.CreateClientFor(user);

        (await client.GetAsync("/api/admin/access")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Removing_the_address_from_the_list_takes_effect_on_the_next_request()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        (await client.GetAsync("/api/admin/access")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        _factory.SetAdminEmails();

        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/access"));
        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/session"));
    }

    [Fact]
    public async Task Removing_the_role_takes_effect_on_the_next_request()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        (await client.GetAsync("/api/admin/access")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await _factory.RevokeRoleAsync(admin);

        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/access"));
        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/session"));
    }

    [Fact]
    public async Task An_unconfirmed_email_is_not_an_admin_even_with_the_role_and_the_list()
    {
        var user = await _factory.SeedAccountAsync("unconfirmed@admin-tests.local", emailConfirmed: false);
        await _factory.GrantRoleAsync(user);
        _factory.SetAdminEmails(user.Email!);
        using var client = _factory.CreateClientFor(user);

        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/access"));
    }

    [Fact]
    public async Task The_email_claim_in_a_token_admits_nobody()
    {
        // A learner's own account, but a token that claims the admin's address. The address is
        // read from the database for the account the token names, never from the token.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userId: learner.Id, email: admin.Email));

        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/session"));
    }

    [Fact]
    public async Task A_token_for_an_account_that_does_not_exist_admits_nobody()
    {
        _factory.SetAdminEmails(TestJwtGenerator.DefaultEmail);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken());

        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/session"));
    }

    [Fact]
    public async Task An_expired_or_forged_token_gets_the_same_404()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userId: admin.Id, lifetime: TimeSpan.FromMinutes(-5)));
        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/session"));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        await AssertBareNotFoundAsync(await client.GetAsync("/api/admin/session"));
    }

    [Fact]
    public async Task A_refusal_is_indistinguishable_from_a_path_that_was_never_mapped()
    {
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        using var client = _factory.CreateClientFor(learner);

        var refused = await client.GetAsync("/api/admin/session");
        var unmapped = await client.GetAsync("/api/this-route-was-never-mapped");

        refused.StatusCode.Should().Be(unmapped.StatusCode);
        (await refused.Content.ReadAsByteArrayAsync()).Should().Equal(await unmapped.Content.ReadAsByteArrayAsync());
        HeaderNames(refused).Should().BeEquivalentTo(HeaderNames(unmapped));

        static IEnumerable<string> HeaderNames(HttpResponseMessage response) =>
            response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task An_admin_still_gets_404_for_an_admin_path_that_does_not_exist()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync("/api/admin/no-such-thing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_responses_are_never_cacheable()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var response = await client.GetAsync("/api/admin/session");

        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task The_development_sign_in_fallback_is_never_an_admin()
    {
        // Even if a local database held an account with the fallback's fixed id, the role and a
        // listed address, the fallback principal names no real sign-in.
        using var scope = _factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<AdminAccessEvaluator>();
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");

        var fallback = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, admin.Id)], DevAuthHandler.SchemeName));
        var bearer = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, admin.Id)], "Bearer"));

        (await evaluator.EvaluateAsync(fallback, CancellationToken.None)).Actor.Should().BeNull();
        (await evaluator.EvaluateAsync(bearer, CancellationToken.None)).Actor.Should().NotBeNull(
            "the same account through a real sign-in is the control for this test");
    }
}
