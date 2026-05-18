using Microsoft.AspNetCore.Http;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Plans;

/// <summary>
/// API-side <see cref="IPlanDateContext"/> resolved per request from the
/// <c>X-Timezone</c> header (IANA preferred, Windows ids also accepted).
/// Falls back to <see cref="TimeZoneInfo.Utc"/> when the header is missing
/// or unrecognized.
/// </summary>
public sealed class HttpPlanDateContext : IPlanDateContext
{
    public const string TimeZoneHeader = "X-Timezone";

    private readonly IPlanDateContext _inner;

    public HttpPlanDateContext(IHttpContextAccessor httpContext)
    {
        var headerValue = httpContext.HttpContext?.Request.Headers[TimeZoneHeader].ToString();
        TimeZoneResolver.TryResolve(headerValue, out var zone);
        _inner = new PlanDateContext(zone);
    }

    public DateOnly UserLocalDate => _inner.UserLocalDate;
    public DateTime UtcNow => _inner.UtcNow;
    public TimeZoneInfo TimeZone => _inner.TimeZone;
    public DateOnly ToUserLocal(DateTime utc) => _inner.ToUserLocal(utc);
    public DateTime ToUtcMidnight(DateOnly userLocal) => _inner.ToUtcMidnight(userLocal);
}

