using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The account switch on the MAUI BlazorWebView, and the renderer lifetime it depends on.
/// </summary>
/// <remarks>
/// <para>
/// Signing in used to call <c>NavigationManager.NavigateTo(returnUrl, forceLoad: true)</c> on every
/// host. On the web that is load-bearing — the server has to set an auth cookie, which it cannot do
/// over the existing connection. Inside a BlazorWebView it is not: there is no cookie, no server
/// round-trip, and the forced load makes
/// <c>Microsoft.AspNetCore.Components.WebView.WebViewManager.AttachToPageAsync</c> dispose the
/// current <c>PageContext</c> — destroying the <c>WebViewRenderer</c> — and build a brand-new DI
/// scope for the new document:
/// </para>
/// <code>
/// internal async Task AttachToPageAsync(string baseUrl, string startUrl)
/// {
///     if (_currentPageContext != null)
///         await _currentPageContext.DisposeAsync();
///     AsyncServiceScope serviceScope = _provider.CreateAsyncScope();
///     _currentPageContext = new PageContext(_dispatcher, serviceScope, ...);
///     ...
/// }
/// </code>
/// <para>
/// Observed consequence on <c>net11.0-macos</c>: after an account switch the .NET side logged
/// "Rendering component N of type SamFab" while the DOM had no <c>#sam-fab</c>, and the render
/// batch was rejected with "There is no browser renderer with ID 3" (dotnet/maui#28339). Only a
/// cold restart recovered it.
/// </para>
/// </remarks>
public class BlazorHostKindTests
{
    [Theory]
    [InlineData("app://0.0.0.1/")]
    [InlineData("app://0.0.0.0/")]
    [InlineData("APP://0.0.0.1/")]
    [InlineData("http://0.0.0.0:5000/")]
    [InlineData("https://0.0.0.0/")]
    public void WebViewBaseUris_AreNotWebHosts(string baseUri)
    {
        BlazorHostKind.IsWebHost(baseUri).Should().BeFalse();
        BlazorHostKind.IsWebViewHost(baseUri).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://localhost:5001/")]
    [InlineData("http://localhost:5081/")]
    [InlineData("https://sentencestudio.example.com/")]
    public void HttpBaseUris_AreWebHosts(string baseUri)
    {
        BlazorHostKind.IsWebHost(baseUri).Should().BeTrue();
        BlazorHostKind.IsWebViewHost(baseUri).Should().BeFalse();
    }

    /// <summary>
    /// An unknown base URI must not be treated as the web host. Guessing "web" forces a document
    /// load, and a document load is exactly what tears the renderer down.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownBaseUri_DefaultsToTheHostThatDoesNotForceALoad(string? baseUri)
    {
        BlazorHostKind.IsWebHost(baseUri).Should().BeFalse();
        BlazorHostKind.ShouldForceLoadAfterSignIn(baseUri).Should().BeFalse();
    }

    [Fact]
    public void SignIn_ForcesADocumentLoad_OnlyOnTheWebHost()
    {
        BlazorHostKind.ShouldForceLoadAfterSignIn("https://localhost:5001/").Should().BeTrue(
            "the web host must leave the page so the server can set the auth cookie");

        BlazorHostKind.ShouldForceLoadAfterSignIn("app://0.0.0.1/").Should().BeFalse(
            "a forced load inside a BlazorWebView destroys the renderer and the DI scope for nothing");
    }
}

/// <summary>
/// Source contract: the login page must not force a document load on the BlazorWebView.
/// </summary>
/// <remarks>
/// This is asserted against the source because the regression is a single boolean argument that
/// compiles either way, renders identically on the web, and only misbehaves on a native head under
/// a timing window. A behavioural test would need a real WebViewManager and a real WKWebView.
/// </remarks>
public class LoginNavigationSourceContractTests
{
    private static string ReadSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the repository root should be locatable from the test output directory");

        var path = Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"expected source at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void LoginPage_DoesNotUnconditionallyForceLoadAfterSignIn()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/LoginPage.razor");

        // The AutoSignIn branch legitimately keeps forceLoad: true — it exists to leave the page.
        // Everything else must consult the host.
        source.Should().Contain(
            "BlazorHostKind.ShouldForceLoadAfterSignIn(NavManager.BaseUri)",
            "the post-sign-in navigation must be conditional on the host");

        var forcedLoads = Regex.Matches(source, @"forceLoad:\s*true").Count;
        forcedLoads.Should().Be(
            1,
            "only the AutoSignIn cookie hand-off may force a document load");
    }

    /// <summary>
    /// The rule that decides web-vs-webview was copy-pasted into four call sites and one of them
    /// had already drifted. It lives in one place now, and must stay there.
    /// </summary>
    [Theory]
    [InlineData("src/SentenceStudio.UI/Pages/LoginPage.razor")]
    [InlineData("src/SentenceStudio.UI/Layout/NavMenu.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Onboarding.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Profile.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Feedback.razor")]
    public void HostDetection_IsNotReimplementedInline(string relativePath)
    {
        var source = ReadSource(relativePath);

        source.Should().NotContain(
            "StartsWith(\"app://\")",
            "host detection belongs in BlazorHostKind, not inline");
    }
}
