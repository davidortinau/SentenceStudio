using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace SentenceStudio.Api.Auth;

/// <summary>
/// Limits on the anonymous auth endpoints, per client address, so a password or reset token
/// cannot be guessed at speed (#89). Bound from <c>Auth:RateLimit</c>.
/// </summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "Auth:RateLimit";

    /// <summary>Login attempts per client address per minute.</summary>
    public int LoginPerMinute { get; set; } = 5;

    /// <summary>Registrations per client address per hour.</summary>
    public int RegisterPerHour { get; set; } = 10;

    /// <summary>Forgot-password, reset-password and confirm-email requests per client address per hour.</summary>
    public int AccountRecoveryPerHour { get; set; } = 10;
}

public static class AuthRateLimiting
{
    public const string LoginPolicy = "auth-login";
    public const string RegisterPolicy = "auth-register";
    public const string AccountRecoveryPolicy = "auth-account-recovery";

    private sealed class AuthRateLimitLog { }

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthRateLimitOptions>(configuration.GetSection(AuthRateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                // The path names the endpoint; the client address is personal data and is not logged.
                context.HttpContext.RequestServices.GetRequiredService<ILogger<AuthRateLimitLog>>()
                    .LogWarning("Auth rate limit refused {Method} {Path}",
                        context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value);
                return ValueTask.CompletedTask;
            };

            limiter.AddPolicy(LoginPolicy, http => Window(http, LoginPolicy,
                options => options.LoginPerMinute, TimeSpan.FromMinutes(1)));
            limiter.AddPolicy(RegisterPolicy, http => Window(http, RegisterPolicy,
                options => options.RegisterPerHour, TimeSpan.FromHours(1)));
            limiter.AddPolicy(AccountRecoveryPolicy, http => Window(http, AccountRecoveryPolicy,
                options => options.AccountRecoveryPerHour, TimeSpan.FromHours(1)));
        });

        return services;
    }

    private static RateLimitPartition<string> Window(
        HttpContext http, string policy, Func<AuthRateLimitOptions, int> limit, TimeSpan window)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
        var permits = Math.Max(1, limit(options));
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{policy}:{ClientAddress(http)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
            });
    }

    /// <summary>
    /// The address the request came from. In production the API sits behind the Container Apps
    /// ingress, which appends the client it saw to <c>X-Forwarded-For</c>; the last entry is that
    /// one, and anything before it was written by the client and is ignored. Without the header
    /// (local runs, tests) the connection's address is used.
    /// </summary>
    internal static string ClientAddress(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            if (last is not null && IPEndPoint.TryParse(last, out var endpoint))
                return endpoint.Address.ToString();
        }

        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
