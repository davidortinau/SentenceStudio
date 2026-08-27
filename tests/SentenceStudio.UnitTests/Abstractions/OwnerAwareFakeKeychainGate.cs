using System;
using System.Collections.Generic;
using System.Text;
using SentenceStudio.Abstractions.Keychain;

namespace SentenceStudio.UnitTests.Abstractions;

/// <summary>Who owns a keychain item, from this app's point of view.</summary>
public enum KeychainItemOwner
{
    /// <summary>Written by the running code signature. Readable and deletable.</summary>
    ThisApp = 0,

    /// <summary>
    /// Written by a different application (or a previous, differently-signed build). Its ACL grants
    /// data access only to the creating signature.
    /// </summary>
    Foreign = 1,
}

/// <summary>
/// A fake <see cref="IKeychainGate"/> whose ACL semantics match what the real macOS legacy keychain
/// was measured to do.
/// </summary>
/// <remarks>
/// <para>
/// The asymmetry is the whole point, and it is what makes the cross-app deletion bug possible:
/// </para>
/// <list type="bullet">
/// <item><b>Read</b> of a foreign item fails with <c>errSecAuthFailed</c> (-25293) once the
/// SecurityAgent prompt is suppressed — measured against a real login keychain with two ad-hoc
/// signed binaries.</item>
/// <item><b>Delete</b> of a foreign item <b>succeeds</b>. Legacy generic-password items carry no
/// <c>ACLAuthorizationDelete</c> entry, so <c>SecKeychainFindGenericPassword</c> +
/// <c>SecKeychainItemDelete</c> removes another application's credential without any prompt —
/// also measured.</item>
/// <item><b>Write</b> of a foreign item succeeds too: <c>ACLAuthorizationEncrypt</c> is granted to
/// any application.</item>
/// </list>
/// <para>
/// A fake that simply refused every foreign operation would make the dangerous tests pass for the
/// wrong reason. This one lets the app do the damage, so a test can prove it does not.
/// </para>
/// </remarks>
public class OwnerAwareFakeKeychainGate : IKeychainGate
{
    private sealed record Item(byte[] Data, KeychainItemOwner Owner);

    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public bool InteractionAllowed { get; private set; } = true;

    public bool CanSetInteraction { get; set; } = true;

    /// <summary>Every account name passed to <see cref="Delete"/>, in order.</summary>
    public List<string> DeleteAttempts { get; } = new();

    /// <summary>Every account name passed to <see cref="Write"/>, in order.</summary>
    public List<string> WriteAttempts { get; } = new();

    /// <summary>Every account name passed to <see cref="Read"/>, in order.</summary>
    public List<string> ReadAttempts { get; } = new();

    public void Seed(string account, string value, KeychainItemOwner owner) =>
        _items[account] = new Item(Encoding.UTF8.GetBytes(value), owner);

    public void SeedRaw(string account, byte[] data, KeychainItemOwner owner) =>
        _items[account] = new Item(data, owner);

    public bool Contains(string account) => _items.ContainsKey(account);

    public string? ValueOf(string account) =>
        _items.TryGetValue(account, out var item) ? Encoding.UTF8.GetString(item.Data) : null;

    public KeychainItemOwner? OwnerOf(string account) =>
        _items.TryGetValue(account, out var item) ? item.Owner : null;

    public IReadOnlyCollection<string> Accounts => _items.Keys;

    public bool SetUserInteractionAllowed(bool allowed)
    {
        if (!CanSetInteraction)
            return false;

        InteractionAllowed = allowed;
        return true;
    }

    public bool? GetUserInteractionAllowed() => InteractionAllowed;

    public KeychainReadResult Read(string account)
    {
        ReadAttempts.Add(account);

        if (!_items.TryGetValue(account, out var item))
            return KeychainReadResult.Status(KeychainStatus.ItemNotFound);

        if (item.Owner == KeychainItemOwner.Foreign)
        {
            // Measured: with the prompt suppressed the legacy keychain answers errSecAuthFailed.
            return KeychainReadResult.Status(KeychainStatus.AuthFailed);
        }

        return new KeychainReadResult(KeychainStatus.Success, item.Data);
    }

    public virtual int Write(string account, byte[] data)
    {
        WriteAttempts.Add(account);

        // Writing succeeds regardless of owner — Encrypt is granted to any app — and the rewritten
        // item becomes ours, exactly as delete-then-add does on the real gate.
        _items[account] = new Item(data, KeychainItemOwner.ThisApp);
        return KeychainStatus.Success;
    }

    public int Delete(string account)
    {
        DeleteAttempts.Add(account);

        // Succeeds for foreign items too. That is the hazard under test.
        return _items.Remove(account) ? KeychainStatus.Success : KeychainStatus.ItemNotFound;
    }
}

/// <summary>In-memory <see cref="ILegacyAdoptionJournal"/> that survives "relaunch" in a test.</summary>
public sealed class FakeAdoptionJournal : ILegacyAdoptionJournal
{
    private readonly Dictionary<string, LegacyAdoptionOutcome> _entries = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public LegacyAdoptionOutcome Read(string groupId) =>
        _entries.TryGetValue(groupId, out var v) ? v : LegacyAdoptionOutcome.Undecided;

    public void Record(string groupId, LegacyAdoptionOutcome outcome)
    {
        Writes++;
        _entries[groupId] = outcome;
    }
}

/// <summary>Minimal in-memory preferences for adoption tests.</summary>
public sealed class FakePreferences : SentenceStudio.Abstractions.IPreferencesService
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public T Get<T>(string key, T defaultValue) =>
        _values.TryGetValue(key, out var v) && v is T typed ? typed : defaultValue;

    public void Set<T>(string key, T value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);

    public void Clear() => _values.Clear();
}
