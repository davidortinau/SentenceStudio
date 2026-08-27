using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Replays the response of a coach write that the client already submitted.
/// </summary>
/// <remarks>
/// <para>
/// A retried turn, accept, reject, or undo must not apply a second plan revision. The
/// deterministic planner makes a repeated apply a no-op (the plan hash already matches), but
/// a retry would still burn a run and could return a confusing "nothing changed" to a client
/// that never saw the first answer. Replaying the stored response keeps the client's view
/// correct and the write count at one.
/// </para>
/// <para>
/// Process-local and bounded, like the Stage 1 budget service. It is a convenience layer over
/// an already-idempotent write path, not the safety mechanism — the plan-version check and the
/// hash comparison in <c>PlanService</c> are.
/// </para>
/// </remarks>
public sealed class CoachTurnIdempotencyStore
{
    /// <summary>How long a completed turn can be replayed.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    /// <summary>Hard cap so a hostile client cannot grow the map without bound.</summary>
    public const int MaxEntries = 2_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public CoachTurnIdempotencyStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Returns a previously stored response for this client turn id, if any.</summary>
    public bool TryGet(string userProfileId, string sessionId, string? clientTurnId, out CoachTurnResponse response)
    {
        response = null!;
        if (string.IsNullOrWhiteSpace(clientTurnId))
        {
            return false;
        }

        var key = Key(userProfileId, sessionId, clientTurnId);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        response = entry.Response;
        return true;
    }

    /// <summary>Stores the response produced for this client turn id.</summary>
    public void Store(string userProfileId, string sessionId, string? clientTurnId, CoachTurnResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrWhiteSpace(clientTurnId))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        Prune(now);

        _entries[Key(userProfileId, sessionId, clientTurnId)] = new Entry(response, now + Retention);
    }

    /// <summary>Forgets everything stored for one session (used when the session is deleted).</summary>
    public void Clear(string userProfileId, string sessionId)
    {
        var prefix = SessionPrefix(userProfileId, sessionId);
        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count < MaxEntries)
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    _entries.TryRemove(pair.Key, out _);
                }
            }

            return;
        }

        // Over the cap: drop everything expired first, then the oldest remaining entries.
        foreach (var pair in _entries.OrderBy(p => p.Value.ExpiresAt).Take(_entries.Count - MaxEntries + 1))
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    private static string SessionPrefix(string userProfileId, string sessionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userProfileId}\u001f{sessionId}"))) + ":";

    private static string Key(string userProfileId, string sessionId, string clientTurnId) =>
        SessionPrefix(userProfileId, sessionId)
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientTurnId)));

    private sealed record Entry(CoachTurnResponse Response, DateTimeOffset ExpiresAt);
}
