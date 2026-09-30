using Microsoft.Maui.Storage;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativePreferences : IPreferencesService
{
    public T Get<T>(string key, T defaultValue) =>
        Preferences.Default.Get(key, defaultValue);

    public void Set<T>(string key, T value) =>
        Preferences.Default.Set(key, value);

    public void Remove(string key) => Preferences.Default.Remove(key);

    public void Clear() =>
        throw new NotSupportedException("The native head never clears an entire preferences domain.");
}
