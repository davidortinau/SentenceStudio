namespace SentenceStudio.Sharing;

public interface ISharedIngestQueue
{
    void Enqueue(SharedIngestItem item);
    IReadOnlyList<SharedIngestItem> List();
    bool Remove(string id);
}
