using Microsoft.Maui.Storage;

namespace SentenceStudio.Abstractions;

public sealed class MauiFilePickerService : IFilePickerService
{
    public async Task<FilePickerResult?> PickAsync(FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = new PickOptions
        {
            PickerTitle = request.Title
        };

        var result = await FilePicker.Default.PickAsync(options);
        if (result == null)
        {
            return null;
        }

        var stream = await result.OpenReadAsync();
        return new FilePickerResult(result.FileName, stream);
    }
}
