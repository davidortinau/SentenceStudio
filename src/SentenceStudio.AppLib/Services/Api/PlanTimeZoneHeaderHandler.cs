using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Services.Api;

/// <summary>
/// Attaches the learner's IANA timezone to every outgoing plan-date-sensitive API call as
/// <see cref="PlanDateHeaders.TimeZone"/> (<c>X-Timezone</c>).
/// </summary>
/// <remarks>
/// <para>
/// The API resolves its per-request <c>IPlanDateContext</c> from this header and falls back to
/// UTC when it is absent. Because no client sent it, every server-side plan-date read used UTC:
/// at 21:52 on Aug 14 in America/Chicago the UTC date is already Aug 15, so a plan generated for
/// Aug 14 looked absent and <c>GET /api/v1/coach/availability</c> answered Disabled even though
/// Today's Plan existed.
/// </para>
/// <para>
/// The context is resolved from the root provider on EVERY request rather than injected into the
/// constructor. <c>HttpClientFactory</c> caches a handler chain for minutes, so a constructor
/// -injected context would freeze one learner's timezone (and one calendar date) into the chain
/// and hand it to everybody else. Both hosts register <c>IPlanDateContext</c> as transient and
/// resolve the current learner from ambient state — <c>CircuitUserStateAccessor</c> on the web,
/// the active device profile on native — so a fresh root resolution per request is both safe and
/// correct.
/// </para>
/// <para>
/// An explicitly set header is never overwritten, so a caller can still pin a timezone.
/// </para>
/// </remarks>
public sealed class PlanTimeZoneHeaderHandler(IServiceProvider serviceProvider) : DelegatingHandler
{
    private readonly IServiceProvider _serviceProvider = serviceProvider
        ?? throw new ArgumentNullException(nameof(serviceProvider));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.Contains(PlanDateHeaders.TimeZone))
        {
            var timeZoneId = ResolveTimeZoneId();
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                request.Headers.TryAddWithoutValidation(PlanDateHeaders.TimeZone, timeZoneId);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }

    private string? ResolveTimeZoneId()
    {
        try
        {
            // Resolved per request. Never cached, never captured.
            var context = _serviceProvider.GetService<IPlanDateContext>();
            return context?.TimeZone.Id;
        }
        catch (Exception)
        {
            // A missing or failing plan-date context must not break the API call. The server
            // then falls back to UTC, which is the behavior we had before this handler existed.
            return null;
        }
    }
}
