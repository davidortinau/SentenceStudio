namespace SentenceStudio.Native.Android;

internal static class AndroidQuizMediaPolicy
{
    public static bool TryResolveRemotePhoto(
        string? source, Uri apiRoot, bool allowEmulatorHttp, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var candidate = source.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal)
            || candidate.Contains('\\')
            || !Uri.TryCreate(candidate, UriKind.RelativeOrAbsolute, out var parsed))
            return false;

        if (!parsed.IsAbsoluteUri)
        {
            if (!candidate.StartsWith("/", StringComparison.Ordinal)
                || !Uri.TryCreate(apiRoot, parsed, out parsed))
                return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo)
            || parsed.Scheme != Uri.UriSchemeHttps
                && !(allowEmulatorHttp
                    && parsed.Scheme == Uri.UriSchemeHttp
                    && parsed.Host == "10.0.2.2"
                    && parsed.Authority == apiRoot.Authority))
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>
    /// The image type proven by the bytes themselves (PNG, JPEG, GIF or WebP);
    /// a server's Content-Type alone is not trusted. Same signatures as iOS.
    /// </summary>
    public static string? DetectImageContentType(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length >= png.Length && data[..png.Length].SequenceEqual(png))
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff)
            return "image/jpeg";
        if (data.Length >= 6 && data[..3].SequenceEqual("GIF"u8) && data[3] == '8')
            return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8)
            && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    public static bool CanRequestWithoutOwnerToken(Uri? remote, Uri apiRoot) =>
        remote is not null
        && !string.Equals(remote.Host, apiRoot.Host,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsPrivatePhoto(
        string? path, string dataRoot, string owner)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dataRoot)
            || string.IsNullOrWhiteSpace(owner)
            || owner.Contains('/') || owner.Contains('\\'))
            return false;
        try
        {
            var root = Path.GetFullPath(dataRoot);
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
            var parts = relative.Replace('\\', '/').Split('/');
            return parts.Length >= 3
                && (parts[0] is "Media" or "PhotoCache")
                && parts[1] == owner
                && !parts.Skip(2).Any(part => part is "." or "..")
                && !Path.IsPathRooted(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }
}
