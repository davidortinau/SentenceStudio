using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebSecureStorageService(IPreferencesService preferences) : ISecureStorageService
{
    private const string SecurePrefix = "secure:";

    public Task<string?> GetAsync(string key)
    {
        return Task.FromResult(preferences.Get<string?>(SecurePrefix + key, null));
    }

    public Task SetAsync(string key, string value)
    {
        preferences.Set(SecurePrefix + key, value);
        return Task.CompletedTask;
    }

    public bool Remove(string key)
    {
        preferences.Remove(SecurePrefix + key);
        return true;
    }

    public void RemoveAll()
    {
        // Secure entries are isolated by prefix, so remove each key selectively.
        // If full secure-key enumeration is needed later, move to a dedicated secure store.
        throw new NotSupportedException("RemoveAll is not supported for the web secure storage adapter.");
    }
}
