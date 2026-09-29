using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Startup;
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
    private readonly Dictionary<string, string?> _previousHostSettings = new(StringComparer.Ordinal);
    private string? _database;

    /// <summary>The signing key the WebApp mints API tokens with during a test run.</summary>
    public const string JwtSigningKey = "webapp-integration-test-signing-key-at-least-32-chars";

    /// <summary>A unique scratch directory under the test output, never the user's app data.</summary>
    public string AppDataRoot { get; } = CreateAppDataRoot();

    public OperatorWebAppFactory(string label, string? environment = null)
    {
        _label = label;
        _environment = environment ?? Environments.Development;
    }

    /// <summary>The stub the WebApp's operator client talks to.</summary>
    public StubOperatorApi Operator { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        var initialized = false;
        try
        {
            _database = await WebAppPostgresServer.CreateDatabaseAsync(_label);
            Operator = await StubOperatorApi.StartAsync();

            // Only the fixture migrates this new database; the WebApp host must only read it.
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(WebAppPostgresServer.ConnectionStringFor(_database))
                .AddInterceptors(new WebAppPostgresMigrationGuard(_database))
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using (var db = new ApplicationDbContext(options))
            {
                Operator.VersionMigrationId = db.Database.GetMigrations().Last();
                await db.Database.OpenConnectionAsync();
                try
                {
                    await WebAppPostgresServer.AssertOwnedDatabaseAsync(
                        db.Database.GetDbConnection(), _database);
                    await db.Database.MigrateAsync();
                    await WebAppPostgresServer.AssertOwnedDatabaseAsync(
                        db.Database.GetDbConnection(), _database);
                    Operator.VersionDatabaseIdentity = await PostgresDatabaseIdentity.ReadAsync(
                        db.Database.GetDbConnection(), Operator.VersionMigrationId);
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            }

            ApplyHostSettings();
            _ = Services;
            initialized = true;
        }
        finally
        {
            if (!initialized)
            {
                ClearHostSettings();
                if (Operator is { } operatorApi)
                {
                    Operator = null!;
                    await operatorApi.DisposeAsync();
                }
            }
        }
    }

    public new async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            try
            {
                if (Operator is { } operatorApi)
                {
                    Operator = null!;
                    await operatorApi.DisposeAsync();
                }
            }
            finally
            {
                ClearHostSettings();
            }
        }
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
            _previousHostSettings[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private void ClearHostSettings()
    {
        foreach (var (key, value) in _previousHostSettings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
        _previousHostSettings.Clear();
    }

    private static string CreateAppDataRoot()
    {
        var project = new DirectoryInfo(AppContext.BaseDirectory);
        while (project is not null &&
               !File.Exists(Path.Combine(project.FullName, "SentenceStudio.WebApp.Tests.csproj")))
        {
            project = project.Parent;
        }

        var output = Path.GetFullPath(AppContext.BaseDirectory);
        if (project is null ||
            !output.StartsWith(
                Path.Combine(project.FullName, "bin") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "WebApp test data must live under this test project's own bin directory.");
        }

        return Path.Combine(output, "webapp-test-data", Guid.NewGuid().ToString("N"));
    }

    private IEnumerable<KeyValuePair<string, string?>> HostSettings() =>
    [
        new("ConnectionStrings__sentencestudio", WebAppPostgresServer.ConnectionStringFor(_database!)),
        new("WebApp__AppDataRoot", AppDataRoot),

        // The operator client's base address. Service discovery passes a concrete loopback URL
        // through untouched.
        new("ApiBaseUrl", Operator.BaseAddress),

        // Without a signing key the WebApp mints no API token at all, which would make every
        // token-forwarding assertion vacuously "no header" for the wrong reason.
        new("Jwt__SigningKey", JwtSigningKey),
        new("Jwt__Issuer", "SentenceStudio"),
        new("Jwt__Audience", "SentenceStudio.Api"),

        // Production-mode tests must not export their synthetic telemetry to a configured
        // production collector. Restore both inherited values when the factory is disposed.
        new("AzureMonitor__ConnectionString", ""),
        new("OTEL_EXPORTER_OTLP_ENDPOINT", ""),

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
        await db.Database.OpenConnectionAsync();
        await WebAppPostgresServer.AssertOwnedDatabaseAsync(
            db.Database.GetDbConnection(), _database!);

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
