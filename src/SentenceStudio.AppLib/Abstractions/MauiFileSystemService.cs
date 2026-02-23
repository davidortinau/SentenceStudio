using Microsoft.Maui.Storage;

namespace SentenceStudio.Abstractions;

public sealed class MauiFileSystemService : IFileSystemService
{
    public string AppDataDirectory => FileSystem.AppDataDirectory;

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        return FileSystem.OpenAppPackageFileAsync(filename);
    }
}
