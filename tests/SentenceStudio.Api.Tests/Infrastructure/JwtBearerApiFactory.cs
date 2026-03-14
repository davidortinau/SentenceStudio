using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Test host configured with JWT Bearer authentication.
/// Replaces DevAuthHandler with real JWT validation using a test signing key.
/// Explicitly sets Auth:UseEntraId=true to simulate the production Entra ID path.
/// </summary>
public class JwtBearerApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:UseEntraId"] = "true"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Override auth to use JWT Bearer instead of DevAuthHandler
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters = TestJwtGenerator.CreateValidationParameters();
                });
        });
    }
}
