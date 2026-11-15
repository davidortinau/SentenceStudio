using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services;

/// <summary>
/// The MAUI sign-in client under the MAUI HTTP defaults (#89). The defaults put the standard
/// resilience pipeline on every named client; it retries a 429 and waits out its Retry-After,
/// which would outlast the client's timeout and hide the "too many attempts" answer.
/// </summary>
public class AuthClientRateLimitTests
{
    [Fact]
    public async Task A_rate_limited_sign_in_is_sent_once_and_returns_the_429()
    {
        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        // What AddMauiServiceDefaults does for every client.
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddAuthServices(new ConfigurationBuilder().Build(), new Uri("https://api.example.test"));
        services.AddHttpClient("AuthClient").ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AuthClient");
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = "a@b.test", Password = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        handler.Calls.Should().Be(1, "a retried sign-in waits out Retry-After and times out with a generic error");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45));
            return Task.FromResult(response);
        }
    }
}
