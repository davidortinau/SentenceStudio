using System.Text.Json;
using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebPreferencesService : IPreferencesService
{
    private readonly string _storagePath;
    private readonly object _syncRoot = new();
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public WebPreferencesService(string storagePath)
    {
        _storagePath = storagePath;
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_storagePath))
        {
            var json = File.ReadAllText(_storagePath);
            _values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
        }
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_syncRoot)
        {
            if (!_values.TryGetValue(key, out var serialized))
            {
                return defaultValue;
            }

            return JsonSerializer.Deserialize<T>(serialized) ?? defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_syncRoot)
        {
            _values[key] = JsonSerializer.Serialize(value);
            Persist();
        }
    }

    public void Remove(string key)
    {
        lock (_syncRoot)
        {
            if (_values.Remove(key))
            {
                Persist();
            }
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _values.Clear();
            Persist();
        }
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(_values);
        File.WriteAllText(_storagePath, json);
    }
}
