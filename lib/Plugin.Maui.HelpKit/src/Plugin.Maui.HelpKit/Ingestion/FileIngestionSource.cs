using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plugin.Maui.HelpKit.Ingestion;

/// <summary>
/// Walks <see cref="HelpKitOptions.ContentDirectories"/> and yields
/// <c>(sourcePath, content)</c> pairs for every <c>*.md</c> file. Files
/// larger than <see cref="MaxFileSizeBytes"/> are skipped with a warning so
/// a single bloated doc can't OOM the app.
/// </summary>
internal sealed class FileIngestionSource
{
    /// <summary>Per-file size ceiling (1 MB).</summary>
    public const long MaxFileSizeBytes = 1L * 1024 * 1024;

    private readonly HelpKitOptions _options;
    private readonly ILogger<FileIngestionSource> _logger;

    public FileIngestionSource(IOptions<HelpKitOptions> options, ILogger<FileIngestionSource> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Enumerates every markdown file under every configured content
    /// directory. Non-existent directories are logged and skipped.
    /// </summary>
    public IEnumerable<(string SourcePath, string Content)> EnumerateFiles()
    {
        if (_options.ContentDirectories.Count == 0)
        {
            _logger.LogInformation(
                "HelpKitOptions.ContentDirectories is empty; nothing to ingest.");
            yield break;
        }

        foreach (var dir in _options.ContentDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("Content directory not found, skipping: {Directory}", dir);
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                FileInfo info;
                try { info = new FileInfo(path); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not stat markdown file {Path}; skipping.", path);
                    continue;
                }

                if (info.Length > MaxFileSizeBytes)
                {
                    _logger.LogWarning(
                        "Skipping markdown file {Path} ({Size} bytes) — exceeds 1 MB ingestion limit.",
                        path, info.Length);
                    continue;
                }

                string content;
                try { content = File.ReadAllText(path); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read markdown file {Path}; skipping.", path);
                    continue;
                }

                yield return (path, content);
            }
        }
    }
}
