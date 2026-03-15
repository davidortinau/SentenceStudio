using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Test host using the default DevAuthHandler configuration.
/// All requests are auto-authenticated with dev claims.
/// Explicitly sets Auth:UseEntraId=false (local development mode).
/// </summary>
public class DevAuthApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:UseEntraId"] = "false"
            });
        });
    }
}
