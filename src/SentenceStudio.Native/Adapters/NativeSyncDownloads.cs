namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Counts sync downloads that applied rows to this device's database. A reader that
/// captured <see cref="Generation"/> before reading knows its rows predate a download when
/// the value has moved since (<see cref="INativeSyncReadiness.SyncGeneration"/>).
/// </summary>
public sealed class NativeSyncDownloads
{
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    internal void Applied() => Interlocked.Increment(ref _generation);
}
