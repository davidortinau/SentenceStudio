using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace SentenceStudio.Abstractions;

public sealed class MauiSecureStorageService : ISecureStorageService
{
    private readonly ILogger<MauiSecureStorageService> _logger;

    // Fallback to Preferences when SecureStorage is unavailable
    // (e.g., Mac Catalyst without keychain entitlements in debug builds)
    private bool _usePreferencesFallback;
    private const string FallbackPrefix = "__ss_fb_";

    public MauiSecureStorageService(ILogger<MauiSecureStorageService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key)
    {
        if (_usePreferencesFallback)
            return Preferences.Default.Get<string?>(FallbackPrefix + key, null);

        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception)
        {
            if (!_usePreferencesFallback)
            {
                _usePreferencesFallback = true;
                _logger.LogWarning("SecureStorage unavailable on this platform — falling back to Preferences. Tokens will NOT survive app reinstall.");
            }
            return Preferences.Default.Get<string?>(FallbackPrefix + key, null);
        }
    }

    public async Task SetAsync(string key, string value)
    {
        if (_usePreferencesFallback)
        {
            Preferences.Default.Set(FallbackPrefix + key, value);
            return;
        }

        try
        {
            await SecureStorage.Default.SetAsync(key, value);
        }
        catch (Exception)
        {
            if (!_usePreferencesFallback)
            {
                _usePreferencesFallback = true;
                _logger.LogWarning("SecureStorage unavailable on this platform — falling back to Preferences. Tokens will NOT survive app reinstall.");
            }
            Preferences.Default.Set(FallbackPrefix + key, value);
        }
    }

    public bool Remove(string key)
    {
        if (_usePreferencesFallback)
        {
            Preferences.Default.Remove(FallbackPrefix + key);
            return true;
        }

        return SecureStorage.Default.Remove(key);
    }

    public void RemoveAll()
    {
        if (_usePreferencesFallback)
            return;

        SecureStorage.Default.RemoveAll();
    }
}
