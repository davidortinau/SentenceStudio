using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// A device whose older app version synced while the server already tracked a late-joined
/// table has its download position past that table's server rows: CoreSync downloads only the
/// tables the app asks for, then saves the server's latest version as the device's position.
/// The first time the device tracks such a table it asks the server to re-send its own owner's
/// rows of it (POST <see cref="Route"/>), which records them as server changes again.
/// </summary>
public interface ISyncResendRequester
{
    /// <summary>True when the server re-sent (recorded) the owner's rows of the tables.</summary>
    Task<bool> RequestAsync(string owner, IReadOnlyCollection<string> tables,
        CancellationToken cancellationToken);
}

public sealed record SyncResendRequest(string[] Tables);

/// <summary>
/// Tables still waiting for a successful re-send request, kept per owner in preferences so an
/// offline first sync asks again on the next one.
/// </summary>
public sealed class SyncResendQueue(IPreferencesService preferences)
{
    public const string Route = "/api/v1/sync/resend";

    private static string Key(string owner) => $"sync.lateTables.resend.{owner}";

    public IReadOnlyList<string> Pending(string owner) =>
        preferences.Get(Key(owner), string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(SyncLateTables.OwnerColumns.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public void Add(string owner, IEnumerable<string> tables) =>
        Save(owner, Pending(owner).Concat(tables));

    public void Clear(string owner, IEnumerable<string> sent) =>
        Save(owner, Pending(owner).Except(sent, StringComparer.Ordinal));

    private void Save(string owner, IEnumerable<string> tables)
    {
        var value = string.Join(',', tables.Where(SyncLateTables.OwnerColumns.ContainsKey)
            .Distinct(StringComparer.Ordinal));
        if (value.Length == 0)
            preferences.Remove(Key(owner));
        else
            preferences.Set(Key(owner), value);
    }
}
