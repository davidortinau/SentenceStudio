using SentenceStudio.Abstractions;

namespace SentenceStudio.Workers.Platform;

public sealed class WorkerFileSystemService : IFileSystemService
{
    public WorkerFileSystemService(string appDataDirectory)
    {
        AppDataDirectory = appDataDirectory;
        Directory.CreateDirectory(AppDataDirectory);
    }

    public string AppDataDirectory { get; }

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        // Worker doesn't have package files, but we provide
        // a stub implementation for service compatibility
        throw new NotSupportedException("Package files are not supported in the worker context.");
    }
}
