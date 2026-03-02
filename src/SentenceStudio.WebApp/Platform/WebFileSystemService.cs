using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebFileSystemService : IFileSystemService
{
    private readonly string _rawAssetsDirectory;

    public WebFileSystemService(string appDataDirectory, string rawAssetsDirectory)
    {
        AppDataDirectory = appDataDirectory;
        _rawAssetsDirectory = rawAssetsDirectory;
        Directory.CreateDirectory(AppDataDirectory);
    }

    public string AppDataDirectory { get; }

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        var safeFilename = Path.GetFileName(filename);
        var fullPath = Path.Combine(_rawAssetsDirectory, safeFilename);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Package file '{safeFilename}' was not found at '{fullPath}'.", safeFilename);
        }

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult(stream);
    }
}
