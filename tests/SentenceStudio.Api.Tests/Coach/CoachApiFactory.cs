using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Boots the real API host with the coach wired in and no AI configuration at all.
/// </summary>
/// <remarks>
/// <para>
/// The host must start without an <c>IChatClient</c>: the coach resolves one lazily and only
/// when a turn needs it, so a deploy with the coach off (or with AI not yet configured) is
/// not allowed to fail at startup.
/// </para>
/// <para>
/// Both databases are redirected to SQLite. The coach context is created rather than migrated
/// because its migrations are PostgreSQL-only by design — the same entity configuration runs
/// on both providers, which is what these tests exercise.
/// </para>
/// </remarks>
public class CoachApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_coach_{Guid.NewGuid():N}.db");

    private readonly string _coachDbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_coachstate_{Guid.NewGuid():N}.db");

    /// <summary>Set before the first request to turn the coach on for the seeded learner.</summary>
    public bool CoachEnabled { get; init; }

    /// <summary>The learner id placed in the cohort allow-list.</summary>
    public string? CohortUserProfileId { get; init; }

    /// <summary>Set to turn durable conversation history on for this host.</summary>
    public bool DurableHistory { get; init; }

    /// <summary>Set to turn learner memory on for this host. Independent of history.</summary>
    public bool Memory { get; init; }

    /// <summary>
    /// Set to register a <see cref="CoachKeyRingPlan"/> that reports itself durable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operator evidence route refuses before it loads a row when the key ring is ephemeral —
    /// deliberately, so an ephemeral host cannot be used to probe which opportunity identifiers
    /// exist. That refusal is correct, and it also means a default test host can never reach the
    /// gates <em>behind</em> it: the cross-owner check, the Product-disposition check, and the
    /// pointer checks are all unreachable.
    /// </para>
    /// <para>
    /// A real durable ring needs blob storage, which a <c>WebApplicationFactory</c> has no
    /// business standing up. Only the <c>IsDurable</c> flag gates the route, and nothing is
    /// decrypted before the checks under test, so this registers a plan whose mode is durable
    /// while the host's actual protector stays exactly as it was.
    /// </para>
    /// </remarks>
    public bool DurableKeyRing { get; init; }

    /// <summary>
    /// A chat client that fails if it is ever used. Coach paths that must not call the model
    /// assert <see cref="RecordingChatClient.CallCount"/> stays at zero.
    /// </summary>
    public RecordingChatClient ChatClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:sentencestudio", TestApiHostConfigurator.DummyPostgresConnectionString);
        builder.UseSetting("Database:SkipMigrateOnStartup", "true");
        builder.UseSetting("Auth:SeedDevTestAccounts", "false");
        builder.UseSetting("Auth:EnableDevAuthFallback", "false");
        builder.UseSetting("Jwt:SigningKey", TestJwtGenerator.TestSigningKeyValue);
        builder.UseSetting("Jwt:Issuer", TestJwtGenerator.TestIssuer);
        builder.UseSetting("Jwt:Audience", TestJwtGenerator.TestAudience);

        // Deliberately no AI:OpenAI:Endpoint and no AI:OpenAI:ApiKey.
        builder.UseSetting("Coach:Enabled", CoachEnabled ? "true" : "false");
        builder.UseSetting("Coach:DurableHistory:Enabled", DurableHistory ? "true" : "false");
        builder.UseSetting("Coach:Memory:Enabled", Memory ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(CohortUserProfileId))
        {
            builder.UseSetting("Coach:AllowedUserProfileIds:0", CohortUserProfileId);
        }

        builder.ConfigureServices(services =>
        {
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath);
            ReplaceCoachContextWithSqlite(services, _coachDbPath);

            // The shared test host already supplies a stand-in IChatClient so the API's
            // unconditional AiService registration can be constructed. This overrides it with
            // one that counts calls, so the coach tests can assert that model-free paths —
            // availability, the feature gate, tapped accept/reject — never reach a model.
            services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(ChatClient);

            if (DurableKeyRing)
            {
                services.RemoveAll<SentenceStudio.Api.Security.DataProtection.CoachKeyRingPlan>();
                services.AddSingleton(new SentenceStudio.Api.Security.DataProtection.CoachKeyRingPlan
                {
                    Mode = SentenceStudio.Api.Security.DataProtection.CoachKeyRingMode
                        .AzureBlobConnectionString,
                    ApplicationName = "sentencestudio-coach-tests",
                    ContainerName = "keyring",
                    BlobName = "keys.xml"
                });
            }

            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        TestApiHostConfigurator.InitializeSqliteDatabaseAndSync(host.Services);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CoachDbContext>().Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var path in new[] { _dbPath, _coachDbPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void ReplaceCoachContextWithSqlite(IServiceCollection services, string dbPath)
    {
        foreach (var descriptor in services
                     .Where(d => d.ServiceType == typeof(DbContextOptions<CoachDbContext>)
                                 || (d.ServiceType.IsGenericType
                                     && d.ServiceType.GenericTypeArguments.Contains(typeof(CoachDbContext))))
                     .ToList())
        {
            services.Remove(descriptor);
        }

        services.RemoveAll<CoachDbContext>();

        services.AddDbContext<CoachDbContext>(options =>
        {
            options.UseSqlite($"Data Source={dbPath}");
            // No PendingModelChangesWarning suppression: production stopped suppressing it for
            // this context, and a test host that still did would boot against a model the
            // migrations no longer describe.
        });
    }
}
