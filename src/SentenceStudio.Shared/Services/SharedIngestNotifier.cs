namespace SentenceStudio.Services;

public enum SharedIngestState { Idle, Processing, Completed }

/// <summary>Discriminates what kind of ingest just completed (or is in progress).</summary>
public enum SharedIngestNotificationKind
{
    /// <summary>Text item processed → vocab words created in Shared Inbox.</summary>
    Vocabulary,
    /// <summary>YouTube URL → video import kicked off detached.</summary>
    VideoImportStarted,
    /// <summary>Article URL → imported as a new LearningResource.</summary>
    ResourceImported
}

/// <summary>
/// Thread-safe singleton that bridges <see cref="SharedIngestProcessor"/> to the UI.
/// The drain calls Set* methods; UI components subscribe to <see cref="Changed"/>.
/// </summary>
public sealed class SharedIngestNotifier
{
    public SharedIngestState State { get; private set; } = SharedIngestState.Idle;
    public SharedIngestNotificationKind Kind { get; private set; }

    /// <summary>Created vocab count (Vocabulary / ResourceImported).</summary>
    public int Count { get; private set; }

    /// <summary>Resource or video title (ResourceImported).</summary>
    public string? Title { get; private set; }

    /// <summary>Where the "View" button navigates.</summary>
    public string? ActionRoute { get; private set; }

    public DateTime? LastCompletedAtUtc { get; private set; }

    public event Action? Changed;

    public void SetProcessing(SharedIngestNotificationKind kind)
    {
        State = SharedIngestState.Processing;
        Kind = kind;
        Raise();
    }

    public void SetCompleted(SharedIngestNotificationKind kind, int count, string? title, string? actionRoute)
    {
        State = SharedIngestState.Completed;
        Kind = kind;
        Count = count;
        Title = title;
        ActionRoute = actionRoute;
        LastCompletedAtUtc = DateTime.UtcNow;
        Raise();
    }

    public void ClearCompleted()
    {
        if (State == SharedIngestState.Completed)
        {
            State = SharedIngestState.Idle;
            Raise();
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch { /* never let a subscriber crash the drain */ }
    }
}
