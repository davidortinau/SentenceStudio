namespace SentenceStudio.Abstractions;

public interface IFileSystemService
{
    string AppDataDirectory { get; }
    Task<Stream> OpenAppPackageFileAsync(string filename);
}
