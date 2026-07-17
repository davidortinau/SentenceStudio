namespace SentenceStudio.Services;

public enum FullscreenFocusTarget
{
    None,
    Thumbnail,
    ActivityContent
}

public static class FullscreenFocusTargetPolicy
{
    public static FullscreenFocusTarget Resolve(
        bool restoreRequested,
        bool thumbnailAvailable)
    {
        if (!restoreRequested)
            return FullscreenFocusTarget.None;

        return thumbnailAvailable
            ? FullscreenFocusTarget.Thumbnail
            : FullscreenFocusTarget.ActivityContent;
    }
}

public sealed class FullscreenDialogLifecycle
{
    private readonly object _gate = new();
    private bool _isOpen;
    private Task? _closeTask;

    public bool IsOpen
    {
        get { lock (_gate) return _isOpen; }
    }

    public bool Open()
    {
        lock (_gate)
        {
            if (_isOpen || _closeTask is not null)
                return false;

            _isOpen = true;
            return true;
        }
    }

    public Task CloseAsync(Func<Task> detachAsync)
    {
        ArgumentNullException.ThrowIfNull(detachAsync);
        lock (_gate)
        {
            if (!_isOpen)
                return _closeTask ?? Task.CompletedTask;

            return _closeTask ??= CloseCoreAsync(detachAsync);
        }
    }

    private async Task CloseCoreAsync(Func<Task> detachAsync)
    {
        await Task.Yield();
        try
        {
            await detachAsync();
        }
        finally
        {
            lock (_gate)
            {
                _isOpen = false;
                _closeTask = null;
            }
        }
    }
}
