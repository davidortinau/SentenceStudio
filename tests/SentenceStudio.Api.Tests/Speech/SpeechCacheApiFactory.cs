using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Speech;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The real API with the paid speech provider replaced by <see cref="FakeSpeechClipProvider"/> and
/// the media container replaced by <see cref="InMemorySpeechClipStore"/>. No ElevenLabs key is
/// configured, so nothing here can reach ElevenLabs.
/// </summary>
public sealed class SpeechCacheApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        $"sentencestudio_speechcache_{Guid.NewGuid():N}.db");

    internal FakeSpeechClipProvider Provider { get; } = new();

    internal InMemorySpeechClipStore Store { get; } = new();

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

        builder.ConfigureServices((context, services) =>
        {
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath);
            TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, context.Configuration);

            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });

            services.RemoveAll<ISpeechClipProvider>();
            services.AddSingleton<ISpeechClipProvider>(Provider);
            services.RemoveAll<ISpeechClipStore>();
            services.AddSingleton<ISpeechClipStore>(Store);
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
