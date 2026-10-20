namespace SentenceStudio.Native.Android;

internal static class AndroidQuizMediaPolicy
{
    public static string? LanguageTag(string? language) => language?.Trim() switch
    {
        "English" => "en-US",
        "Korean" => "ko-KR",
        "French" => "fr-FR",
        "German" => "de-DE",
        "Spanish" => "es-ES",
        "Japanese" => "ja-JP",
        "Chinese" => "zh-CN",
        _ => null
    };

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
