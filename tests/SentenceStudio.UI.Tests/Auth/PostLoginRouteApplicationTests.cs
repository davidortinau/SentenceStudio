using FluentAssertions;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Auth;

/// <summary>
/// Which <c>IPostLoginRouter</c> decisions are allowed to move the learner, and from where.
/// </summary>
/// <remarks>
/// <para>
/// <c>MainLayout.ApplyPostLoginRouteAsync</c> is called from two places: the authentication
/// false → true transition, and <c>OnInitializedAsync</c>. The second one runs on <em>every</em>
/// full page load, so whatever this rule allows happens on every reload and every deep link.
/// </para>
/// <para>
/// The regression these tests exist for: the dashboard decision used to be applied whenever the
/// current path merely differed from it. A signed-in learner who reloaded <c>/skills</c>, or opened
/// a bookmark, was sent to the dashboard instead — and because the redirect happened during the
/// render pass, Blazor turned it into an HTTP 302, so the requested page never rendered at all.
/// Onboarding must still win from anywhere, which is what separates the two cases.
/// </para>
/// </remarks>
public class PostLoginRouteApplicationTests
{
    // ------------------------------------------------------------------ the regression

    [Theory]
    [InlineData("/skills")]
    [InlineData("/vocabulary")]
    [InlineData("/resources")]
    [InlineData("/settings")]
    [InlineData("/profile")]
    [InlineData("/reading")]
    [InlineData("/coach")]
    public void The_dashboard_decision_does_not_pull_a_learner_off_the_page_they_are_on(string currentPath)
    {
        PostLoginRouteApplication.ShouldNavigate("/", currentPath).Should().BeFalse(
            "reloading {0} or opening it as a link must render that page, not bounce to the dashboard",
            currentPath);
    }

    [Fact]
    public void The_dashboard_decision_still_applies_from_the_login_page()
    {
        // The real post-login case: the learner is on the login form and has nowhere better to go.
        PostLoginRouteApplication.ShouldNavigate("/", "/auth/login").Should().BeTrue();
    }

    [Fact]
    public void The_dashboard_decision_applies_when_the_current_path_is_unknown()
    {
        // Nothing to compare against: routing is the safer default, because the failure mode is a
        // redundant navigation rather than a learner stranded on the login form.
        PostLoginRouteApplication.ShouldNavigate("/", null).Should().BeTrue();
        PostLoginRouteApplication.ShouldNavigate("/", string.Empty).Should().BeTrue();
    }

    // ------------------------------------------------------------------ onboarding wins

    [Theory]
    [InlineData("/")]
    [InlineData("/skills")]
    [InlineData("/auth/login")]
    [InlineData("/settings")]
    [InlineData(null)]
    public void A_fresh_install_reaches_onboarding_from_anywhere(string? currentPath)
    {
        PostLoginRouteApplication.ShouldNavigate("/onboarding", currentPath).Should().BeTrue(
            "a fresh install that never reaches onboarding has lost it for good");
    }

    [Fact]
    public void Onboarding_does_not_navigate_to_itself()
    {
        PostLoginRouteApplication.ShouldNavigate("/onboarding", "/onboarding").Should().BeFalse();
    }

    [Fact]
    public void Onboarding_matching_is_case_insensitive()
    {
        PostLoginRouteApplication.ShouldNavigate("/Onboarding", "/skills").Should().BeTrue();
        PostLoginRouteApplication.ShouldNavigate("/onboarding", "/Onboarding").Should().BeFalse();
    }

    // ------------------------------------------------------------------ deferred / no-op

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_deferred_decision_never_navigates(string? routePath)
    {
        // DeferUntilSyncCompletes carries a null path: the sync overlay stays up and the decision
        // is re-evaluated later. Navigating here would race the overlay.
        PostLoginRouteApplication.ShouldNavigate(routePath, "/skills").Should().BeFalse();
        PostLoginRouteApplication.ShouldNavigate(routePath, "/auth/login").Should().BeFalse();
    }

    [Fact]
    public void Already_on_the_decided_route_is_never_a_navigation()
    {
        PostLoginRouteApplication.ShouldNavigate("/", "/").Should().BeFalse();
        PostLoginRouteApplication.ShouldNavigate("/", "/ ".Trim()).Should().BeFalse();
    }

    [Fact]
    public void A_trailing_slash_on_the_root_is_still_the_root()
    {
        // ShouldRouteToReturnUrl trims the trailing slash before comparing, so "/" never counts as
        // "still on the login page".
        PostLoginRouteApplication.ShouldNavigate("/", "/").Should().BeFalse();
    }

    // ------------------------------------------------------------------ the contract in one place

    [Fact]
    public void Onboarding_path_constant_matches_the_router_contract()
    {
        // PostLoginRouter returns this literal; if it ever changes, both sides must move together.
        PostLoginRouteApplication.OnboardingPath.Should().Be("/onboarding");
    }
}
