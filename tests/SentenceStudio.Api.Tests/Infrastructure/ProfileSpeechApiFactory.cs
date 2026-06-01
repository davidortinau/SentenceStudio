using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Test host for ProfileEndpoints + SpeechEndpoints integration tests.
/// Differs from <see cref="JwtBearerApiFactory"/> in two ways:
/// 1. Registers a <see cref="UserProfilesQueryCounter"/> EF interceptor so
///    fetch-all anti-patterns can be asserted at the DB layer.
/// 2. Replaces the production <see cref="IVoiceDiscoveryService"/> with
///    <see cref="StubVoiceDiscoveryService"/> so SpeechEndpoints tests are
///    deterministic and never hit ElevenLabs.
/// </summary>
public class ProfileSpeechApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        $"sentencestudio_profilespeech_{Guid.NewGuid():N}.db");

    public UserProfilesQueryCounter QueryCounter { get; } = new();
    public StubVoiceDiscoveryService VoiceService { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:sentencestudio",
            TestApiHostConfigurator.DummyPostgresConnectionString);
        builder.UseSetting("Database:SkipMigrateOnStartup", "true");
        builder.UseSetting("Auth:SeedDevTestAccounts", "false");
        builder.UseSetting("Auth:EnableDevAuthFallback", "false");
        builder.UseSetting("Jwt:SigningKey", TestJwtGenerator.TestSigningKeyValue);
        builder.UseSetting("Jwt:Issuer", TestJwtGenerator.TestIssuer);
        builder.UseSetting("Jwt:Audience", TestJwtGenerator.TestAudience);
        builder.UseSetting("AI:OpenAI:ApiKey", "test-openai-key");

        builder.ConfigureServices(services =>
        {
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath, QueryCounter);

            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });

            services.RemoveAll<IVoiceDiscoveryService>();
            services.AddSingleton<IVoiceDiscoveryService>(VoiceService);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        TestApiHostConfigurator.InitializeSqliteDatabaseAndSync(host.Services);
        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
