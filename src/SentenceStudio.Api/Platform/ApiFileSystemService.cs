using SentenceStudio.Abstractions;

namespace SentenceStudio.Api.Platform;

public sealed class ApiFileSystemService : IFileSystemService
{
    public ApiFileSystemService(string appDataDirectory)
    {
        AppDataDirectory = appDataDirectory;
        Directory.CreateDirectory(AppDataDirectory);
    }

    public string AppDataDirectory { get; }

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        // API server doesn't typically have package files, but we provide
        // a stub implementation for service compatibility
        throw new NotSupportedException("Package files are not supported in the API server context.");
    }
}
