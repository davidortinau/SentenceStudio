using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Tests.Infrastructure;

/// <summary>
/// Boots the real WebApp pipeline against a throwaway PostgreSQL database and a stub operator API.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about authentication is replaced. The point of this family is that a learner who signed
/// in through the real cookie path stays signed in across a full document load of the operator
/// route, and a test authentication handler would assert that against a fiction. Sign-in therefore
/// goes through <c>/account-action/AutoSignIn</c>, the same endpoint the login page redirects to,
/// and the resulting Identity cookie is the one the browser would hold.
/// </para>
/// <para>
/// The environment is Development because the operator surface exists only there. That is the
/// configuration under test, not a convenience.
/// </para>
/// </remarks>
public sealed class OperatorWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _label;
    private readonly string _environment;
    private string? _database;

    /// <summary>The signing key the WebApp mints API tokens with during a test run.</summary>
    public const string JwtSigningKey = "webapp-integration-test-signing-key-at-least-32-chars";

    public OperatorWebAppFactory(string label, string? environment = null)
    {
        _label = label;
        _environment = environment ?? Environments.Development;
    }

    /// <summary>The stub the WebApp's operator client talks to.</summary>
    public StubOperatorApi Operator { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Operator = await StubOperatorApi.StartAsync();
        _database = await WebAppPostgresServer.CreateDatabaseAsync(_label);

        ApplyHostSettings();

        // Force host construction now so a migration failure surfaces here rather than inside the
        // first assertion.
        _ = Services;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        if (Operator is not null)
        {
            await Operator.DisposeAsync();
        }

        if (_database is not null)
        {
            await WebAppPostgresServer.DropDatabaseAsync(_database);
        }

        ClearHostSettings();
    }

    /// <summary>
    /// Publishes this factory's settings where the application will read them.
    /// </summary>
    /// <remarks>
    /// Environment variables rather than <c>ConfigureAppConfiguration</c>. The WebApp uses minimal
    /// hosting and calls <c>builder.Build()</c> itself, which validates the container before the
    /// test host's configuration callbacks have run — so a connection string supplied that way
    /// arrives after the only moment it could have been used. Environment variables are already in
    /// the default configuration sources when <c>WebApplication.CreateBuilder</c> runs, which is
    /// early enough.
    ///
    /// Because these are process-wide, this assembly disables test parallelisation; see
    /// <c>AssemblyInfo.cs</c>.
    /// </remarks>
    private void ApplyHostSettings()
    {
        foreach (var (key, value) in HostSettings())
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private void ClearHostSettings()
    {
        foreach (var (key, _) in HostSettings())
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private IEnumerable<KeyValuePair<string, string?>> HostSettings() =>
    [
        new("ConnectionStrings__sentencestudio", WebAppPostgresServer.ConnectionStringFor(_database!)),

        // The operator client's base address. Service discovery passes a concrete loopback URL
        // through untouched.
        new("ApiBaseUrl", Operator.BaseAddress),

        // Without a signing key the WebApp mints no API token at all, which would make every
        // token-forwarding assertion vacuously "no header" for the wrong reason.
        new("Jwt__SigningKey", JwtSigningKey),
        new("Jwt__Issuer", "SentenceStudio"),
        new("Jwt__Audience", "SentenceStudio.Api"),

        // Keep the host off the network for everything that is not the stub.
        //
        // A syntactically valid endpoint is supplied rather than none at all: the WebApp registers
        // its chat clients only when one is present, and those registrations are what satisfy
        // container validation for the services that depend on IChatClient. Every one of them is a
        // lazy factory, so nothing is constructed and no credential is acquired unless something
        // resolves a chat client — and nothing under test does.
        new("AI__OpenAI__Endpoint", "https://webapp-integration-tests.invalid/openai/v1")
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }

    /// <summary>Creates a client that reports redirects instead of following them.</summary>
    /// <remarks>
    /// The whole question this family answers is "does this request redirect to the login page",
    /// so a client that followed redirects would turn the symptom into a 200 on a different page.
    /// </remarks>
    public HttpClient CreateBrowserClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost")
    });

    /// <summary>Creates an Identity account with a linked profile, and returns the profile id.</summary>
    public async Task<SeededLearner> SeedLearnerAsync(string email, string password)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = new UserProfile
        {
            Id = Guid.NewGuid().ToString(),
            Name = email,
            Email = email,
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            CreatedAt = DateTime.UtcNow
        };

        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = email,
            EmailConfirmed = true,
            UserProfileId = profile.Id
        };

        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Seeding the test learner failed: "
                + string.Join("; ", created.Errors.Select(e => e.Code)));
        }

        return new SeededLearner(user.Id, profile.Id, email);
    }

    /// <summary>
    /// Signs <paramref name="learner"/> in on <paramref name="client"/> through the real cookie
    /// endpoint, leaving the Identity cookie in the client's cookie container.
    /// </summary>
    public async Task SignInAsync(HttpClient client, SeededLearner learner)
    {
        string token;
        using (var scope = Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(learner.UserId)
                ?? throw new InvalidOperationException("The seeded learner disappeared before sign-in.");
            token = await users.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, "AutoSignIn");
        }

        var response = await client.GetAsync(
            $"/account-action/AutoSignIn?userId={Uri.EscapeDataString(learner.UserId)}"
            + $"&token={Uri.EscapeDataString(token)}&returnUrl=%2F");

        if (response.StatusCode is not (System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.OK))
        {
            throw new InvalidOperationException(
                $"AutoSignIn answered {(int)response.StatusCode}, so no session was established.");
        }

        var location = response.Headers.Location?.OriginalString;
        if (location is not null && location.Contains("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AutoSignIn bounced back to the login page, so no session was established.");
        }
    }

    /// <summary>An account this family created.</summary>
    public sealed record SeededLearner(string UserId, string UserProfileId, string Email);
}
