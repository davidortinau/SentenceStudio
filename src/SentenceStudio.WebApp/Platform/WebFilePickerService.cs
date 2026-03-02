using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebFilePickerService : IFilePickerService
{
    public Task<FilePickerResult?> PickAsync(FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Server-side file picking is not supported in SentenceStudio.WebApp.");
    }
}
