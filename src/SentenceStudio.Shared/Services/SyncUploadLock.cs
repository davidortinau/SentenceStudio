namespace SentenceStudio.Services;

/// <summary>
/// The PostgreSQL advisory lock that serializes sync uploads across API replicas while their
/// ownership is checked and written (<c>NativeOwnerSyncProvider</c>). A server write that must not
/// interleave with an upload (the smart-list fold, which moves what names a copy and then deletes
/// it) takes the same key.
/// </summary>
public static class SyncUploadLock
{
    public const long Key = 0x53534E4154495645L;
}
