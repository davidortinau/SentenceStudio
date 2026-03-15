using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Test host using the default DevAuthHandler configuration.
/// All requests are auto-authenticated with dev claims.
/// Uses Development environment so DevAuthHandler path is active.
/// </summary>
public class DevAuthApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Clear JWT key so the API falls back to DevAuthHandler
        builder.UseSetting("Jwt:SigningKey", "");
    }
}
