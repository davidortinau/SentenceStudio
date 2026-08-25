using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests;

/// <summary>
/// The coach overlay is query-backed (<c>?coach=</c>) and the mobile route carries
/// <c>?pane=</c>. If either survives into a remembered section route, a later sidebar tap on
/// Dashboard silently re-opens the coach workspace. These tests pin that behavior.
/// </summary>
public class NavigationMemoryQueryStrippingTests
{
    [Fact]
    public void StripExcludedQuery_RemovesCoachSessionParameter()
    {
        NavigationMemoryService.StripExcludedQuery("/?coach=abc123")
            .Should().Be("/");
    }

    [Fact]
    public void StripExcludedQuery_RemovesPaneParameter()
    {
        NavigationMemoryService.StripExcludedQuery("/coach?pane=plan")
            .Should().Be("/coach");
    }

    [Fact]
    public void StripExcludedQuery_RemovesBothCoachParameters()
    {
        NavigationMemoryService.StripExcludedQuery("/coach?coach=abc123&pane=plan")
            .Should().Be("/coach");
    }

    [Fact]
    public void StripExcludedQuery_PreservesUnrelatedParameters()
    {
        NavigationMemoryService.StripExcludedQuery("/reading?resourceId=42&coach=abc&skillId=7")
            .Should().Be("/reading?resourceId=42&skillId=7");
    }

    [Fact]
    public void StripExcludedQuery_LeavesPathWithoutQueryUnchanged()
    {
        NavigationMemoryService.StripExcludedQuery("/vocabulary")
            .Should().Be("/vocabulary");
    }

    [Fact]
    public void StripExcludedQuery_PreservesFragment()
    {
        NavigationMemoryService.StripExcludedQuery("/activity-log?coach=abc#today")
            .Should().Be("/activity-log#today");
    }

    [Fact]
    public void StripExcludedQuery_IsCaseInsensitiveOnKeys()
    {
        NavigationMemoryService.StripExcludedQuery("/?Coach=abc&PANE=plan")
            .Should().Be("/");
    }

    [Fact]
    public void StripExcludedQuery_DoesNotStripParametersThatMerelyStartWithCoach()
    {
        NavigationMemoryService.StripExcludedQuery("/?coachId=abc")
            .Should().Be("/?coachId=abc");
    }
}
