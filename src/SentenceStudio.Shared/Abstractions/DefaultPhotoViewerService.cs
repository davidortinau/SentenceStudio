namespace SentenceStudio.Abstractions;

/// <summary>
/// Default/web implementation of <see cref="IPhotoViewerService"/> that explicitly
/// declines native presentation. The caller (VocabQuiz.razor) will use the existing
/// Razor/CSS fullscreen overlay when this service returns not-handled.
/// </summary>
public sealed class DefaultPhotoViewerService : IPhotoViewerService
{
    public Task<PhotoViewerResult> ShowAsync(PhotoViewerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new PhotoViewerResult(HandledByNative: false));
    }
}
