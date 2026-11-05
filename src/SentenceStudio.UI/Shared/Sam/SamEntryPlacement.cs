using Microsoft.AspNetCore.Components;

namespace SentenceStudio.WebUI.Shared.Sam;

/// <summary>Where a signed-in page shows its way into Sam (docs/sam-entry.md).</summary>
public enum SamEntryPlacement
{
    /// <summary>No entry: the page is Sam's own surface.</summary>
    None = 0,

    /// <summary>The floating round button in the bottom corner.</summary>
    Floating,

    /// <summary>An icon in the page's <c>PageHeader</c>.</summary>
    Header
}

/// <summary>
/// Which entry a page gets. The floating button belongs to the main pages only, the Dashboard
/// (which holds Today's Plan on the web) and Resources, the same screens that are tab roots in the
/// native apps. Every other signed-in page puts Sam in its header.
/// </summary>
/// <remarks>
/// Read from the URL by both the overlay host (which draws the floating button) and the page
/// header (which draws the icon), so the two can never both show, and each decides on its own
/// render rather than waiting for the other to hear about a navigation. Whether Sam is available
/// at all is a separate question, answered by <see cref="SamEntryState"/>.
/// </remarks>
public static class SamEntryPlacementRule
{
    /// <param name="baseRelativePath">
    /// The location relative to the app's base address, as
    /// <see cref="NavigationManager.ToBaseRelativePath(string)"/> returns it; a query or fragment
    /// is ignored.
    /// </param>
    public static SamEntryPlacement ForPath(string? baseRelativePath)
    {
        var path = baseRelativePath ?? string.Empty;
        var end = path.IndexOfAny(['?', '#']);
        if (end >= 0)
        {
            path = path[..end];
        }

        path = path.Trim('/');

        if (path.Length == 0 || path.Equals("resources", StringComparison.OrdinalIgnoreCase))
        {
            return SamEntryPlacement.Floating;
        }

        // The full-page coach is Sam's own surface; an entry to it there would open a second Sam.
        if (path.Equals("coach", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("coach/", StringComparison.OrdinalIgnoreCase))
        {
            return SamEntryPlacement.None;
        }

        return SamEntryPlacement.Header;
    }

    /// <summary>The placement for the page <paramref name="navigation"/> is on now.</summary>
    /// <remarks>
    /// Read during render, so it never throws: <see cref="NavigationManager.ToBaseRelativePath"/>
    /// does for a location outside the base address, and this falls back to the location's path.
    /// </remarks>
    public static SamEntryPlacement For(NavigationManager navigation)
    {
        var uri = navigation.Uri;
        string relative;
        try
        {
            // The framework's own rule, including the base address without its trailing slash
            // (https://host/app for a base of https://host/app/), which is the Dashboard.
            relative = navigation.ToBaseRelativePath(uri);
        }
        catch (ArgumentException)
        {
            relative = Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : uri;
        }

        return ForPath(relative);
    }
}
