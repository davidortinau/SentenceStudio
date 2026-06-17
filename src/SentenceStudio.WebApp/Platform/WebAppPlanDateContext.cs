using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.WebApp.Platform;

/// <summary>
/// Webapp <see cref="IPlanDateContext"/> that resolves the user's timezone from
/// their persisted <c>UserProfile.IanaTimeZoneId</c>. Falls back to UTC when:
///   (a) no authenticated user in the current circuit/request, or
///   (b) the user has not yet captured their timezone (IanaTimeZoneId is null).
///
/// Registered as Scoped so each circuit/request gets a fresh resolution.
/// Mirrors the API's <c>HttpPlanDateContext</c> pattern but sources the timezone
/// from the database rather than an HTTP header.
/// </summary>
public sealed class WebAppPlanDateContext : IPlanDateContext
{
    private readonly IPlanDateContext _inner;

    public WebAppPlanDateContext(
        IServiceProvider serviceProvider,
        ILogger<WebAppPlanDateContext> logger)
    {
        // Resolve user profile id from circuit state or HttpContext
        string? userProfileId = null;

        var circuitState = serviceProvider.GetService<CircuitUserStateAccessor>();
        if (circuitState?.Current?.UserProfileId is { Length: > 0 } profileId)
        {
            userProfileId = profileId;
        }
        else
        {
            // Fallback: try HttpContext (SSR prerender)
            var httpAccessor = serviceProvider.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            var claim = httpAccessor?.HttpContext?.User?.FindFirst(SentenceStudio.Contracts.AuthClaimTypes.UserProfileId);
            if (claim?.Value is { Length: > 0 } claimValue)
            {
                userProfileId = claimValue;
            }
        }

        TimeZoneInfo zone = TimeZoneInfo.Utc;

        if (!string.IsNullOrEmpty(userProfileId))
        {
            try
            {
                // Read the user's persisted IANA timezone synchronously (constructor)
                // Use a scoped DbContext to avoid concurrency issues.
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var ianaId = db.UserProfiles
                    .Where(p => p.Id == userProfileId)
                    .Select(p => p.IanaTimeZoneId)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(ianaId) && TimeZoneResolver.TryResolve(ianaId, out var resolved))
                {
                    zone = resolved;
                    logger.LogDebug(
                        "WebAppPlanDateContext: resolved timezone '{IanaId}' for user '{UserId}'",
                        ianaId, userProfileId);
                }
                else
                {
                    logger.LogDebug(
                        "WebAppPlanDateContext: user '{UserId}' has no IanaTimeZoneId — falling back to UTC",
                        userProfileId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "WebAppPlanDateContext: failed to read timezone for user '{UserId}' — falling back to UTC",
                    userProfileId);
            }
        }
        else
        {
            logger.LogDebug("WebAppPlanDateContext: no authenticated user — using UTC");
        }

        _inner = new PlanDateContext(zone);
    }

    public DateOnly UserLocalDate => _inner.UserLocalDate;
    public DateTime UtcNow => _inner.UtcNow;
    public TimeZoneInfo TimeZone => _inner.TimeZone;
    public DateOnly ToUserLocal(DateTime utc) => _inner.ToUserLocal(utc);
    public DateTime ToUtcMidnight(DateOnly userLocal) => _inner.ToUtcMidnight(userLocal);
}
