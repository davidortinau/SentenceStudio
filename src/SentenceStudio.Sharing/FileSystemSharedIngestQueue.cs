using System.Text.Json;

namespace SentenceStudio.Sharing;

public sealed class FileSystemSharedIngestQueue : ISharedIngestQueue
{
    private readonly string _directory;

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = false
    };

    public FileSystemSharedIngestQueue(string directoryPath)
    {
        _directory = directoryPath;
        Directory.CreateDirectory(_directory);
    }

    public void Enqueue(SharedIngestItem item)
    {
        var json = JsonSerializer.Serialize(item, _jsonOptions);
        var finalPath = Path.Combine(_directory, $"{item.Id}.json");
        var tempPath = Path.Combine(_directory, $"{item.Id}.tmp");

        File.WriteAllText(tempPath, json);

        // Atomic replace: move temp over the final file
        if (File.Exists(finalPath))
            File.Delete(finalPath);

        File.Move(tempPath, finalPath);
    }

    public IReadOnlyList<SharedIngestItem> List()
    {
        var results = new List<SharedIngestItem>();

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "*.json");
        }
        catch
        {
            return results;
        }

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<SharedIngestItem>(json, _jsonOptions);
                if (item is not null)
                    results.Add(item);
            }
            catch
            {
                // skip malformed or concurrently deleted files
            }
        }

        results.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
        return results.AsReadOnly();
    }

    public bool Remove(string id)
    {
        var path = Path.Combine(_directory, $"{id}.json");
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
