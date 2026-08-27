using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services.Theme;

namespace SentenceStudio.WebApp.Platform.Theme;

/// <summary>
/// Browser-scoped appearance storage for the web host, over the <c>ss_appearance</c> cookie.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered scoped.</b> That is the isolation boundary: a Blazor circuit is a DI scope, so one
/// circuit gets one store instance holding one browser's value, and an SSR render gets its own tied
/// to that request. A singleton here would put every learner's appearance in one field again, which
/// is the bug this replaces.
/// </para>
/// <para>
/// <b>Two read paths, because the host has two contexts.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>Server-side render.</b> <c>HttpContext</c> is present and the cookie is on the request, so
/// <see cref="TryLoad"/> answers synchronously — early enough for <c>App.razor</c> to put the right
/// <c>data-bs-theme</c> and <c>data-ss-theme</c> on the <c>&lt;html&gt;</c> element and avoid a
/// flash of the default theme.
/// </item>
/// <item>
/// <b>Interactive circuit.</b> <c>HttpContext</c> is <see langword="null"/> — the request that
/// opened the circuit is long finished and the transport is a WebSocket. <see cref="LoadAsync"/>
/// asks the browser over JS interop instead, and caches the answer for the life of the scope.
/// </item>
/// </list>
/// <para>
/// Writes mirror the same split: a <c>Set-Cookie</c> header while a response is still open, JS
/// interop once it is not.
/// </para>
/// </remarks>
public sealed class BrowserAppearanceCookieStore : IThemePreferenceStore
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAppearanceCookieChannel _channel;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<BrowserAppearanceCookieStore> _logger;

    /// <summary>
    /// The value this scope believes the browser holds. Populated from whichever read path
    /// succeeded, and updated on write so a later read in the same circuit does not have to make
    /// another interop round trip.
    /// </summary>
    private AppearanceSelection? _cached;

    public BrowserAppearanceCookieStore(
        IHttpContextAccessor httpContextAccessor,
        IAppearanceCookieChannel channel,
        IHostEnvironment environment,
        ILogger<BrowserAppearanceCookieStore> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _channel = channel;
        _environment = environment;
        _logger = logger;
    }

    public bool TryLoad(out AppearanceSelection selection)
    {
        if (_cached is not null)
        {
            selection = _cached;
            return true;
        }

        var http = _httpContextAccessor.HttpContext;
        if (http is null)
        {
            // Circuit context: the cookie is only reachable through the browser. Not a failure —
            // LoadAsync finishes the job on first render.
            selection = null!;
            return false;
        }

        if (!http.Request.Cookies.TryGetValue(AppearanceCookie.Name, out var token))
        {
            selection = null!;
            return false;
        }

        if (!AppearanceSelection.TryParse(token, out selection))
        {
            // Hand-edited, truncated, or written by a build with a different token layout. Rejected
            // rather than coerced, and logged without the value so a malformed cookie cannot inject
            // arbitrary text into the log.
            _logger.LogWarning(
                "Discarding an invalid {Cookie} cookie ({Length} chars); using the default appearance.",
                AppearanceCookie.Name,
                token?.Length ?? 0);
            return false;
        }

        _cached = selection;
        return true;
    }

    public async ValueTask<AppearanceSelection?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (TryLoad(out var fromRequest))
        {
            return fromRequest;
        }

        // Only the circuit path gets here. If an HttpContext existed, the synchronous read was
        // authoritative — a miss there means the browser genuinely has no cookie, and asking it
        // again over interop would just be a slower way to learn the same thing.
        if (_httpContextAccessor.HttpContext is not null)
        {
            return null;
        }

        var token = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (!AppearanceSelection.TryParse(token, out var selection))
        {
            _logger.LogWarning(
                "Discarding an invalid {Cookie} cookie read from the browser ({Length} chars); using the default appearance.",
                AppearanceCookie.Name,
                token.Length);
            return null;
        }

        _cached = selection;
        return selection;
    }

    public async ValueTask SaveAsync(
        AppearanceSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        _cached = selection;
        var token = selection.ToToken();

        var http = _httpContextAccessor.HttpContext;
        if (http is not null && !http.Response.HasStarted)
        {
            http.Response.Cookies.Append(
                AppearanceCookie.Name,
                token,
                AppearanceCookie.BuildOptions(http, _environment));
            return;
        }

        // Circuit, or a response already on the wire: the browser writes it.
        await _channel
            .WriteAsync(token, (int)AppearanceCookie.Lifetime.TotalDays, cancellationToken)
            .ConfigureAwait(false);
    }
}
