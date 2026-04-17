using System.Text;
using System.Text.RegularExpressions;

namespace Plugin.Maui.HelpKit.Scanner;

/// <summary>
/// Alpha non-AI page scanner. Walks a target MAUI project for
/// <c>*.xaml</c> files and emits one <c>.md</c> stub per page under
/// <c>{outputDir}/helpkit-scan/pages/</c>. Devs edit the stubs to describe
/// each page, then point <see cref="HelpKitOptions.ContentDirectories"/>
/// at the output directory.
/// </summary>
/// <remarks>
/// Alpha behaviour is intentionally trivial — no AI, no network. A Beta
/// release will add an AI enrichment pass delivered as a dotnet tool and
/// MSBuild task.
/// </remarks>
public static class StubScanner
{
    private static readonly Regex XamlTitleRegex = new(
        @"Title\s*=\s*""(?<title>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShellRouteRegex = new(
        @"Route\s*=\s*""(?<route>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QueryPropertyRegex = new(
        @"\[QueryProperty\s*\(\s*""(?<name>[^""]+)""\s*,\s*""(?<key>[^""]+)""\s*\)\]",
        RegexOptions.Compiled);

    private static readonly Regex XamlNamedElementRegex = new(
        @"x:Name\s*=\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex BindingRegex = new(
        @"\{Binding\s+(?<path>[^\s,}\.]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="projectRoot"/> and writes stubs under
    /// <c>{outputDir}/helpkit-scan/pages</c>. Returns the number of pages
    /// emitted.
    /// </summary>
    public static async Task<int> RunAsync(
        string projectRoot,
        string outputDir,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("projectRoot is required.", nameof(projectRoot));
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new ArgumentException("outputDir is required.", nameof(outputDir));
        if (!Directory.Exists(projectRoot))
            throw new DirectoryNotFoundException($"projectRoot not found: {projectRoot}");

        var pagesDir = Path.Combine(outputDir, "helpkit-scan", "pages");
        Directory.CreateDirectory(pagesDir);

        await WriteReadmeAsync(Path.Combine(outputDir, "helpkit-scan"), ct).ConfigureAwait(false);

        var emitted = 0;

        foreach (var xamlPath in EnumerateFiles(projectRoot, "*.xaml"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var xaml = File.ReadAllText(xamlPath);
                var codeBehind = FindCodeBehind(xamlPath);
                var cs = codeBehind is not null ? File.ReadAllText(codeBehind) : string.Empty;

                var pageName = Path.GetFileNameWithoutExtension(xamlPath);
                var title = ExtractTitle(xaml) ?? pageName;
                var routes = ExtractRoutes(cs, xaml);
                var fields = ExtractFields(xaml);

                var md = BuildMarkdown(pageName, title, xamlPath, routes, fields);
                var outPath = Path.Combine(pagesDir, Slug(pageName) + ".md");

                // Never overwrite a file a developer has already hand-edited.
                if (File.Exists(outPath))
                    continue;

                await File.WriteAllTextAsync(outPath, md, ct).ConfigureAwait(false);
                emitted++;
            }
            catch
            {
                // Scanner is best-effort; malformed files are skipped silently.
                continue;
            }
        }

        // TODO(Beta): AI enrichment pass — feed each stub plus page source
        // to an IChatClient and have it draft a usage description.

        return emitted;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var f in files)
        {
            // Skip obvious generated / output directories.
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    private static string? FindCodeBehind(string xamlPath)
    {
        var candidate = xamlPath + ".cs";
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ExtractTitle(string xaml)
    {
        var match = XamlTitleRegex.Match(xaml);
        return match.Success ? match.Groups["title"].Value.Trim() : null;
    }

    private static IReadOnlyList<string> ExtractRoutes(string cs, string xaml)
    {
        var routes = new List<string>();
        foreach (Match m in ShellRouteRegex.Matches(xaml))
            routes.Add(m.Groups["route"].Value);
        foreach (Match m in QueryPropertyRegex.Matches(cs))
            routes.Add($"?{m.Groups["key"].Value} -> {m.Groups["name"].Value}");
        return routes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> ExtractFields(string xaml)
    {
        var fields = new List<string>();
        foreach (Match m in XamlNamedElementRegex.Matches(xaml))
            fields.Add(m.Groups["name"].Value);
        foreach (Match m in BindingRegex.Matches(xaml))
            fields.Add(m.Groups["path"].Value);
        return fields
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildMarkdown(
        string pageName,
        string title,
        string xamlPath,
        IReadOnlyList<string> routes,
        IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"page: {pageName}");
        sb.AppendLine($"title: {title}");
        sb.AppendLine($"source: {xamlPath.Replace('\\', '/')}");
        sb.AppendLine("generated-by: Plugin.Maui.HelpKit StubScanner (alpha)");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("> This file is a starting draft. Edit it to describe what users do on this page,");
        sb.AppendLine("> common questions, and any gotchas. HelpKit ingests your edited content; the");
        sb.AppendLine("> scanner never overwrites a file that already exists in your hand-edited corpus.");
        sb.AppendLine();

        if (routes.Count > 0)
        {
            sb.AppendLine("## Routes");
            sb.AppendLine();
            foreach (var r in routes) sb.AppendLine($"- `{r}`");
            sb.AppendLine();
        }

        sb.AppendLine("## This page has fields");
        sb.AppendLine();
        if (fields.Count == 0)
        {
            sb.AppendLine("- _(no named elements or bindings detected)_");
        }
        else
        {
            foreach (var f in fields) sb.AppendLine($"- `{f}`");
        }
        sb.AppendLine();

        sb.AppendLine("## Common questions");
        sb.AppendLine();
        sb.AppendLine("_(TODO: add what users typically ask about this page.)_");
        sb.AppendLine();
        return sb.ToString();
    }

    private static async Task WriteReadmeAsync(string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var readmePath = Path.Combine(dir, "README.md");
        if (File.Exists(readmePath)) return; // never overwrite hand edits
        var readme =
@"# HelpKit scan output

These files were generated by `Plugin.Maui.HelpKit.Scanner.StubScanner`. They
are a **starting draft** — one file per detected page.

1. Edit each file under `pages/` to describe what the page does, common
   questions, and any gotchas. Keep the frontmatter intact.
2. Point `HelpKitOptions.ContentDirectories` at this folder (or copy edited
   files into your main docs directory).
3. Re-run `AddHelpKit(...).IngestAsync()` at app startup; content changes
   invalidate the pipeline fingerprint and trigger re-ingest.

The Alpha scanner is deliberately trivial — no AI. The Beta release will
add an AI-enriched pass delivered as a dotnet tool + MSBuild task.
";
        await File.WriteAllTextAsync(readmePath, readme, ct).ConfigureAwait(false);
    }

    private static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c is '-' or '_' or '.') sb.Append('-');
            else sb.Append('-');
        }
        return Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    }
}
