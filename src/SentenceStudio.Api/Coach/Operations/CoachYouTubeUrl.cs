using System.Diagnostics.CodeAnalysis;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// Decides whether a string the model produced is a YouTube video address, and reduces it to a
/// video id.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the import tool is the only place in the coach surface where a
/// model-supplied string turns into an outbound network request. Without a shape check the tool
/// would be a general fetch primitive wearing an import tool's name: point it at an internal
/// address and it becomes server-side request forgery, point it at a redirector and the
/// destination is chosen after the check.
/// </para>
/// <para>
/// The check is deliberately narrower than "is this a valid URL". Scheme must be https, host must
/// be one of a fixed handful, the path must match the exact shape YouTube uses, and the video id
/// must be the eleven-character token YouTube issues. Everything else is refused, including hosts
/// that merely end in youtube.com — <c>youtube.com.attacker.example</c> passes a suffix test and
/// fails this one.
/// </para>
/// <para>
/// Only the extracted id is carried forward. The original string is never used to build the
/// request, so a userinfo segment, an embedded credential, or a query parameter cannot ride along
/// into the call.
/// </para>
/// </remarks>
public static class CoachYouTubeUrl
{
    /// <summary>Hosts a YouTube watch address may use. Compared whole, never by suffix.</summary>
    private static readonly string[] AllowedHosts =
    [
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
        "youtu.be"
    ];

    /// <summary>Length of a YouTube video id.</summary>
    private const int VideoIdLength = 11;

    /// <summary>The longest address worth parsing.</summary>
    private const int MaxUrlLength = 2048;

    /// <summary>
    /// Extracts the video id when <paramref name="value"/> is a YouTube video address.
    /// </summary>
    /// <returns><c>true</c> when a video id was recovered; otherwise <c>false</c>.</returns>
    public static bool TryGetVideoId(string? value, [NotNullWhen(true)] out string? videoId)
    {
        videoId = null;

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUrlLength)
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        // A populated userinfo segment is how a hostile address hides its real host from a reader.
        // The parser is not fooled, but refusing outright keeps the address that reaches the log
        // and the address that reaches the network the same shape.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (!uri.IsDefaultPort)
        {
            return false;
        }

        var host = uri.Host;
        if (!AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = string.Equals(host, "youtu.be", StringComparison.OrdinalIgnoreCase)
            ? FromShortForm(uri)
            : FromLongForm(uri);

        if (!IsVideoId(candidate))
        {
            return false;
        }

        videoId = candidate;
        return true;
    }

    /// <summary>
    /// Rebuilds a canonical watch address from a video id.
    /// </summary>
    /// <remarks>
    /// The import path calls the downstream service with this, not with the string the model
    /// supplied, so whatever else that string carried does not survive the check.
    /// </remarks>
    public static string CanonicalUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    private static string? FromShortForm(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 ? null : path;
    }

    private static string? FromLongForm(Uri uri)
    {
        var path = uri.AbsolutePath;

        if (string.Equals(path, "/watch", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (string.Equals(pair[..separator], "v", StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pair[(separator + 1)..]);
                }
            }

            return null;
        }

        foreach (var prefix in (ReadOnlySpan<string>)["/shorts/", "/embed/", "/live/", "/v/"])
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('/');
                return slash < 0 ? rest : rest[..slash];
            }
        }

        return null;
    }

    private static bool IsVideoId([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: VideoIdLength })
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
