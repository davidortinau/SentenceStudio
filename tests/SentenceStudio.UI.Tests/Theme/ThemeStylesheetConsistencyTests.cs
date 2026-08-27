using System.Text.RegularExpressions;
using FluentAssertions;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The server and the browser must agree about which stylesheet a theme needs.
/// </summary>
/// <remarks>
/// <para>
/// Two independent places decide this. <c>App.razor</c> writes the <c>&lt;link&gt;</c> during the
/// server-side render, from <see cref="ThemeDescriptor.StylesheetSwap"/>. <c>app.js</c> rewrites
/// the same element once the circuit is running, from a <c>BOOTSWATCH_THEMES</c> array. If the two
/// disagree about a theme, the learner sees the page restyle itself a moment after it loads —
/// which is precisely the flash the SSR path exists to prevent, reintroduced from the other side.
/// </para>
/// <para>
/// The C# side is now derived from the catalogue, so it cannot drift on its own. The JavaScript
/// array cannot reference the catalogue, so it is pinned here instead.
/// </para>
/// </remarks>
public class ThemeStylesheetConsistencyTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.UI")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return directory!.FullName;
    }

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(segments).ToArray()));

    [Fact]
    public void The_javascript_bootswatch_list_matches_the_catalogue()
    {
        var appJs = ReadSource("src", "SentenceStudio.UI", "wwwroot", "js", "app.js");

        var match = Regex.Match(appJs, @"BOOTSWATCH_THEMES\s*=\s*\[(?<items>[^\]]*)\]");
        match.Success.Should().BeTrue("app.js must still declare BOOTSWATCH_THEMES");

        var fromJs = Regex.Matches(match.Groups["items"].Value, @"'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .OrderBy(x => x, StringComparer.Ordinal);

        var fromCatalog = ThemeCatalog.All
            .Where(t => t.IsStylesheetSwap)
            .Select(t => t.StylesheetSwap!)
            .OrderBy(x => x, StringComparer.Ordinal);

        fromJs.Should().Equal(
            fromCatalog,
            "the server and the browser must pick the same stylesheet for every theme");
    }

    [Fact]
    public void App_razor_emits_the_stylesheet_swap_during_the_server_render()
    {
        var appRazor = ReadSource("src", "SentenceStudio.WebApp", "Components", "App.razor");

        appRazor.Should().Contain("StylesheetSwap",
            "a Bootswatch learner must get their stylesheet in the first response, not after the circuit starts");
        appRazor.Should().Contain("_content/SentenceStudio.UI/css/themes/",
            "and it must be the same path app.js would set");
    }

    [Fact]
    public void The_server_and_the_browser_use_the_same_bootstrap_cdn_url()
    {
        var appJs = ReadSource("src", "SentenceStudio.UI", "wwwroot", "js", "app.js");
        var appRazor = ReadSource("src", "SentenceStudio.WebApp", "Components", "App.razor");

        var cdn = Regex.Match(appJs, @"BOOTSTRAP_CDN\s*=\s*'(?<url>[^']+)'");
        cdn.Success.Should().BeTrue();

        appRazor.Should().Contain(
            cdn.Groups["url"].Value,
            "otherwise a non-Bootswatch theme swaps stylesheets for no reason on first render");
    }

    [Fact]
    public void App_razor_writes_the_appearance_attributes_and_the_font_scale_up_front()
    {
        var appRazor = ReadSource("src", "SentenceStudio.WebApp", "Components", "App.razor");

        appRazor.Should().Contain(@"data-bs-theme=""@ThemeService.CurrentMode""");
        appRazor.Should().Contain(@"data-ss-theme=""@ThemeService.CurrentTheme""");
        appRazor.Should().Contain("--ss-font-scale",
            "text size is part of the first paint too, or the page reflows once the circuit connects");
        appRazor.Should().Contain("InvariantCulture",
            "a comma decimal separator would make the CSS variable invalid in some locales");
    }

    [Fact]
    public void The_appearance_cookie_helpers_exist_on_the_browser_side()
    {
        var appJs = ReadSource("src", "SentenceStudio.UI", "wwwroot", "js", "app.js");

        // The circuit has no HTTP response to attach Set-Cookie to, so these are the only write
        // path once the page is interactive.
        appJs.Should().Contain("export function readAppearanceCookie");
        appJs.Should().Contain("export function writeAppearanceCookie");
        appJs.Should().Contain("SameSite=Lax", "the appearance cookie is never sent cross-site");
        appJs.Should().Contain("Path=/", "or a write from /settings would not be visible on /");
    }
}
