using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers a named "openai" HttpClient with Polly resilience (429/5xx retry, circuit breaker, timeout)
/// so that OpenAIClient traffic flows through the standard resilience pipeline.
/// </summary>
public static class OpenAIExtensions
{
    /// <summary>
    /// Registers a named "openai" HttpClient. Server projects that call AddServiceDefaults() already
    /// get resilience from ConfigureHttpClientDefaults — pass <paramref name="addExplicitResilience"/>
    /// = true for MAUI / non-Aspire hosts that don't have global defaults.
    /// </summary>
    public static IServiceCollection AddResilientOpenAIHttpClient(
        this IServiceCollection services,
        bool addExplicitResilience = false)
    {
        var builder = services.AddHttpClient("openai");

        if (addExplicitResilience)
        {
            builder.AddStandardResilienceHandler(options =>
            {
                // Match the timeouts in AddServiceDefaults for AI-class workloads
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(300);
            });
        }

        return services;
    }
}
