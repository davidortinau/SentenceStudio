using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Tracks the last-visited URL per sidebar section so that returning to a section
/// via the sidebar restores the user's position within that section.
/// </summary>
public class NavigationMemoryService : IDisposable
{
    private readonly NavigationManager _nav;
    private readonly Dictionary<string, string> _lastRoutes = new();

    private static readonly (string Key, string Root, string[] Prefixes)[] Sections =
    [
        // Dashboard section: root "/" plus all activity routes launched from dashboard
        ("dashboard", "/", [
            "/conversation", "/reading", "/translation", "/scene",
            "/cloze", "/vocab-quiz", "/vocab-matching", "/how-do-you-say",
            "/shadowing", "/video-watching"
        ]),
        ("activity", "/activity-log", ["/activity-log"]),
        ("resources", "/resources", ["/resources"]),
        ("vocabulary", "/vocabulary", ["/vocabulary"]),
        ("minimal-pairs", "/minimal-pairs", ["/minimal-pairs"]),
        ("skills", "/skills", ["/skills"]),
        ("import-content", "/import-content", ["/import-content"]),
        ("media-import", "/media-import", ["/media-import", "/import"]), // "/import" kept for back-compat
        ("profile", "/profile", ["/profile"]),
        ("settings", "/settings", ["/settings"]),
        ("feedback", "/feedback", ["/feedback"]),
    ];

    /// <summary>
    /// Query parameters that must never be remembered as part of a section route.
    /// The coach overlay is query-backed (<c>?coach=&lt;sessionId&gt;</c>) and the mobile route
    /// carries <c>?pane=coach|plan</c>. Storing either verbatim would make a later sidebar tap on
    /// Dashboard silently re-open the coach workspace, which the learner never asked for.
    /// </summary>
    private static readonly string[] ExcludedQueryKeys = ["coach", "pane"];

    public NavigationMemoryService(NavigationManager nav)
    {
        _nav = nav;
        _nav.LocationChanged += OnLocationChanged;

        // Seed with current location
        var path = GetPath(_nav.Uri);
        var section = ResolveSection(path);
        if (section != null)
            _lastRoutes[section] = path;
    }

    /// <summary>
    /// Returns the last-visited route for the given section key, or the section root if none remembered.
    /// </summary>
    public string GetSectionRoute(string sectionKey)
    {
        if (_lastRoutes.TryGetValue(sectionKey, out var route))
            return route;

        // Return root for the section
        var section = Array.Find(Sections, s => s.Key == sectionKey);
        return section.Root ?? "/";
    }

    /// <summary>
    /// Returns the section key for the given sidebar href (used for active-state detection).
    /// </summary>
    public string? GetCurrentSection()
    {
        var path = GetPath(_nav.Uri);
        return ResolveSection(path);
    }

    /// <summary>
    /// Check if a sidebar section is currently active (the current URL belongs to this section).
    /// </summary>
    public bool IsSectionActive(string sectionKey)
    {
        return GetCurrentSection() == sectionKey;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var path = GetPath(e.Location);
        var section = ResolveSection(path);
        if (section != null)
            _lastRoutes[section] = path;
    }

    private string GetPath(string uri)
    {
        var baseUri = _nav.BaseUri.TrimEnd('/');
        var path = uri.StartsWith(baseUri, StringComparison.OrdinalIgnoreCase)
            ? uri[baseUri.Length..]
            : uri;

        // Ensure leading slash
        if (!path.StartsWith('/'))
            path = "/" + path;

        return StripExcludedQuery(path);
    }

    /// <summary>
    /// Removes workspace-transient query parameters from a remembered route while preserving every
    /// other parameter and the fragment. Public so the rule can be unit tested without a
    /// NavigationManager.
    /// </summary>
    public static string StripExcludedQuery(string path)
    {
        var queryStart = path.IndexOf('?');
        if (queryStart < 0)
        {
            return path;
        }

        var pathOnly = path[..queryStart];
        var rest = path[(queryStart + 1)..];

        // Keep any fragment attached to the end, not to the last query value.
        var fragment = string.Empty;
        var hashIndex = rest.IndexOf('#');
        if (hashIndex >= 0)
        {
            fragment = rest[hashIndex..];
            rest = rest[..hashIndex];
        }

        var kept = rest
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair =>
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                return !ExcludedQueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
            })
            .ToArray();

        return kept.Length == 0
            ? pathOnly + fragment
            : $"{pathOnly}?{string.Join('&', kept)}{fragment}";
    }

    private static string? ResolveSection(string path)
    {
        // Strip query string for matching
        var pathOnly = path.Contains('?') ? path[..path.IndexOf('?')] : path;
        pathOnly = pathOnly.TrimEnd('/');

        // Check non-dashboard sections first (they have explicit prefixes)
        for (int i = 1; i < Sections.Length; i++)
        {
            var section = Sections[i];
            foreach (var prefix in section.Prefixes)
            {
                if (pathOnly.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    pathOnly.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    return section.Key;
            }
        }

        // Dashboard section: root or any of its activity routes
        var dashboard = Sections[0];
        if (pathOnly is "" or "/")
            return dashboard.Key;

        foreach (var prefix in dashboard.Prefixes)
        {
            if (pathOnly.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                pathOnly.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return dashboard.Key;
        }

        return null;
    }

    public void Dispose()
    {
        _nav.LocationChanged -= OnLocationChanged;
    }
}
