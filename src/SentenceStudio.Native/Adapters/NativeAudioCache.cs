namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Resolves a StreamHistory clip inside the current AppData/AudioCache directory.
/// iOS moves the app container on every install, so a row's stored absolute
/// path from an earlier install no longer exists; the clip is found by its file
/// name under today's AudioCache instead. Nothing outside that directory is read,
/// and existing rows are not rewritten.
/// </summary>
public static class NativeAudioCache
{
    public const string DirectoryName = "AudioCache";

    public static string DirectoryFor(string appDataDirectory) =>
        Path.Combine(appDataDirectory, DirectoryName);

    /// <summary>The cached clip's current path, or null when absent, empty or over <paramref name="maxBytes"/>.</summary>
    public static string? Resolve(string appDataDirectory, string? storedPath, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(appDataDirectory) || string.IsNullOrWhiteSpace(storedPath))
            return null;
        try
        {
            var directory = Path.GetFullPath(DirectoryFor(appDataDirectory));
            var name = Path.GetFileName(storedPath);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
                return null;
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(path))
                return null;
            var length = new FileInfo(path).Length;
            return length > 0 && length <= maxBytes ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
