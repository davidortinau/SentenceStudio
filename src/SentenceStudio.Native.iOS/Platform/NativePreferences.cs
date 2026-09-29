using Foundation;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativePreferences : IPreferencesService
{
    private readonly NSUserDefaults _store = NSUserDefaults.StandardUserDefaults;

    public T Get<T>(string key, T defaultValue)
    {
        if (_store[key] is null)
            return defaultValue;

        object result = defaultValue switch
        {
            bool => _store.BoolForKey(key),
            int => (int)_store.IntForKey(key),
            long => (long)_store.IntForKey(key),
            string => _store.StringForKey(key) ?? string.Empty,
            _ => throw new NotSupportedException($"Preference type {typeof(T).Name} is not supported.")
        };
        return (T)result;
    }

    public void Set<T>(string key, T value)
    {
        switch (value)
        {
            case bool boolean:
                _store.SetBool(boolean, key);
                break;
            case int number:
                _store.SetInt(number, key);
                break;
            case long number:
                _store.SetInt((nint)number, key);
                break;
            case string text:
                _store.SetString(text, key);
                break;
            default:
                throw new NotSupportedException($"Preference type {typeof(T).Name} is not supported.");
        }
    }

    public void Remove(string key) => _store.RemoveObject(key);

    public void Clear() =>
        throw new NotSupportedException("The native head never clears an entire preferences domain.");
}
