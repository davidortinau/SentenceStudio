using Microsoft.AspNetCore.Components;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Where to send a learner immediately after an interactive sign-in — and, more importantly, when
/// <em>not</em> to send them anywhere.
/// </summary>
/// <remarks>
/// <para>
/// Signing in publishes an authentication state change, and the shell reacts to it: on the
/// authentication transition <c>MainLayout</c> re-runs <c>ApplyPostLoginRouteAsync</c>, which owns
/// the fresh-install <c>/onboarding</c> redirect and the initial-sync overlay. That notification is
/// raised <b>synchronously</b>, so by the time it returns the learner may already have been routed
/// somewhere better than the login page's own default of <c>"/"</c>.
/// </para>
/// <para>
/// The login page must therefore treat its own navigation as a fallback rather than a conclusion.
/// A fresh install that gets routed to <c>/onboarding</c> and is then bounced to the dashboard a
/// moment later has lost its onboarding, which is the exact regression this guard exists to stop.
/// </para>
/// </remarks>
public static class PostSignInNavigation
{
    /// <summary>The route the sign-in form lives on.</summary>
    public const string LoginPath = "/auth/login";

    /// <summary>
    /// True when nothing has navigated away from the login page yet, so the login page's own
    /// post-sign-in navigation is still the right thing to do.
    /// </summary>
    /// <param name="currentAbsolutePath">
    /// <see cref="System.Uri.AbsolutePath"/> of the current location — no query, no fragment.
    /// </param>
    public static bool ShouldRouteToReturnUrl(string? currentAbsolutePath)
    {
        if (string.IsNullOrEmpty(currentAbsolutePath))
        {
            // Nothing to compare against. Routing is the safer default: the failure mode is a
            // redundant navigation to the dashboard, whereas skipping would strand the learner on
            // the login form.
            return true;
        }

        var path = currentAbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            // Already at the application root, i.e. somebody routed us to the dashboard.
            return false;
        }

        return string.Equals(path, LoginPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The endpoint a sign-in must round-trip through so the server can write the auth cookie, or
    /// null when this sign-in needs no round trip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two hosts, two shapes of <c>AuthResult.AccessToken</c>, and the difference decides whether
    /// there is a cookie to write at all. The web host's <c>ServerAuthService</c> validates the
    /// password itself and hands back <c>userId|oneTimeToken</c>, because Blazor Server cannot set
    /// a cookie over the WebSocket the circuit already owns — the browser has to leave the page and
    /// come back through <c>/account-action/AutoSignIn</c>. The MAUI host's
    /// <c>IdentityAuthService</c> hands back a JWT it keeps itself; there is no cookie and nothing
    /// to round-trip.
    /// </para>
    /// <para>
    /// <b>Skipping this round trip is what leaves a web learner with circuit state and no durable
    /// session.</b> The dashboard renders, because the circuit holds an authenticated principal —
    /// and then the first full-document navigation arrives at the server with no cookie and is sent
    /// to the login page. Returning the URL from here rather than building it inline in the page
    /// means the decision is testable without driving a browser.
    /// </para>
    /// </remarks>
    /// <param name="accessToken">The <c>AuthResult.AccessToken</c> the sign-in returned.</param>
    /// <param name="returnUrl">Where the endpoint should send the browser once signed in.</param>
    public static string? AutoSignInUrl(string? accessToken, string returnUrl)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        var separator = accessToken.IndexOf('|');
        if (separator <= 0 || separator == accessToken.Length - 1)
        {
            // A JWT, or a malformed pair. Either way there is no user id and one-time token to
            // hand the endpoint, and inventing one would send the learner to an InvalidLink bounce.
            return null;
        }

        var userId = accessToken[..separator];
        var token = accessToken[(separator + 1)..];

        return $"/account-action/AutoSignIn?userId={Uri.EscapeDataString(userId)}"
             + $"&token={Uri.EscapeDataString(token)}"
             + $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    /// <summary>
    /// Navigates to <paramref name="returnUrl"/> unless something has already routed away from the
    /// login page.
    /// </summary>
    /// <returns><c>true</c> when this call performed the navigation.</returns>
    public static bool RouteAfterSignIn(NavigationManager navigation, string returnUrl, bool forceLoad)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        var currentPath = navigation.ToAbsoluteUri(navigation.Uri).AbsolutePath;
        if (!ShouldRouteToReturnUrl(currentPath))
        {
            return false;
        }

        navigation.NavigateTo(returnUrl, forceLoad);
        return true;
    }
}

/// <summary>
/// Whether a decision from <c>IPostLoginRouter</c> should actually move the learner.
/// </summary>
/// <remarks>
/// <para>
/// <c>MainLayout.ApplyPostLoginRouteAsync</c> runs from <c>OnInitializedAsync</c>, which happens on
/// every full page load — not only after signing in. The router's two answers do not deserve the
/// same treatment there:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>/onboarding</c> is a real redirect. A fresh install must reach it from wherever it started,
/// so it is applied unconditionally.
/// </description></item>
/// <item><description>
/// <c>/</c> means "there is nowhere special to send this learner". Treating that as a destination
/// on every initialisation sent a signed-in learner from <c>/skills</c> (or any other route) back
/// to the dashboard on reload, and during the render pass <c>NavigateTo</c> becomes an HTTP 302, so
/// deep links and bookmarks were unreachable too. It may only be applied while the learner is still
/// sitting on the login page.
/// </description></item>
/// </list>
/// </remarks>
public static class PostLoginRouteApplication
{
    /// <summary>The route a fresh install must always be sent to.</summary>
    public const string OnboardingPath = "/onboarding";

    /// <summary>
    /// True when <paramref name="routePath"/> should be navigated to from
    /// <paramref name="currentAbsolutePath"/>.
    /// </summary>
    /// <param name="routePath">The route decided by <c>IPostLoginRouter</c>, or null when deferred.</param>
    /// <param name="currentAbsolutePath">
    /// <see cref="System.Uri.AbsolutePath"/> of the current location — no query, no fragment.
    /// </param>
    public static bool ShouldNavigate(string? routePath, string? currentAbsolutePath)
    {
        if (string.IsNullOrEmpty(routePath))
        {
            // The decision was deferred (sync in flight). Nothing to apply yet.
            return false;
        }

        if (string.Equals(routePath, currentAbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            // Already there.
            return false;
        }

        if (string.Equals(routePath, OnboardingPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return PostSignInNavigation.ShouldRouteToReturnUrl(currentAbsolutePath);
    }
}
