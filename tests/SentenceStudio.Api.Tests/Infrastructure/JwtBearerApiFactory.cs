using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Test host configured with IdentityJwt Bearer authentication.
/// Uses an isolated SQLite database per factory instance.
/// Sets Auth:UseEntraId=false with Development environment, then overrides
/// the default auth scheme to IdentityJwt.
/// </summary>
public class JwtBearerApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        $"sentencestudio_test_{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Auth:UseEntraId", "false");
        builder.UseSetting("Jwt:SigningKey", TestJwtGenerator.TestSigningKeyValue);
        builder.UseSetting("Jwt:Issuer", TestJwtGenerator.TestIssuer);
        builder.UseSetting("Jwt:Audience", TestJwtGenerator.TestAudience);

        builder.ConfigureServices(services =>
        {
            // Replace the production DbContext with a test-specific SQLite file
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor != null)
                services.Remove(descriptor);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite($"Data Source={_dbPath}"));

            // Override default auth to use IdentityJwt scheme (already registered by the API
            // because we provide Jwt:SigningKey via UseSetting above)
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = "IdentityJwt";
                options.DefaultChallengeScheme = "IdentityJwt";
            });

            // Ensure the database schema is created
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
