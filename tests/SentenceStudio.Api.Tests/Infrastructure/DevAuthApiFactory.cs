using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Test host using the default DevAuthHandler configuration.
/// All requests are auto-authenticated with dev claims.
/// Simulates Auth:UseEntraId=false (local development mode).
/// </summary>
public class DevAuthApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
