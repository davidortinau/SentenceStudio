using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.WebApp.Platform.Theme;

/// <summary>
/// The per-browser appearance cookie: its name, its options, and the rules that make it safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cookie and not something else.</b> The requirement is a value that is readable
/// <i>synchronously, during server-side rendering, before the <c>&lt;html&gt;</c> element is
/// written</i> — otherwise the first paint uses the default theme and then snaps to the learner's
/// choice. Only a cookie satisfies that: it arrives on the request, so
/// <c>HttpContext.Request.Cookies</c> answers without awaiting anything. <c>localStorage</c> and
/// <c>sessionStorage</c> are unreachable during SSR and would guarantee the flash. Server-side
/// session state would be keyed by identity, which is the wrong scope — the product decision is
/// per-browser, so a learner's phone and desktop stay independent, and two people sharing an
/// account do not fight over one setting.
/// </para>
/// <para>
/// <b>Why this is not a security surface.</b> The cookie carries a theme id, a mode token and an
/// integer percentage. No identity, no user id, no token, no secret — nothing that grants anything.
/// The worst a forged value can do is show its own author an unusual colour scheme, and even that
/// is bounded: it is parsed by <see cref="AppearanceSelection.TryParse"/>, which enforces a length
/// cap, a fixed shape, membership of the closed theme catalogue, membership of the closed mode set,
/// and a numeric range. Anything else is discarded and the default is used.
/// </para>
/// <para>
/// <b>Why it is not <c>HttpOnly</c>.</b> Once an interactive circuit is running there is no HTTP
/// response left to attach a <c>Set-Cookie</c> header to — the connection is a WebSocket. The
/// browser side therefore has to write it, which requires script access. That is an acceptable
/// trade only because of the paragraph above: this cookie is not a credential. It is explicitly
/// separate from the auth cookie, which stays <c>HttpOnly</c>.
/// </para>
/// <para>
/// <c>SameSite=Lax</c> keeps it off cross-site requests, and <c>Secure</c> follows the request
/// scheme so local HTTP development still works while deployed HTTPS gets the flag.
/// </para>
/// </remarks>
public static class AppearanceCookie
{
    /// <summary>Cookie name. Prefixed <c>ss_</c> to sit alongside the app's other non-auth cookies.</summary>
    public const string Name = "ss_appearance";

    /// <summary>How long a browser keeps the choice. Long, because it is a preference, not a session.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>
    /// Reads and validates the cookie off a request. Returns <see langword="false"/> when it is
    /// absent or fails validation — the caller falls back explicitly rather than receiving a
    /// coerced value.
    /// </summary>
    public static bool TryRead(HttpRequest request, out AppearanceSelection selection)
    {
        selection = null!;
        if (!request.Cookies.TryGetValue(Name, out var token))
        {
            return false;
        }

        return AppearanceSelection.TryParse(token, out selection);
    }

    /// <summary>
    /// Cookie options for a write from the server side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>Secure</c> is decided by the host environment, not by the scheme the app happens to
    /// observe.</b> In production this app runs behind Azure Container Apps' ingress, which
    /// terminates TLS and forwards over plain HTTP. <c>Request.IsHttps</c> is therefore only true
    /// there if forwarded-header processing is configured and working — and a cookie policy that
    /// silently degrades when a header goes missing is the wrong shape for a security attribute.
    /// So: outside Development the flag is unconditional, and only Development consults the scheme,
    /// so that <c>http://localhost</c> development still round-trips the cookie.
    /// </para>
    /// <para>
    /// The browser-side writer in <c>app.js</c> reaches the same answer from the other direction —
    /// it reads <c>window.location.protocol</c>, which is the scheme the browser actually used and
    /// cannot be lost to a missing proxy header.
    /// </para>
    /// </remarks>
    public static CookieOptions BuildOptions(HttpContext context, IHostEnvironment environment) => new()
    {
        // Script-writable on purpose: the circuit has no response to set a header on. Safe because
        // the value is a non-secret, closed-set presentation preference.
        HttpOnly = false,
        Secure = !environment.IsDevelopment() || context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        Expires = DateTimeOffset.UtcNow.Add(Lifetime)
    };
}
