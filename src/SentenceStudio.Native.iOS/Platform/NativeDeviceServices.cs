using System.Net.NetworkInformation;
using Foundation;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativeConnectivity : IConnectivityService
{
    public bool IsInternetAvailable => NetworkInterface.GetIsNetworkAvailable();
}

public sealed class NativeFileSystem : IFileSystemService
{
    public string AppDataDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (filename != Path.GetFileName(filename))
            throw new ArgumentException("App package resources must be a filename.", nameof(filename));
        var path = NSBundle.MainBundle.PathForResource(
            Path.GetFileNameWithoutExtension(filename), Path.GetExtension(filename).TrimStart('.'))
            ?? throw new FileNotFoundException("App package resource not found.", filename);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }
}
