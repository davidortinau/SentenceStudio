using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Decision 8b (Captain, 2026-11-04): iOS and Android read one shared table of the web
/// app's exact theme colours. Every value is compared here with the CSS the web app
/// ships: <c>app.css</c> for the five custom themes, stock Bootstrap 5.3.3 for their
/// alerts, and the five Bootswatch 5.3.8 stylesheets.
/// </summary>
public sealed partial class NativeThemeColorTableTests
{
    private static readonly string[] Bootswatch = ["flatly", "sketchy", "slate", "vapor", "brite"];

    public static TheoryData<string, bool> Themes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var theme in ThemeCatalog.All)
        {
            data.Add(theme.Id, false);
            data.Add(theme.Id, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryRoleEqualsTheWebCss(string themeId, bool dark)
    {
        var table = NativeThemeColorTable.For(themeId, dark);
        var web = WebColors(themeId, dark);

        Assert.Equal(web["--bs-primary"], table.Primary);
        Assert.Equal(web["--bs-secondary"], table.Secondary);
        Assert.Equal(web["--bs-body-bg"], table.Body);
        Assert.Equal(web["--bs-body-color"], table.Text);
        Assert.Equal(web["--bs-secondary-color"], table.SecondaryText);
        Assert.Equal(web["--bs-tertiary-color"], table.TertiaryText);
        Assert.Equal(web["--bs-secondary-bg"], table.SecondarySurface);
        Assert.Equal(web["--bs-tertiary-bg"], table.CardSurface);
        Assert.Equal(web["--bs-border-color"], table.Border);
        Assert.Equal(web["--ss-surface-elevated"], table.ElevatedSurface);
        Assert.Equal(web["--ss-on-primary"], table.OnPrimary);
        if (web.TryGetValue("--ss-accent", out var accent))
            Assert.Equal(accent, table.Accent);

        Assert.Equal(Alert(web, "success"), table.SuccessAlert);
        Assert.Equal(Alert(web, "danger"), table.DangerAlert);
        Assert.Equal(Alert(web, "info"), table.InfoAlert);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void BrandColoursEqualTheSharedCatalogue(string themeId, bool dark)
    {
        var palette = ThemeCatalog.Get(themeId).PaletteFor(dark ? ThemeMode.Dark : ThemeMode.Light);
        var table = NativeThemeColorTable.For(themeId, dark);
        Assert.Equal((palette.Primary.Hex, palette.Accent.Hex, palette.Surface.Hex),
            (table.Primary, table.Accent, table.Body));
    }

    [Fact]
    public void DarkSurfaceFollowsThePageNotTheMode()
    {
        Assert.True(NativeThemeColorTable.For("slate", dark: false).IsDarkSurface);
        Assert.True(NativeThemeColorTable.For("vapor", dark: false).IsDarkSurface);
        Assert.False(NativeThemeColorTable.For("flatly", dark: false).IsDarkSurface);
        Assert.True(NativeThemeColorTable.For("flatly", dark: true).IsDarkSurface);
        Assert.False(NativeThemeColorTable.For("seoul-pop", dark: false).IsDarkSurface);
        Assert.True(NativeThemeColorTable.For("seoul-pop", dark: true).IsDarkSurface);
    }

    [Fact]
    public void UnknownThemeIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeThemeColorTable.For("not-a-theme", dark: true));

    private static NativeThemeAlertColors Alert(IReadOnlyDictionary<string, string> web, string variant) =>
        new(web[$"--bs-{variant}-bg-subtle"], web[$"--bs-{variant}-text-emphasis"],
            web[$"--bs-{variant}-border-subtle"]);

    /// <summary>The effective CSS custom properties for one theme and mode, as opaque hex.</summary>
    private static Dictionary<string, string> WebColors(string themeId, bool dark)
    {
        var appCss = Read("src/SentenceStudio.UI/wwwroot/css/app.css");
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Bootswatch.Contains(themeId))
        {
            var theme = Read($"src/SentenceStudio.UI/wwwroot/css/themes/{themeId}.min.css");
            Merge(vars, theme, ":root,[data-bs-theme=light]");
            if (dark)
                Merge(vars, theme, "[data-bs-theme=dark]");
            // app.css :root sets --ss-on-primary for every theme; Brite overrides it.
            Merge(vars, appCss, ":root", only: "--ss-on-primary");
            Merge(vars, appCss, $"[data-ss-theme=\"{themeId}\"]", only: "--ss-on-primary",
                required: false);
            // Bootswatch has no --ss-surface-elevated; native elevated surfaces use
            // --bs-secondary-bg (documented in NativeThemeColorTable).
            vars["--ss-surface-elevated"] = vars["--bs-secondary-bg"];
        }
        else
        {
            // Custom themes overlay stock Bootstrap 5.3.3, which supplies the alert tokens.
            var bootstrap = Read("src/SentenceStudio.WebApp/wwwroot/lib/bootstrap/dist/css/bootstrap.min.css");
            Assert.Contains("Bootstrap  v5.3.3", bootstrap);
            Merge(vars, bootstrap, ":root,[data-bs-theme=light]");
            if (dark)
                Merge(vars, bootstrap, "[data-bs-theme=dark]");
            Merge(vars, appCss,
                $"[data-ss-theme=\"{themeId}\"][data-bs-theme=\"{(dark ? "dark" : "light")}\"]");
        }

        var body = Hex(vars["--bs-body-bg"], null);
        return vars.ToDictionary(pair => pair.Key, pair => Hex(pair.Value, body),
            StringComparer.Ordinal);
    }

    private static void Merge(Dictionary<string, string> vars, string css, string selector,
        string? only = null, bool required = true)
    {
        var found = false;
        // A selector starts a rule after a previous rule, an @import, whitespace or a comment end.
        var pattern = new Regex(@"(^|[};\s/])" + Regex.Escape(selector) + @"\s*\{([^}]*)\}",
            RegexOptions.Multiline);
        foreach (Match block in pattern.Matches(css))
        {
            found = true;
            foreach (Match declaration in Declaration().Matches(block.Groups[2].Value))
            {
                var name = declaration.Groups[1].Value;
                if (only is null || name == only)
                    vars[name] = declaration.Groups[2].Value.Trim();
            }
        }
        Assert.True(found || !required, $"No CSS block for selector {selector}.");
    }

    // Opaque uppercase #RRGGBB; rgba() is composited over the page like the browser.
    private static string Hex(string value, string? body)
    {
        if (value.StartsWith('#'))
        {
            var digits = value[1..];
            if (digits.Length == 3)
                digits = string.Concat(digits.Select(c => $"{c}{c}"));
            return "#" + digits.ToUpperInvariant();
        }
        var rgba = Rgba().Match(value);
        if (rgba.Success && body is not null)
        {
            var alpha = double.Parse(rgba.Groups[4].Value, CultureInfo.InvariantCulture);
            var back = SrgbColor.Parse(body);
            static int Channel(string front, byte back, double a) =>
                (int)Math.Round(int.Parse(front, CultureInfo.InvariantCulture) * a + back * (1 - a));
            return $"#{Channel(rgba.Groups[1].Value, back.R, alpha):X2}"
                + $"{Channel(rgba.Groups[2].Value, back.G, alpha):X2}"
                + $"{Channel(rgba.Groups[3].Value, back.B, alpha):X2}";
        }
        return value;
    }

    private static string Read(string repositoryPath, [CallerFilePath] string testFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(root, repositoryPath));
    }

    [GeneratedRegex(@"(--[a-z0-9-]+)\s*:\s*([^;]+)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([0-9.]+)\s*\)")]
    private static partial Regex Rgba();
}
