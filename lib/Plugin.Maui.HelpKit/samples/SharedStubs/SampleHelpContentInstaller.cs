using Microsoft.Maui.Storage;

namespace HelpKitSample.SharedStubs;

/// <summary>
/// Copies the bundled <c>help-content/*.md</c> MauiAsset files to a writable
/// folder on first run so HelpKit can ingest them. HelpKit's ContentDirectories
/// expect real filesystem paths, and MauiAssets are only reachable via
/// <see cref="FileSystem.OpenAppPackageFileAsync"/>, so we materialize them once.
/// </summary>
public static class SampleHelpContentInstaller
{
    private static readonly string[] s_fileNames =
    {
        "getting-started.md",
        "features.md",
        "troubleshooting.md",
    };

    /// <summary>
    /// Ensures the sample help-content markdown files are available at
    /// <c>{AppDataDirectory}/help-content</c>. Returns the target directory.
    /// </summary>
    public static async Task<string> EnsureInstalledAsync(CancellationToken ct = default)
    {
        var targetDir = Path.Combine(FileSystem.AppDataDirectory, "help-content");
        Directory.CreateDirectory(targetDir);

        foreach (var fileName in s_fileNames)
        {
            ct.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(targetDir, fileName);
            if (File.Exists(targetPath))
                continue;

            try
            {
                await using var source = await FileSystem.OpenAppPackageFileAsync(Path.Combine("help-content", fileName)).ConfigureAwait(false);
                await using var dest = File.Create(targetPath);
                await source.CopyToAsync(dest, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // Asset not bundled on this platform — skip silently.
            }
        }

        return targetDir;
    }
}
