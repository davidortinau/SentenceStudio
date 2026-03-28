using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebSecureStorageService : ISecureStorageService
{
    private const string SecurePrefix = "secure:";
    private const string Purpose = "SentenceStudio.SecureStorage";

    private readonly IPreferencesService _preferences;
    private readonly IDataProtector _protector;
    private readonly ILogger<WebSecureStorageService> _logger;

    public WebSecureStorageService(
        IPreferencesService preferences,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<WebSecureStorageService> logger)
    {
        _preferences = preferences;
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    public Task<string?> GetAsync(string key)
    {
        var encrypted = _preferences.Get<string?>(SecurePrefix + key, null);
        if (encrypted is null)
            return Task.FromResult<string?>(null);

        try
        {
            var decrypted = _protector.Unprotect(encrypted);
            return Task.FromResult<string?>(decrypted);
        }
        catch (CryptographicException ex)
        {
            // Key rotation or corrupted data — clear the stale entry and return null
            _logger.LogWarning(ex, "Failed to decrypt secure storage key '{Key}'; returning null", key);
            _preferences.Remove(SecurePrefix + key);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value)
    {
        var encrypted = _protector.Protect(value);
        _preferences.Set(SecurePrefix + key, encrypted);
        return Task.CompletedTask;
    }

    public bool Remove(string key)
    {
        _preferences.Remove(SecurePrefix + key);
        return true;
    }

    public void RemoveAll()
    {
        // Secure entries are isolated by prefix, so remove each key selectively.
        // If full secure-key enumeration is needed later, move to a dedicated secure store.
        throw new NotSupportedException("RemoveAll is not supported for the web secure storage adapter.");
    }
}
