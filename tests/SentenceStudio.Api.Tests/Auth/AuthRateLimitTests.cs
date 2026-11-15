using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Auth;

/// <summary>
/// The auth endpoints' per-address rate limits and the login account lockout (#89).
/// </summary>
public class AuthRateLimitTests : IClassFixture<AuthRateLimitTests.StrictFactory>
{
    /// <summary>Two logins per minute and two registrations per hour per address.</summary>
    public sealed class StrictFactory : JwtBearerApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:RateLimit:LoginPerMinute", "2");
            builder.UseSetting("Auth:RateLimit:RegisterPerHour", "2");
        }
    }

    private readonly StrictFactory _factory;

    public AuthRateLimitTests(StrictFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_over_the_limit_from_one_address_is_refused_with_retry_after()
    {
        var client = ClientFrom("203.0.113.10");

        (await LoginAsync(client, "nobody-a@test.local")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(client, "nobody-b@test.local")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var refused = await LoginAsync(client, "nobody-c@test.local");

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.Should().NotBeNull("the client should know when to try again");
    }

    [Fact]
    public async Task Another_address_keeps_its_own_allowance()
    {
        var busy = ClientFrom("203.0.113.20");
        for (var i = 0; i < 3; i++)
            await LoginAsync(busy, $"busy-{i}@test.local");

        var other = await LoginAsync(ClientFrom("203.0.113.21"), "other@test.local");

        other.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a different address is not limited");
    }

    [Fact]
    public async Task Client_written_forwarded_entries_do_not_create_new_allowances()
    {
        // The ingress appends the address it saw; earlier entries are the client's own and
        // rotating them must not reset the limit.
        for (var i = 0; i < 2; i++)
            await LoginAsync(ClientFrom($"10.0.0.{i}, 203.0.113.30"), $"spoof-{i}@test.local");

        var refused = await LoginAsync(ClientFrom("10.0.0.99, 203.0.113.30"), "spoof-x@test.local");

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Register_over_the_limit_is_refused()
    {
        var client = ClientFrom("203.0.113.40");
        for (var i = 0; i < 2; i++)
        {
            var ok = await RegisterAsync(client, $"reg-limit-{i}-{Guid.NewGuid():N}@test.local");
            ok.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        var refused = await RegisterAsync(client, $"reg-limit-x-{Guid.NewGuid():N}@test.local");

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_with_the_right_password_next()
    {
        var email = $"lockout-{Guid.NewGuid():N}@test.local";
        const string password = "Test1234!";
        await CreateConfirmedUserAsync(email, password);

        // A fresh address per attempt keeps the per-address limit out of this test.
        for (var i = 0; i < 5; i++)
        {
            var wrong = await LoginAsync(ClientFrom($"198.51.100.{i + 1}"), email, "WrongPassword99!");
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var locked = await LoginAsync(ClientFrom("198.51.100.50"), email, password);

        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the account is locked out");
    }

    [Fact]
    public async Task A_successful_login_clears_earlier_failures()
    {
        var email = $"lockout-reset-{Guid.NewGuid():N}@test.local";
        const string password = "Test1234!";
        await CreateConfirmedUserAsync(email, password);

        for (var i = 0; i < 4; i++)
            await LoginAsync(ClientFrom($"198.51.101.{i + 1}"), email, "WrongPassword99!");
        (await LoginAsync(ClientFrom("198.51.101.10"), email, password)).StatusCode.Should().Be(HttpStatusCode.OK);

        await LoginAsync(ClientFrom("198.51.101.11"), email, "WrongPassword99!");
        var next = await LoginAsync(ClientFrom("198.51.101.12"), email, password);

        next.StatusCode.Should().Be(HttpStatusCode.OK, "the count restarted after the successful login");
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("10.1.1.1, 203.0.113.6", "203.0.113.6")]
    [InlineData("203.0.113.7:51234", "203.0.113.7")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("not-an-address", "127.0.0.1")]
    public void Client_address_is_the_last_forwarded_entry_or_the_connection(string forwarded, string expected)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers["X-Forwarded-For"] = forwarded;

        AuthRateLimiting.ClientAddress(http).Should().Be(expected);
    }

    private HttpClient ClientFrom(string forwardedFor)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password = "Test1234!") =>
        client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = "Test1234!" });

    private async Task CreateConfirmedUserAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Lockout" };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        (await users.ConfirmEmailAsync(user, token)).Succeeded.Should().BeTrue();
    }
}
