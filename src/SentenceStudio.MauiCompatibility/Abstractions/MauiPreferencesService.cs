using Microsoft.Maui.Storage;

namespace SentenceStudio.Abstractions;

public sealed class MauiPreferencesService : IPreferencesService
{
    public T Get<T>(string key, T defaultValue)
    {
        return Preferences.Default.Get(key, defaultValue);
    }

    public void Set<T>(string key, T value)
    {
        Preferences.Default.Set(key, value);
    }

    public void Remove(string key)
    {
        Preferences.Default.Remove(key);
    }

    public void Clear()
    {
        Preferences.Default.Clear();
    }
}
