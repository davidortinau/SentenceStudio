namespace SentenceStudio.Abstractions;

public interface IPreferencesService
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    void Remove(string key);
    void Clear();
}
