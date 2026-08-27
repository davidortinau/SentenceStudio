namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Which Blazor host the UI is running under, decided from the navigation base URI.
/// </summary>
/// <remarks>
/// <para>
/// The distinction matters because a <b>full document reload</b>
/// (<c>NavigationManager.NavigateTo(url, forceLoad: true)</c>) means two completely different
/// things on the two hosts:
/// </para>
/// <list type="bullet">
/// <item>
/// On the <b>web</b> host it is load-bearing. Signing in has to round-trip through
/// <c>/account-action/AutoSignIn</c> so ASP.NET Core can write the auth cookie — a cookie cannot
/// be set over the existing WebSocket connection, so the navigation must actually leave the page.
/// </item>
/// <item>
/// On the <b>BlazorWebView</b> host (MAUI: macOS AppKit, Mac Catalyst, iOS, Android, Windows)
/// there is no cookie and no server round-trip, so a forced load buys nothing — and it costs a
/// great deal. <c>WebViewManager.AttachToPageAsync</c> disposes the current <c>PageContext</c>
/// (destroying the <c>WebViewRenderer</c>) and calls <c>_provider.CreateAsyncScope()</c> to build
/// a <b>brand-new DI scope</b> for the new document. Everything scoped is rebuilt, and any render
/// batch still travelling to the old renderer is dropped by the JS side with
/// <c>"There is no browser renderer with ID 3"</c>.
/// </item>
/// </list>
/// <para>
/// The rule lives here, once, because it was previously copy-pasted into four call sites and one
/// of them (the login page) had drifted to an unconditional <c>forceLoad: true</c>.
/// </para>
/// </remarks>
public static class BlazorHostKind
{
    /// <summary>The BlazorWebView host serves the app from this scheme.</summary>
    private const string WebViewScheme = "app://";

    /// <summary>Some MAUI platforms use a 0.0.0.0 loopback host for the WebView instead.</summary>
    private const string WebViewLoopbackHost = "0.0.0.0";

    /// <summary>
    /// True when the app is served over HTTP by the ASP.NET Core web app, rather than from inside
    /// a native BlazorWebView.
    /// </summary>
    /// <param name="baseUri">
    /// <see cref="Microsoft.AspNetCore.Components.NavigationManager.BaseUri"/>.
    /// </param>
    public static bool IsWebHost(string? baseUri)
    {
        if (string.IsNullOrWhiteSpace(baseUri))
        {
            // No base URI is not evidence of a web host, and guessing "web" here would force a
            // document reload on the host where that is harmful. Default to the safe answer.
            return false;
        }

        return !baseUri.StartsWith(WebViewScheme, StringComparison.OrdinalIgnoreCase)
            && !baseUri.Contains(WebViewLoopbackHost, StringComparison.Ordinal);
    }

    /// <summary>True when the app is running inside a native BlazorWebView.</summary>
    public static bool IsWebViewHost(string? baseUri) => !IsWebHost(baseUri);

    /// <summary>
    /// Whether a post-sign-in navigation should force a full document load.
    /// </summary>
    /// <remarks>
    /// Only the web host needs it, and only so the server can set the auth cookie. Forcing it on a
    /// BlazorWebView tears down the renderer and the DI scope for no benefit — see the type remarks.
    /// </remarks>
    public static bool ShouldForceLoadAfterSignIn(string? baseUri) => IsWebHost(baseUri);

    /// <summary>
    /// Whether an in-app completion flow — finishing onboarding, finishing registration, landing
    /// after a server auth round-trip — should force a full document load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These navigations go to ordinary in-app routes (<c>/</c>, <c>/onboarding</c>), so on a
    /// BlazorWebView the router can handle them client-side. Forcing a load there destroys the
    /// <c>WebViewRenderer</c> and the DI scope, which is the same teardown that dropped render
    /// batches with <c>"There is no browser renderer with ID 3"</c>.
    /// </para>
    /// <para>
    /// The web host keeps forcing the load: there the completion flows depend on a fresh request so
    /// the server can re-run authentication and prerendering, and changing that is not this
    /// change's business.
    /// </para>
    /// <para>
    /// This is deliberately a separate member from <see cref="ShouldForceLoadAfterSignIn"/> even
    /// though they agree today. They answer different questions — "does the server need to set a
    /// cookie" versus "does this route change need a new document" — and a future divergence should
    /// not have to untangle one shared call site.
    /// </para>
    /// </remarks>
    public static bool ShouldForceLoadForCompletionRoute(string? baseUri) => IsWebHost(baseUri);
}
