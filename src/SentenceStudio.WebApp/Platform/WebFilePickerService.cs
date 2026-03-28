using Microsoft.JSInterop;
using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebFilePickerService : IFilePickerService
{
    private readonly IJSRuntime _js;

    public WebFilePickerService(IJSRuntime jsRuntime)
    {
        _js = jsRuntime;
    }

    public async Task<FilePickerResult?> PickAsync(FilePickerRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Build the accept string from the requested file types (e.g. ".txt,.csv")
        string? acceptTypes = request.FileTypes is { Count: > 0 }
            ? string.Join(",", request.FileTypes)
            : null;

        var result = await _js.InvokeAsync<FilePickerJsResult?>(
            "filePickerInterop.pickFile", cancellationToken, acceptTypes);

        if (result is null || result.Content is null)
            return null;

        var stream = new MemoryStream(result.Content);
        return new FilePickerResult(result.FileName ?? "unknown", stream);
    }

    /// <summary>
    /// Matches the shape returned by filePicker.js.
    /// </summary>
    private sealed class FilePickerJsResult
    {
        public string? FileName { get; set; }
        public byte[]? Content { get; set; }
    }
}
