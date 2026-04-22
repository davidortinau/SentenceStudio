using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services.Observability;

/// <summary>
/// Delegating handler that starts a <see cref="System.Diagnostics.Activity"/> per outbound HTTP request
/// so that mobile→API calls propagate a W3C <c>traceparent</c> header and show up as dependencies
/// in Application Insights linked to the server-side request by <c>operation_Id</c>.
///
/// <para>
/// Background: <c>OpenTelemetry.Instrumentation.Http</c>'s auto-instrumentation relies on the
/// <see cref="System.Diagnostics.ActivitySource"/> it registers being active at the time HttpClient
/// sends a request. On a full <see cref="Microsoft.Extensions.Hosting.IHost"/>, that happens via
/// <c>TelemetryHostedService.StartAsync</c> which materialises the <c>TracerProvider</c> and its
/// listeners. MAUI <c>MauiApp</c> doesn't run <see cref="Microsoft.Extensions.Hosting.IHostedService"/>,
/// so the provider can end up unstarted — logs still flow (they hook <c>ILoggerFactory</c> directly)
/// but no HTTP dependency spans are produced and no <c>traceparent</c> is emitted.
/// </para>
///
/// <para>
/// This handler sidesteps the lifecycle issue by using a dedicated <see cref="ActivitySource"/>
/// that the MAUI service defaults explicitly <c>.AddSource(...)</c> to the <c>TracerProvider</c>.
/// Once an <see cref="Activity"/> is current, HttpClient's built-in <c>DiagnosticsHandler</c>
/// injects the W3C headers for us — we never touch <c>request.Headers</c> directly. That means
/// if the framework-level MAUI/OTel lifecycle gap is ever fixed (see follow-up issue), this
/// handler remains correct and non-duplicative; if it isn't fixed, this handler is what makes
/// correlation work at all.
/// </para>
/// </summary>
public sealed class ApiActivityHandler : DelegatingHandler
{
    /// <summary>
    /// Well-known <see cref="ActivitySource"/> name. Must be registered on the mobile
    /// <c>TracerProvider</c> (see <c>SentenceStudio.MauiServiceDefaults.Extensions</c>) or
    /// spans will be created but never exported.
    /// </summary>
    public const string ActivitySourceName = "SentenceStudio.Mobile.HttpClient";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private readonly ILogger<ApiActivityHandler>? _logger;

    public ApiActivityHandler(ILogger<ApiActivityHandler>? logger = null)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Skip activity creation if the request somehow has no URI — defensive only.
        if (request.RequestUri is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var method = request.Method.Method;
        var uri = request.RequestUri;

        // OTel HTTP semantic-convention-ish name: "HTTP {METHOD} {path}". Keep it short enough
        // to be readable in App Insights but informative for debugging.
        var activityName = $"HTTP {method} {uri.AbsolutePath}";

        using var activity = Source.StartActivity(activityName, ActivityKind.Client);

        // If no listener is attached (source not registered on the TracerProvider, or provider
        // not materialised), StartActivity returns null. In that case we still want the call
        // to succeed — we just skip the telemetry bookkeeping.
        if (activity is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Standard HTTP tags (OpenTelemetry semantic conventions).
        activity.SetTag("http.request.method", method);
        activity.SetTag("url.full", uri.ToString());
        activity.SetTag("url.scheme", uri.Scheme);
        activity.SetTag("server.address", uri.Host);
        if (!uri.IsDefaultPort)
        {
            activity.SetTag("server.port", uri.Port);
        }

        // Explicit W3C traceparent propagation. HttpClient's built-in DiagnosticsHandler
        // normally injects traceparent automatically, but ONLY when an OTel-style
        // ActivityListener is attached to "System.Net.Http". On MAUI the listener never
        // attaches because IHostedService doesn't run (see issue #171). Without this
        // block, the API sees every request as a root operation and the App Insights
        // correlation join against mobile dependencies returns zero rows — the exact
        // symptom PR #172 partially fixed (spans emit) but couldn't close out.
        //
        // We only inject if no traceparent already exists on the request so that a
        // deliberate upstream caller can override (and so repeated retries via the
        // standard resilience handler don't duplicate the header).
        if (!request.Headers.Contains("traceparent"))
        {
            DistributedContextPropagator.Current.Inject(
                activity,
                request.Headers,
                static (carrier, name, value) =>
                {
                    if (carrier is HttpRequestHeaders headers && !string.IsNullOrEmpty(value))
                    {
                        headers.TryAddWithoutValidation(name, value);
                    }
                });
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            activity.SetTag("http.response.status_code", statusCode);

            // 4xx is client-side failure, 5xx is server-side — both warrant Error status in OTel.
            if (statusCode >= 400)
            {
                activity.SetStatus(ActivityStatusCode.Error, $"HTTP {statusCode}");
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User/caller cancelled — not an application error. Record-but-don't-fail.
            activity.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // RecordException emits a standard OTel "exception" event with type/message/stacktrace,
            // which surfaces in App Insights' exception timeline. Keep status error for queryability.
            activity.AddException(ex);
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger?.LogDebug(ex, "API HttpClient call failed: {Method} {Uri}", method, uri);
            throw;
        }
    }
}
