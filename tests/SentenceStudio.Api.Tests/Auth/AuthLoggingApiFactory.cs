using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Tests.Auth;

/// <summary>
/// Test host for the auth endpoints with a recording logger attached, so a test can inspect
/// exactly what the endpoints emitted. The mail sender is stubbed out: this suite is about what
/// the endpoints log, and leaving the real console sender in place would blur the two.
/// </summary>
public sealed class AuthLoggingApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        AppContext.BaseDirectory, "authlog-tests", $"auth_log_{Guid.NewGuid():N}.db");

    public AuthLogRecorder Recorder { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

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
        builder.UseSetting("ElevenLabsKey", "test-elevenlabs-key");

        builder.ConfigureServices((context, services) =>
        {
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath);
            TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, context.Configuration);

            services.RemoveAll<IAppEmailSender>();
            services.AddSingleton<IAppEmailSender, SilentEmailSender>();
        });

        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Recorder);
            logging.SetMinimumLevel(LogLevel.Trace);
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

    private sealed class SilentEmailSender : IAppEmailSender
    {
        public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
            => Task.CompletedTask;

        public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
            => Task.CompletedTask;

        public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
            => Task.CompletedTask;

        public Task SendEmailAsync(string toEmail, string subject, string htmlBody)
            => Task.CompletedTask;
    }
}
