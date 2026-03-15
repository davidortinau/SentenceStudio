using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for the Identity auth endpoints (register, login, confirm-email, refresh).
/// Uses JwtBearerApiFactory so Identity services and JWT signing are configured.
/// </summary>
public class IdentityAuthTests : IClassFixture<JwtBearerApiFactory>
{
    private readonly JwtBearerApiFactory _factory;
    private readonly HttpClient _client;

    public IdentityAuthTests(JwtBearerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_ReturnsOk()
    {
        var email = $"register-ok-{Guid.NewGuid():N}@test.local";

        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "Test1234!",
            DisplayName = "Tester"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "register should succeed for a new user");
    }

    [Fact]
    public async Task Login_UnconfirmedEmail_Returns401()
    {
        var email = $"unconfirmed-{Guid.NewGuid():N}@test.local";

        // Register but do NOT confirm email
        var reg = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "Test1234!",
        });
        reg.EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = "Test1234!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "login should be rejected when email is not confirmed");
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var email = $"wrongpw-{Guid.NewGuid():N}@test.local";

        // Register and confirm
        await RegisterAndConfirmAsync(email, "Test1234!");

        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = "WrongPassword99!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "login should be rejected with wrong password");
    }

    [Fact]
    public async Task ConfirmEmail_ThenLogin_Succeeds()
    {
        var email = $"confirm-{Guid.NewGuid():N}@test.local";
        const string password = "Test1234!";

        // Register
        var reg = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = password,
        });
        reg.EnsureSuccessStatusCode();

        // Confirm email via UserManager
        await ConfirmEmailDirectlyAsync(email);

        // Login should now succeed
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = password
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "login should succeed after email confirmation");

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        auth.Should().NotBeNull();
        auth!.Token.Should().NotBeNullOrWhiteSpace("JWT should be returned");
        auth.RefreshToken.Should().NotBeNullOrWhiteSpace("refresh token should be returned");
    }

    [Fact]
    public async Task RefreshToken_ReturnsNewTokens()
    {
        var email = $"refresh-{Guid.NewGuid():N}@test.local";
        const string password = "Test1234!";

        await RegisterAndConfirmAsync(email, password);

        // Login to get initial tokens
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();

        var initial = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        initial.Should().NotBeNull();

        // Use refresh token to get new tokens
        var refreshResponse = await _client.PostAsJsonAsync("/api/auth/refresh", new
        {
            RefreshToken = initial!.RefreshToken
        });

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "refresh should return new tokens");

        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>();
        refreshed.Should().NotBeNull();
        refreshed!.Token.Should().NotBeNullOrWhiteSpace("new JWT should be returned");
        refreshed.RefreshToken.Should().NotBeNullOrWhiteSpace("new refresh token should be returned");
        refreshed.RefreshToken.Should().NotBe(initial.RefreshToken,
            "refresh token should be rotated");
    }

    // -- helpers --

    private async Task RegisterAndConfirmAsync(string email, string password)
    {
        var reg = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = password,
        });
        reg.EnsureSuccessStatusCode();
        await ConfirmEmailDirectlyAsync(email);
    }

    private async Task ConfirmEmailDirectlyAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull($"user {email} should exist after registration");

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        var result = await userManager.ConfirmEmailAsync(user!, token);
        result.Succeeded.Should().BeTrue("email confirmation should succeed");
    }
}
