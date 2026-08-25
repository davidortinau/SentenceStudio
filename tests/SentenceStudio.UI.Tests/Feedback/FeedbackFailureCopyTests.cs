using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UI.Tests.Feedback;

/// <summary>
/// What the feedback page tells a learner whose submission was refused.
/// </summary>
/// <remarks>
/// <para>
/// The two 409 outcomes share a status and have opposite honest messages, so "the page shows the
/// right one" is a correctness property rather than a wording preference. Telling somebody to check
/// GitHub after a proved failure sends them looking for an issue that does not exist; telling
/// somebody nothing was filed after an unknown outcome invites them to write it again, which is how
/// a duplicate reaches a public repository.
/// </para>
/// <para>
/// Source- and resource-level, because driving the component would need an authenticated render
/// host, a fake API client, and a toast double — machinery whose failure modes would be its own.
/// What has to be true is narrow and is exactly what is asserted: each outcome has its own branch,
/// each branch names its own string, and the strings say what they should.
/// </para>
/// </remarks>
public sealed class FeedbackFailureCopyTests
{
    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();
        return root!.FullName;
    }

    private static string PageSource() =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.UI", "Pages", "Feedback.razor"));

    private static string ResourceValue(string culture, string key)
    {
        var name = culture.Length == 0 ? "AppResources.resx" : $"AppResources.{culture}.resx";
        var path = Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Shared", "Resources", "Strings", name);

        var value = XDocument.Load(path)
            .Root!.Elements("data")
            .FirstOrDefault(d => (string?)d.Attribute("name") == key)
            ?.Element("value")?.Value;

        value.Should().NotBeNullOrWhiteSpace($"{key} must exist in {name}");
        return value!;
    }

    /// <summary>
    /// The proved-failure message says the report was not filed, and does not send anybody to
    /// GitHub to look.
    /// </summary>
    [Fact]
    public void The_closed_message_says_nothing_was_filed_and_does_not_send_the_learner_to_github()
    {
        var closed = ResourceValue(string.Empty, "Feedback_SubmitClosed");

        closed.Should().Contain("not filed");
        closed.Should().NotContain("check GitHub");
        closed.Should().Contain("again", "the learner has to know a new preview is required");
    }

    /// <summary>The unknown-outcome message keeps its check-before-retrying advice.</summary>
    [Fact]
    public void The_in_doubt_message_still_tells_the_learner_to_check_before_rewriting()
    {
        var inDoubt = ResourceValue(string.Empty, "Feedback_SubmitInDoubt");

        inDoubt.Should().Contain("GitHub");
        inDoubt.Should().NotContain("not filed", "the whole point is that we do not know");
    }

    /// <summary>The two messages are not interchangeable.</summary>
    [Fact]
    public void The_closed_and_in_doubt_messages_are_different()
    {
        ResourceValue(string.Empty, "Feedback_SubmitClosed")
            .Should().NotBe(ResourceValue(string.Empty, "Feedback_SubmitInDoubt"));
    }

    /// <summary>Both messages are translated, so a Korean learner gets the same distinction.</summary>
    [Theory]
    [InlineData("Feedback_SubmitClosed")]
    [InlineData("Feedback_SubmitInDoubt")]
    [InlineData("Feedback_TokenRejected")]
    [InlineData("Feedback_RateLimited")]
    [InlineData("Feedback_RateLimitedNoWait")]
    [InlineData("Feedback_AlreadyFiled")]
    public void Every_feedback_failure_string_is_translated(string key)
    {
        ResourceValue(string.Empty, key).Should().NotBeNullOrWhiteSpace();
        ResourceValue("ko", key).Should().NotBeNullOrWhiteSpace();
        ResourceValue("ko", key).Should().NotBe(
            ResourceValue(string.Empty, key), "an untranslated string is an English one in disguise");
    }

    /// <summary>
    /// The page branches on the closed outcome separately, and that branch does not reach for the
    /// in-doubt string.
    /// </summary>
    [Fact]
    public void The_closed_branch_uses_the_closed_string_and_not_the_in_doubt_one()
    {
        var source = PageSource();

        var start = source.IndexOf("case FeedbackApiFailure.Closed:", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the closed outcome needs its own branch");

        var length = source.IndexOf("break;", start, StringComparison.Ordinal) - start;
        var branch = source.Substring(start, length);

        branch.Should().Contain("Feedback_SubmitClosed");
        branch.Should().NotContain(
            "Feedback_SubmitInDoubt",
            "a proved failure must never render the check-GitHub message");
    }

    /// <summary>The in-doubt branch is still its own, and still uses its own string.</summary>
    [Fact]
    public void The_in_doubt_branch_is_separate_and_uses_the_in_doubt_string()
    {
        var source = PageSource();

        var start = source.IndexOf("case FeedbackApiFailure.InDoubt:", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);

        var length = source.IndexOf("break;", start, StringComparison.Ordinal) - start;
        var branch = source.Substring(start, length);

        branch.Should().Contain("Feedback_SubmitInDoubt");
        branch.Should().NotContain("Feedback_SubmitClosed");
    }

    /// <summary>
    /// Both terminal outcomes take the Submit button away.
    /// </summary>
    /// <remarks>
    /// Closed and in-doubt differ in what they say and agree on what they do: the preview token is
    /// spent either way, so offering to send it again is offering an action that cannot work — and,
    /// for the in-doubt case, one that might file a duplicate.
    /// </remarks>
    [Theory]
    [InlineData("case FeedbackApiFailure.Closed:")]
    [InlineData("case FeedbackApiFailure.InDoubt:")]
    [InlineData("case FeedbackApiFailure.TokenRejected:")]
    public void Every_terminal_outcome_spends_the_submission(string caseLabel)
    {
        var source = PageSource();

        var start = source.IndexOf(caseLabel, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);

        var length = source.IndexOf("break;", start, StringComparison.Ordinal) - start;
        source.Substring(start, length).Should().Contain("submissionSpent = true");
    }

    /// <summary>
    /// A rate-limited outcome does not spend the submission.
    /// </summary>
    /// <remarks>
    /// The counter-case, and the reason the assertion above is not just "every branch sets the
    /// flag". A limit is a wait, not a verdict: the token is still redeemable when the wait is over,
    /// and disabling Submit would force a fresh preview that spends the preview allowance too.
    /// </remarks>
    [Fact]
    public void A_rate_limited_outcome_does_not_spend_the_submission()
    {
        var source = PageSource();

        var start = source.IndexOf("case FeedbackApiFailure.RateLimited:", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);

        var length = source.IndexOf("break;", start, StringComparison.Ordinal) - start;
        source.Substring(start, length).Should().NotContain("submissionSpent = true");
    }

    /// <summary>
    /// Every declared failure outcome the page can receive has a branch of its own.
    /// </summary>
    /// <remarks>
    /// A mutation guard. The switch has a default arm, so a member added later would silently land
    /// in the generic "something went wrong" message — which for a terminal outcome means the page
    /// keeps offering a Submit button that cannot work, or worse, one that can duplicate.
    /// </remarks>
    [Fact]
    public void Every_actionable_failure_outcome_has_its_own_branch()
    {
        var source = PageSource();

        foreach (var failure in Enum.GetValues<FeedbackApiFailure>())
        {
            if (failure is FeedbackApiFailure.None or FeedbackApiFailure.Unavailable)
            {
                // None never reaches the handler, and Unavailable is what the default arm is for.
                continue;
            }

            Regex.IsMatch(source, $@"case\s+FeedbackApiFailure\.{failure}\s*:")
                .Should().BeTrue($"{failure} needs an explicit branch rather than the default arm");
        }
    }
}
