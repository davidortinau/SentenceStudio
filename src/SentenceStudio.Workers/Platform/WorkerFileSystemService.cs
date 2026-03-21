using System.Reflection;
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
        // Look for the file as an embedded resource in this assembly
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(filename, StringComparison.OrdinalIgnoreCase));

        if (resourceName != null)
        {
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
                return Task.FromResult(stream);
        }

        throw new FileNotFoundException(
            $"Package file '{filename}' not found as embedded resource in Workers project.", filename);
    }
}
