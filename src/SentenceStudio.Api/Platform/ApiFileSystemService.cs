using SentenceStudio.Abstractions;

namespace SentenceStudio.Api.Platform;

public sealed class ApiFileSystemService : IFileSystemService
{
    public ApiFileSystemService(string appDataDirectory)
    {
        AppDataDirectory = appDataDirectory;
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only filesystem (e.g. ACA /app). The API doesn't actually write to this
            // path — the dependency is structural via shared services. Swallow so the DI
            // graph can resolve; downstream writes will surface their own errors if they
            // ever happen, but in practice the API never writes here.
        }
        catch (IOException)
        {
            // Same rationale — permission/EROFS wrapped as IOException on some platforms.
        }
    }

    public string AppDataDirectory { get; }

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        // API server doesn't typically have package files, but we provide
        // a stub implementation for service compatibility
        throw new NotSupportedException("Package files are not supported in the API server context.");
    }
}
