using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Conversation;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Test host for Conversation endpoint tests. Replaces the real
/// <see cref="IServerConversationService"/> with
/// <see cref="StubServerConversationService"/> so tests never call OpenAI
/// and can deterministically assert wire shape and routing behavior.
/// </summary>
public class ConversationApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        $"sentencestudio_conversation_{Guid.NewGuid():N}.db");

    public StubServerConversationService ConversationService { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:sentencestudio",
            TestApiHostConfigurator.DummyPostgresConnectionString);
        builder.UseSetting("Database:SkipMigrateOnStartup", "true");
        builder.UseSetting("Auth:EnableDevAuthFallback", "false");
        builder.UseSetting("Jwt:SigningKey", TestJwtGenerator.TestSigningKeyValue);
        builder.UseSetting("Jwt:Issuer", TestJwtGenerator.TestIssuer);
        builder.UseSetting("Jwt:Audience", TestJwtGenerator.TestAudience);

        builder.ConfigureServices(services =>
        {
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath);

            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });

            services.RemoveAll<IServerConversationService>();
            services.AddSingleton<IServerConversationService>(ConversationService);
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
