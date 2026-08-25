using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Tracks the in-flight coach run for each owned session so a Stop action can cancel it.
/// </summary>
/// <remarks>
/// Process-local by design, exactly like <c>InMemoryCoachBudgetService</c>: with more than one
/// API replica a Stop only reaches the replica running the turn. Both must move to a shared
/// store before the coach runs on multiple instances. Keys are hashed so a user profile id
/// never sits in a long-lived in-memory dictionary key.
/// </remarks>
public sealed class CoachRunRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a run and returns a token source linked to the request token. Disposing the
    /// returned registration unregisters the run.
    /// </summary>
    public CoachRunRegistration Register(string userProfileId, string sessionId, CancellationToken requestToken)
    {
        var key = Key(userProfileId, sessionId);
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestToken);

        // A second concurrent run for the same session is refused upstream by the budget
        // lease; if one still slips through, the newest registration wins and the older run
        // is cancelled rather than orphaned.
        _runs.AddOrUpdate(
            key,
            source,
            (_, existing) =>
            {
                TryCancel(existing);
                return source;
            });

        return new CoachRunRegistration(this, key, source);
    }

    /// <summary>Cancels the in-flight run for an owned session. Returns false when none is running.</summary>
    public bool Cancel(string userProfileId, string sessionId)
    {
        if (!_runs.TryGetValue(Key(userProfileId, sessionId), out var source))
        {
            return false;
        }

        return TryCancel(source);
    }

    /// <summary>True when a run is currently registered for the owned session.</summary>
    public bool IsRunning(string userProfileId, string sessionId) =>
        _runs.ContainsKey(Key(userProfileId, sessionId));

    internal void Release(string key, CancellationTokenSource source)
    {
        // Only remove our own registration; a newer run may already have replaced it.
        if (_runs.TryGetValue(key, out var current) && ReferenceEquals(current, source))
        {
            _runs.TryRemove(key, out _);
        }
    }

    private static bool TryCancel(CancellationTokenSource source)
    {
        try
        {
            if (source.IsCancellationRequested)
            {
                return false;
            }

            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static string Key(string userProfileId, string sessionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userProfileId}\u001f{sessionId}")));
}

/// <summary>Unregisters a coach run when disposed.</summary>
public sealed class CoachRunRegistration : IDisposable
{
    private readonly CoachRunRegistry _registry;
    private readonly string _key;
    private readonly CancellationTokenSource _source;
    private bool _disposed;

    internal CoachRunRegistration(CoachRunRegistry registry, string key, CancellationTokenSource source)
    {
        _registry = registry;
        _key = key;
        _source = source;
    }

    /// <summary>The token the turn must observe. Cancels on request abort or on Stop.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>True when the run was stopped rather than completing on its own.</summary>
    public bool IsCancelled => _source.IsCancellationRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Release(_key, _source);
        _source.Dispose();
    }
}
