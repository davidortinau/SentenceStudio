namespace SentenceStudio.Native.Android;

/// <summary>
/// Navigation policy for the approved YouTube embed web view, matching the
/// iOS bridge player: https://www.youtube-nocookie.com/embed/{id} only in the main
/// frame, YouTube/Google https hosts for the player's own subframes.
/// </summary>
internal static class AndroidYouTubeEmbedPolicy
{
    internal const string EmbedHost = "www.youtube-nocookie.com";

    internal static bool IsVideoId(string? value) =>
        value is { Length: 11 } && value.All(character =>
            character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '_');

    internal static string? EmbedUrl(string videoId) =>
        IsVideoId(videoId) ? $"https://{EmbedHost}/embed/{videoId}?playsinline=1&rel=0" : null;

    internal static bool AllowsNavigation(Uri? url, bool isMainFrame, string videoId)
    {
        if (url is null || !IsVideoId(videoId))
            return false;
        if (isMainFrame)
            return url.Scheme == Uri.UriSchemeHttps && url.Host == EmbedHost
                && url.AbsolutePath == $"/embed/{videoId}" && string.IsNullOrEmpty(url.UserInfo)
                && url.IsDefaultPort;
        if (url.Scheme == "about")
            return url.OriginalString is "about:blank" or "about:srcdoc";
        if (url.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = url.Host.ToLowerInvariant();
        return new[] { "youtube-nocookie.com", "youtube.com", "google.com" }
            .Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }
}
