using System.Collections.Concurrent;
using CoreSync;
using CoreSync.Http;
using Microsoft.Extensions.Caching.Memory;

namespace SentenceStudio.Api.Sync;

internal sealed class NativeSyncUploadSessions : IAsyncDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private const int MaxNativeSessions = 512;

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly SemaphoreSlim _beginGate = new(1, 1);
    private readonly IMemoryCache _coreSyncCache;
    private readonly NativeSyncDownloadSessions _downloads;
    private readonly ILogger<NativeSyncUploadSessions> _logger;
    private readonly TimeProvider _clock;
    private readonly Timer _sweep;

    public NativeSyncUploadSessions(
        IMemoryCache coreSyncCache, NativeSyncDownloadSessions downloads,
        ILogger<NativeSyncUploadSessions> logger)
        : this(coreSyncCache, downloads, logger, TimeProvider.System)
    {
    }

    internal NativeSyncUploadSessions(
        IMemoryCache coreSyncCache, NativeSyncDownloadSessions downloads,
        ILogger<NativeSyncUploadSessions> logger, TimeProvider clock)
    {
        _coreSyncCache = coreSyncCache;
        _downloads = downloads;
        _logger = logger;
        _clock = clock;
        _sweep = new Timer(static state =>
            ((NativeSyncUploadSessions)state!).PruneExpired(),
            this, SweepInterval, SweepInterval);
    }

    internal static ValueTask<object?> GuardBeginAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.RequestServices.GetRequiredService<NativeSyncUploadSessions>()
            .BeginAsync(context, next);

    internal static ValueTask<object?> GuardJsonChunkAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var item = context.GetArgument<BulkChangeSetUploadItem>(0);
        return context.HttpContext.RequestServices.GetRequiredService<NativeSyncUploadSessions>()
            .UseSessionAsync(context, next, item.SessionId, item.Items?.Count, false);
    }

    internal static async ValueTask<object?> GuardBinaryChunkAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var sessions = context.HttpContext.RequestServices
            .GetRequiredService<NativeSyncUploadSessions>();
        var owner = NativeOwnerSyncProvider.GetNativeOwner(context.HttpContext);
        var request = context.HttpContext.Request;
        request.EnableBuffering();
        object? payload;
        try
        {
            payload = await CoreSyncMessagePackSerializer.DeserializeAsync<object>(
                request.Body, context.HttpContext.RequestAborted);
        }
        finally
        {
            request.Body.Position = 0;
        }

        if (payload is not BulkChangeSetUploadItem item)
            return owner is null ? await next(context) : Results.BadRequest();
        return await sessions.UseSessionAsync(
            context, next, item.SessionId, item.Items?.Count, false);
    }

    internal static async ValueTask<object?> GuardCompleteAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var rawId = context.HttpContext.Request.RouteValues["sessionId"]?.ToString();
        if (!Guid.TryParse(rawId, out var sessionId))
            return NativeOwnerSyncProvider.GetNativeOwner(context.HttpContext) is null
                ? await next(context) : Results.NotFound();
        return await context.HttpContext.RequestServices.GetRequiredService<NativeSyncUploadSessions>()
            .UseSessionAsync(context, next, sessionId, null, true);
    }

    private async ValueTask<object?> BeginAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var owner = NativeOwnerSyncProvider.GetNativeOwner(context.HttpContext);
        var changeSet = context.GetArgument<BulkSyncChangeSet>(0);
        var sessionId = changeSet.SessionId;
        await _beginGate.WaitAsync(context.HttpContext.RequestAborted);
        try
        {
            if (_sessions.ContainsKey(sessionId) || _downloads.IsBound(sessionId))
            {
                _logger.LogWarning("Native CoreSync refused a duplicate upload session.");
                return Results.Conflict();
            }
            if (owner is null)
                return await next(context);
            if (sessionId == Guid.Empty || changeSet.SourceAnchor is null
                || changeSet.TotalChanges < 0)
            {
                _logger.LogWarning("Native CoreSync refused an invalid upload begin.");
                return Results.BadRequest();
            }
            if (_coreSyncCache.TryGetValue(sessionId, out _))
                return Results.Conflict();

            PruneExpired();
            if (_sessions.Count >= MaxNativeSessions)
            {
                _logger.LogWarning("Native CoreSync upload session limit reached.");
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            await context.HttpContext.RequestServices
                .GetRequiredService<NativeSyncStoreBindings>()
                .BindAsync(changeSet.SourceAnchor.StoreId, owner,
                    context.HttpContext.RequestAborted);
            var session = new Session(
                owner, changeSet.TotalChanges, _clock.GetUtcNow() + Lifetime);
            await session.Gate.WaitAsync(context.HttpContext.RequestAborted);
            try
            {
                if (!_sessions.TryAdd(sessionId, session))
                    return Results.Conflict();

                var started = false;
                try
                {
                    var result = await next(context);
                    if (!_coreSyncCache.TryGetValue(sessionId, out var staged)
                        || staged is not SyncChangeSet)
                        throw new InvalidOperationException(
                            "CoreSync did not stage a native upload session.");
                    started = true;
                    return result;
                }
                finally
                {
                    if (!started)
                        Remove(sessionId, session);
                }
            }
            finally
            {
                session.Gate.Release();
            }
        }
        finally
        {
            _beginGate.Release();
        }
    }

    private async ValueTask<object?> UseSessionAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next,
        Guid sessionId, int? chunkCount, bool completing)
    {
        var owner = NativeOwnerSyncProvider.GetNativeOwner(context.HttpContext);
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            if (owner is null)
            {
                await _beginGate.WaitAsync(context.HttpContext.RequestAborted);
                try
                {
                    if (_sessions.ContainsKey(sessionId) || _downloads.IsBound(sessionId))
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    return await next(context);
                }
                finally
                {
                    _beginGate.Release();
                }
            }
            _logger.LogWarning("Native CoreSync refused an unbound upload session.");
            return Results.NotFound();
        }

        await session.Gate.WaitAsync(context.HttpContext.RequestAborted);
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var active)
                || !ReferenceEquals(active, session))
                return Results.NotFound();
            if (session.ExpiresAt <= _clock.GetUtcNow()
                || !_coreSyncCache.TryGetValue(sessionId, out var staged)
                || staged is not SyncChangeSet changeSet)
            {
                Remove(sessionId, session);
                _logger.LogWarning("Native CoreSync refused an expired upload session.");
                return Results.NotFound();
            }
            if (owner != session.Owner)
            {
                _logger.LogWarning("Native CoreSync refused an upload session from another owner.");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (chunkCount is null && !completing
                || chunkCount.HasValue
                    && (long)changeSet.Items.Count + chunkCount.Value > session.TotalChanges
                || completing && changeSet.Items.Count != session.TotalChanges)
            {
                _logger.LogWarning("Native CoreSync refused an incomplete or oversized upload.");
                return Results.BadRequest();
            }

            session.ExpiresAt = _clock.GetUtcNow() + Lifetime;
            var result = await next(context);
            if (completing)
            {
                if (result is not SyncAnchor)
                    throw new InvalidOperationException(
                        "CoreSync did not complete the native upload session.");
                Remove(sessionId, session);
            }
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
        _coreSyncCache.Remove(sessionId);
        _sessions.TryRemove(new KeyValuePair<Guid, Session>(sessionId, session));
    }

    public async ValueTask DisposeAsync()
    {
        await _sweep.DisposeAsync();
        _beginGate.Dispose();
    }

    private sealed class Session(string owner, int totalChanges, DateTimeOffset expiresAt)
    {
        internal string Owner { get; } = owner;
        internal int TotalChanges { get; } = totalChanges;
        internal DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
