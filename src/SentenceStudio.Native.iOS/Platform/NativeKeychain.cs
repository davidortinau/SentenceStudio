using Foundation;
using Security;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativeKeychain : ISecureStorageService
{
    private readonly string _service = (NSBundle.MainBundle.BundleIdentifier
        ?? throw new InvalidOperationException("An iOS bundle ID is required for credential isolation"))
        + ".native.auth";

    public Task<string?> GetAsync(string key)
    {
        using var record = Query(key);
        using var match = SecKeyChain.QueryAsRecord(record, out var status);
        return status switch
        {
            SecStatusCode.Success when match?.ValueData is not null =>
                Task.FromResult<string?>(NSString.FromData(match.ValueData, NSStringEncoding.UTF8)),
            SecStatusCode.ItemNotFound => Task.FromResult<string?>(null),
            _ => throw new InvalidOperationException($"Keychain read failed: {status}")
        };
    }

    public async Task SetAsync(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var query = Query(key);
        // Update attributes must omit kSecClass; it belongs only in the search query.
        using var update = new SecRecord()
        {
            ValueData = NSData.FromString(value)
        };
        var status = SecKeyChain.Update(query, update);
        if (status == SecStatusCode.ItemNotFound)
        {
            using var create = Query(key);
            create.ValueData = NSData.FromString(value);
            status = SecKeyChain.Add(create);
        }
        if (status != SecStatusCode.Success
            || !string.Equals(await GetAsync(key), value, StringComparison.Ordinal))
            throw new InvalidOperationException($"Keychain write could not be verified: {status}");
    }

    public bool Remove(string key)
    {
        using var query = Query(key);
        var status = SecKeyChain.Remove(query);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            throw new InvalidOperationException($"Keychain removal failed: {status}");
        return GetAsync(key).GetAwaiter().GetResult() is null;
    }

    public void RemoveAll() =>
        throw new NotSupportedException("Bulk credential deletion is not supported in the native head.");

    private SecRecord Query(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new SecRecord(SecKind.GenericPassword) { Service = _service, Account = key };
    }
}
