using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;
using Android.Net;
using Android.Runtime;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Native.Android;

internal sealed class AndroidPreferences : IPreferencesService
{
    private readonly ISharedPreferences _preferences;

    public AndroidPreferences(Context context, bool preview = false)
    {
        _preferences = context.GetSharedPreferences(
            preview ? "sentence-studio-native-preview-settings"
                : "sentence-studio-native-settings", FileCreationMode.Private)
            ?? throw new InvalidOperationException("Android settings storage is unavailable.");
    }

    public T Get<T>(string key, T defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = _preferences.GetString(key, null);
        if (json is null)
            return defaultValue;
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidDataException($"Stored preference '{key}' could not be decoded.");
    }

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var editor = _preferences.Edit()
            ?? throw new InvalidOperationException("Android settings editor is unavailable.");
        if (!editor.PutString(key, JsonSerializer.Serialize(value)).Commit())
            throw new IOException($"Could not save preference '{key}'.");
    }

    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var editor = _preferences.Edit()
            ?? throw new InvalidOperationException("Android settings editor is unavailable.");
        if (!editor.Remove(key).Commit())
            throw new IOException($"Could not remove preference '{key}'.");
    }

    public void Clear() => throw new NotSupportedException(
        "Clearing all native preferences is prohibited; remove explicit keys instead.");
}

internal sealed class AndroidSecureStorage : ISecureStorageService
{
    private const string KeyAlias = "sentence-studio-native-tokens-v1";
    private const int IvLength = 12;
    private const int TagLength = 16;
    private readonly object _gate = new();
    private readonly ISharedPreferences _preferences;
    private readonly KeyStore _keyStore;

    public AndroidSecureStorage(Context context)
    {
        _preferences = context.GetSharedPreferences(
            "sentence-studio-native-secure", FileCreationMode.Private)
            ?? throw new InvalidOperationException("Android secure preferences are unavailable.");
        _keyStore = KeyStore.GetInstance("AndroidKeyStore")
            ?? throw new InvalidOperationException("Android KeyStore is unavailable.");
        _keyStore.Load(null);
    }

    public Task<string?> GetAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var encoded = _preferences.GetString(StorageKey(key), null);
            if (encoded is null)
                return Task.FromResult<string?>(null);

            var payload = Convert.FromBase64String(encoded);
            if (payload.Length < IvLength + TagLength)
                throw new CryptographicException("Stored credential is truncated.");

            var iv = payload.AsSpan(0, IvLength).ToArray();
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
                ?? throw new CryptographicException("AES-GCM is unavailable.");
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, iv));
            var plaintext = cipher.DoFinal(payload, IvLength, payload.Length - IvLength)
                ?? throw new CryptographicException("Stored credential could not be decrypted.");
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plaintext));
        }
    }

    public Task SetAsync(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
                ?? throw new CryptographicException("AES-GCM is unavailable.");
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateKey());
            var iv = cipher.GetIV()
                ?? throw new CryptographicException("AES-GCM did not produce an IV.");
            if (iv.Length != IvLength)
                throw new CryptographicException("AES-GCM produced an unexpected IV length.");
            var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(value))
                ?? throw new CryptographicException("Credential encryption failed.");
            var payload = new byte[IvLength + encrypted.Length];
            Buffer.BlockCopy(iv, 0, payload, 0, IvLength);
            Buffer.BlockCopy(encrypted, 0, payload, IvLength, encrypted.Length);
            var editor = _preferences.Edit()
                ?? throw new InvalidOperationException("Android secure editor is unavailable.");
            if (!editor.PutString(StorageKey(key), Convert.ToBase64String(payload)).Commit())
                throw new IOException("Could not persist an encrypted credential.");
        }
        return Task.CompletedTask;
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var storageKey = StorageKey(key);
            var existed = _preferences.Contains(storageKey);
            var editor = _preferences.Edit()
                ?? throw new InvalidOperationException("Android secure editor is unavailable.");
            if (!editor.Remove(storageKey).Commit())
                throw new IOException("Could not remove an encrypted credential.");
            return existed;
        }
    }

    public void RemoveAll() => throw new NotSupportedException(
        "Bulk removal of encrypted native data is prohibited.");

    private ISecretKey GetOrCreateKey()
    {
        if (_keyStore.GetKey(KeyAlias, null) is { } existing)
            return existing.JavaCast<ISecretKey>();

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
            ?? throw new CryptographicException("Android AES KeyStore is unavailable.");
        using var specification = new KeyGenParameterSpec.Builder(
                KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .Build();
        generator.Init(specification);
        return generator.GenerateKey()
            ?? throw new CryptographicException("Android AES key creation failed.");
    }

    private static string StorageKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

internal sealed class AndroidConnectivity(Context context) : IConnectivityService
{
    public bool IsInternetAvailable
    {
        get
        {
            var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            var network = manager?.ActiveNetwork;
            return network is not null
                && manager!.GetNetworkCapabilities(network)?
                    .HasCapability(NetCapability.Validated) == true;
        }
    }
}

internal sealed class AndroidFiles(Context context) : IFileSystemService
{
    public string AppDataDirectory => context.FilesDir?.AbsolutePath
        ?? throw new DirectoryNotFoundException("Android app-private storage is unavailable.");

    public Task<Stream> OpenAppPackageFileAsync(string filename) =>
        Task.FromResult<Stream>(context.Assets?.Open(filename)
            ?? throw new FileNotFoundException("Android packaged asset is unavailable.", filename));
}
