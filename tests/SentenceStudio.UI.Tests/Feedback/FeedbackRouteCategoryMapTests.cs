using FluentAssertions;
using SentenceStudio.Contracts.Feedback;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Feedback;

/// <summary>
/// The client's route-to-category reduction.
/// </summary>
/// <remarks>
/// <para>
/// The mapping itself is unremarkable. What these tests are really pinning is the property that
/// the mapper has no escape hatch: there is no input for which any part of the route is echoed
/// into the result. That is why the parameterised routes — the ones carrying entity ids, dates,
/// and tokens — are the majority of the cases here.
/// </para>
/// <para>
/// The server re-normalises whatever arrives and clamps undeclared ordinals, so nothing below is
/// load-bearing for security on its own. It is what makes the honest client send something useful.
/// </para>
/// </remarks>
public sealed class FeedbackRouteCategoryMapTests
{
    [Theory]
    [InlineData("", FeedbackRouteCategory.Home)]
    [InlineData("/", FeedbackRouteCategory.Home)]
    [InlineData(null, FeedbackRouteCategory.Home)]
    [InlineData("feedback", FeedbackRouteCategory.Feedback)]
    [InlineData("coach", FeedbackRouteCategory.Coach)]
    [InlineData("profile", FeedbackRouteCategory.Profile)]
    [InlineData("settings", FeedbackRouteCategory.Profile)]
    [InlineData("skills", FeedbackRouteCategory.Skills)]
    [InlineData("resources", FeedbackRouteCategory.Resources)]
    [InlineData("vocabulary", FeedbackRouteCategory.Resources)]
    [InlineData("import", FeedbackRouteCategory.Resources)]
    [InlineData("media-import", FeedbackRouteCategory.Resources)]
    [InlineData("activity-log", FeedbackRouteCategory.Progress)]
    [InlineData("diary", FeedbackRouteCategory.Progress)]
    [InlineData("auth", FeedbackRouteCategory.Account)]
    [InlineData("onboarding", FeedbackRouteCategory.Account)]
    [InlineData("reading", FeedbackRouteCategory.Activity)]
    [InlineData("vocab-quiz", FeedbackRouteCategory.Activity)]
    [InlineData("shadowing", FeedbackRouteCategory.Activity)]
    [InlineData("numberdrill", FeedbackRouteCategory.Activity)]
    [InlineData("minimal-pairs", FeedbackRouteCategory.Activity)]
    public void Known_routes_map_to_their_area(string? route, FeedbackRouteCategory expected)
    {
        FeedbackRouteCategoryMap.Categorize(route).Should().Be(expected);
    }

    /// <summary>
    /// Only the first segment is read, so nothing downstream of it can influence the result.
    /// </summary>
    /// <remarks>
    /// Everything after the first segment is where identifiers live — <c>/resources/edit/4821</c>,
    /// <c>/diary/2026-08-21</c>, <c>/minimal-pairs/session/{id}</c>. Reading it would be the first
    /// step towards putting it somewhere.
    /// </remarks>
    [Theory]
    [InlineData("/resources/edit/4821", FeedbackRouteCategory.Resources)]
    [InlineData("/vocabulary/edit/99", FeedbackRouteCategory.Resources)]
    [InlineData("/skills/edit/12", FeedbackRouteCategory.Skills)]
    [InlineData("/diary/2026-08-21", FeedbackRouteCategory.Progress)]
    [InlineData("/minimal-pairs/session/abc-123", FeedbackRouteCategory.Activity)]
    [InlineData("/import/channel/UCxyz", FeedbackRouteCategory.Resources)]
    [InlineData("/auth/reset-password", FeedbackRouteCategory.Account)]
    public void A_parameterised_route_maps_to_its_area_and_nothing_more(
        string route, FeedbackRouteCategory expected)
    {
        FeedbackRouteCategoryMap.Categorize(route).Should().Be(expected);
    }

    /// <summary>Query strings and fragments are dropped before anything is examined.</summary>
    /// <remarks>
    /// The query string is the highest-risk part of a route: reset tokens, email addresses, and
    /// search terms all live there, and all of them were being published verbatim before this.
    /// </remarks>
    [Theory]
    [InlineData("/auth/login?returnUrl=%2Fresources%2F4821&email=learner%40example.com")]
    [InlineData("/auth?token=abc123def456")]
    [InlineData("/resources?search=my%20private%20note")]
    [InlineData("/profile#section-email")]
    public void Query_strings_and_fragments_never_survive(string route)
    {
        var category = FeedbackRouteCategoryMap.Categorize(route);

        Enum.IsDefined(category).Should().BeTrue();
        category.ToString().Should().NotContainAny("token", "email", "search", "4821", "abc123");
    }

    /// <summary>An unrecognised route becomes Unknown, never text.</summary>
    /// <remarks>
    /// The case that matters most for the future. A page added in two years is unknown to this
    /// mapper, and "unknown" has to be a small triage loss rather than a pass-through — otherwise
    /// the next feature leaks its parameters by default and nobody has to make a mistake for it to
    /// happen.
    /// </remarks>
    [Theory]
    [InlineData("/some-future-page")]
    [InlineData("/admin/secrets/42")]
    [InlineData("/..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/<script>alert(1)</script>")]
    [InlineData("/learner@example.com")]
    public void An_unrecognised_route_becomes_unknown(string route)
    {
        FeedbackRouteCategoryMap.Categorize(route).Should().Be(FeedbackRouteCategory.Unknown);
    }

    /// <summary>
    /// No route, however hostile, produces anything but a declared enum member.
    /// </summary>
    /// <remarks>
    /// The structural claim the whole design rests on, asserted over a wide sample rather than
    /// argued: the mapper's return type has a finite set of legal values and it never manufactures
    /// one outside it.
    /// </remarks>
    [Fact]
    public void Every_conceivable_route_maps_to_a_declared_member()
    {
        var routes = new[]
        {
            "", "/", "///", "   ", "?only=query", "#only-fragment",
            "/resources/edit/4821?token=x#y", new string('a', 4000),
            "/RESOURCES/EDIT/1", "/Resources", "/résources", "/资源",
            "/%00", "/a/b/c/d/e/f/g", "/feedback/../admin"
        };

        foreach (var route in routes)
        {
            var category = FeedbackRouteCategoryMap.Categorize(route);
            Enum.IsDefined(category).Should().BeTrue($"'{route}' must not produce an undeclared value");
        }
    }

    /// <summary>Matching is case-insensitive, so a differently-cased link is still categorised.</summary>
    [Theory]
    [InlineData("/RESOURCES", FeedbackRouteCategory.Resources)]
    [InlineData("/Coach", FeedbackRouteCategory.Coach)]
    [InlineData("/Vocab-Quiz", FeedbackRouteCategory.Activity)]
    public void Matching_ignores_case(string route, FeedbackRouteCategory expected)
    {
        FeedbackRouteCategoryMap.Categorize(route).Should().Be(expected);
    }
}
