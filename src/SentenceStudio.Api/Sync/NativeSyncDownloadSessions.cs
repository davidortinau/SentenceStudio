using System.Collections.Concurrent;
using CoreSync.Http;
using Microsoft.Extensions.Caching.Memory;

namespace SentenceStudio.Api.Sync;

internal sealed class NativeSyncDownloadSessions : IAsyncDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly IMemoryCache _coreSyncCache;
    private readonly ILogger<NativeSyncDownloadSessions> _logger;
    private readonly TimeProvider _clock;
    private readonly Timer _sweep;

    public NativeSyncDownloadSessions(
        IMemoryCache coreSyncCache, ILogger<NativeSyncDownloadSessions> logger)
        : this(coreSyncCache, logger, TimeProvider.System)
    {
    }

    internal NativeSyncDownloadSessions(
        IMemoryCache coreSyncCache, ILogger<NativeSyncDownloadSessions> logger, TimeProvider clock)
    {
        _coreSyncCache = coreSyncCache;
        _logger = logger;
        _clock = clock;
        _sweep = new Timer(static state =>
            ((NativeSyncDownloadSessions)state!).PruneExpired(),
            this, SweepInterval, SweepInterval);
    }

    internal static async ValueTask<object?> BindDownloadAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var owner = NativeOwnerSyncProvider.GetNativeOwner(context.HttpContext);
        var result = await next(context);
        if (owner is not null)
        {
            if (result is not BulkSyncChangeSet session)
                throw new InvalidOperationException("CoreSync did not return a bulk download session.");

            context.HttpContext.RequestServices.GetRequiredService<NativeSyncDownloadSessions>()
                .Bind(session.SessionId, owner, session.TotalChanges);
        }

        return result;
    }

    internal static async ValueTask<object?> GuardChunkAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var owner = NativeOwnerSyncProvider.GetNativeOwner(context.HttpContext);
        var rawId = context.HttpContext.Request.RouteValues["sessionId"]?.ToString();
        if (!Guid.TryParse(rawId, out var sessionId))
            return owner is null ? await next(context) : Results.NotFound();

        var sessions = context.HttpContext.RequestServices
            .GetRequiredService<NativeSyncDownloadSessions>();
        return await sessions.UseSessionAsync(context, next, sessionId, owner);
    }

    internal void Bind(Guid sessionId, string owner, int totalChanges)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(owner)
            || !_sessions.TryAdd(sessionId,
                new Session(owner, totalChanges, _clock.GetUtcNow() + Lifetime)))
            throw new InvalidOperationException("Could not bind a native CoreSync download session.");
    }

    internal bool IsBound(Guid sessionId) => _sessions.ContainsKey(sessionId);

    private async ValueTask<object?> UseSessionAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next,
        Guid sessionId, string? owner)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            if (owner is null)
                return await next(context);

            _logger.LogWarning("Native CoreSync chunk refused an unbound session.");
            return Results.NotFound();
        }

        await session.Gate.WaitAsync(context.HttpContext.RequestAborted);
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var active)
                || !ReferenceEquals(active, session))
                return Results.NotFound();

            if (session.ExpiresAt <= _clock.GetUtcNow()
                || !_coreSyncCache.TryGetValue(sessionId, out _))
            {
                Remove(sessionId, session);
                _logger.LogWarning("Native CoreSync chunk refused an expired session.");
                return Results.NotFound();
            }

            if (owner != session.Owner)
            {
                _logger.LogWarning("Native CoreSync chunk refused a different authenticated owner.");
                return Results.NotFound();
            }

            if (!int.TryParse(context.HttpContext.Request.RouteValues["skip"]?.ToString(), out var skip)
                || !int.TryParse(context.HttpContext.Request.RouteValues["take"]?.ToString(), out var take)
                || skip < 0 || take <= 0 || (long)skip + take > int.MaxValue
                || (session.TotalChanges == 0 ? skip != 0 : skip >= session.TotalChanges))
                return Results.BadRequest();

            var result = await next(context);
            if (result is null
                || result is IStatusCodeHttpResult { StatusCode: < 200 or >= 300 }
                || context.HttpContext.Response.StatusCode is < 200 or >= 300)
                return result;

            if ((long)skip + take >= session.TotalChanges)
                Remove(sessionId, session);
            else
                session.ExpiresAt = _clock.GetUtcNow() + Lifetime;

            return result;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    internal void PruneExpired()
    {
        foreach (var pair in _sessions)
        {
            if (!pair.Value.Gate.Wait(0))
                continue;
            try
            {
                if (pair.Value.ExpiresAt <= _clock.GetUtcNow())
                    Remove(pair.Key, pair.Value);
            }
            finally
            {
                pair.Value.Gate.Release();
            }
        }
    }

    private void Remove(Guid sessionId, Session session)
    {
        if (!_sessions.TryGetValue(sessionId, out var active)
            || !ReferenceEquals(active, session))
            return;
        // Remove CoreSync's cached rows first; otherwise a headerless request could
        // observe them after the ownership binding disappears.
        _coreSyncCache.Remove(sessionId);
        _sessions.TryRemove(new KeyValuePair<Guid, Session>(sessionId, session));
    }

    public async ValueTask DisposeAsync() => await _sweep.DisposeAsync();

    private sealed class Session(string owner, int totalChanges, DateTimeOffset expiresAt)
    {
        internal string Owner { get; } = owner;
        internal int TotalChanges { get; } = totalChanges;
        internal DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
