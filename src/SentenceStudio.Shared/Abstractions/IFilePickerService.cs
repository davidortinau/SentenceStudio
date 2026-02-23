namespace SentenceStudio.Abstractions;

public sealed record FilePickerRequest(string? Title, IReadOnlyCollection<string>? FileTypes);

public sealed record FilePickerResult(string FileName, Stream Content);

public interface IFilePickerService
{
    Task<FilePickerResult?> PickAsync(FilePickerRequest request, CancellationToken cancellationToken = default);
}
