using System.Security.Claims;
using CoreSync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Sync;
using SentenceStudio.Contracts;

namespace SentenceStudio.Api.Tests.Sync;

public sealed class NativeSyncDownloadSessionsTests
{
    private const string Owner = "11111111-1111-1111-1111-111111111111";
    private const string Foreign = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task AuthorizedJsonAndBinaryChunks_RenewIdleLeaseBeyondTwentyMinutes()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ControlledTimeProvider();
        await using var sessions = new NativeSyncDownloadSessions(
            cache, NullLogger<NativeSyncDownloadSessions>.Instance, clock);
        using var services = new ServiceCollection().AddSingleton(sessions).BuildServiceProvider();
        var sessionId = Guid.NewGuid();
        var json = Array.Empty<SyncItem>();
        var binary = Results.File(new byte[] { 1 }, "application/x-msgpack");
        EndpointFilterDelegate jsonChunk = _ => ValueTask.FromResult<object?>(json);
        EndpointFilterDelegate binaryChunk = _ => ValueTask.FromResult<object?>(binary);
        cache.Set(sessionId, new object());
        sessions.Bind(sessionId, Owner, 5);

        clock.Advance(TimeSpan.FromMinutes(19));
        Assert.Same(json, await GetChunkAsync(services, sessionId, Owner, jsonChunk));

        clock.Advance(TimeSpan.FromMinutes(19));
        sessions.PruneExpired();
        Assert.Same(binary, await GetChunkAsync(
            services, sessionId, Owner, binaryChunk, skip: "1"));

        clock.Advance(TimeSpan.FromMinutes(19));
        sessions.PruneExpired();
        Assert.Same(json, await GetChunkAsync(
            services, sessionId, Owner, jsonChunk, skip: "2"));

        clock.Advance(TimeSpan.FromMinutes(19));
        sessions.PruneExpired();
        Assert.True(sessions.IsBound(sessionId));
        Assert.True(cache.TryGetValue(sessionId, out _));
        Assert.Same(json, await GetChunkAsync(
            services, sessionId, Owner, jsonChunk, skip: "3"));
    }

    [Fact]
    public async Task IdleLongerThanTwentyMinutes_RemovesCoreSyncRowsAndOwnerBinding()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ControlledTimeProvider();
        await using var sessions = new NativeSyncDownloadSessions(
            cache, NullLogger<NativeSyncDownloadSessions>.Instance, clock);
        using var services = new ServiceCollection().AddSingleton(sessions).BuildServiceProvider();
        var sessionId = Guid.NewGuid();
        cache.Set(sessionId, new object());
        sessions.Bind(sessionId, Owner, 5);
        EndpointFilterDelegate jsonChunk = _ => ValueTask.FromResult<object?>(Array.Empty<SyncItem>());

        clock.Advance(TimeSpan.FromMinutes(21));
        AssertStatus(StatusCodes.Status404NotFound,
            await GetChunkAsync(services, sessionId, Owner, jsonChunk));
        Assert.False(cache.TryGetValue(sessionId, out _));
        Assert.False(sessions.IsBound(sessionId));
        AssertStatus(StatusCodes.Status404NotFound,
            await GetChunkAsync(services, sessionId, Owner, jsonChunk));
    }

    [Fact]
    public async Task ForeignHeaderlessInvalidAndFailedChunks_DoNotRenewLease()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ControlledTimeProvider();
        await using var sessions = new NativeSyncDownloadSessions(
            cache, NullLogger<NativeSyncDownloadSessions>.Instance, clock);
        using var services = new ServiceCollection().AddSingleton(sessions).BuildServiceProvider();
        var sessionId = Guid.NewGuid();
        cache.Set(sessionId, new object());
        sessions.Bind(sessionId, Owner, 5);
        var calls = 0;
        EndpointFilterDelegate jsonChunk = _ =>
        {
            calls++;
            return ValueTask.FromResult<object?>(Array.Empty<SyncItem>());
        };

        clock.Advance(TimeSpan.FromMinutes(19));
        AssertStatus(StatusCodes.Status404NotFound,
            await GetChunkAsync(services, sessionId, Foreign, jsonChunk));
        AssertStatus(StatusCodes.Status404NotFound,
            await GetChunkAsync(services, sessionId, Owner, jsonChunk, nativeHeader: false));
        AssertStatus(StatusCodes.Status404NotFound,
            await GetChunkAsync(services, sessionId, Owner, jsonChunk, routeId: "not-a-guid"));
        AssertStatus(StatusCodes.Status400BadRequest,
            await GetChunkAsync(services, sessionId, Owner, jsonChunk, skip: "-1"));
        AssertStatus(StatusCodes.Status400BadRequest,
            await GetChunkAsync(services, sessionId, Owner, jsonChunk, take: "0"));
        AssertStatus(StatusCodes.Status400BadRequest,
            await GetChunkAsync(services, sessionId, Owner, jsonChunk, skip: "5"));
        Assert.Equal(0, calls);

        EndpointFilterDelegate failedChunk = _ => ValueTask.FromResult<object?>(
            Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        AssertStatus(StatusCodes.Status503ServiceUnavailable,
            await GetChunkAsync(services, sessionId, Owner, failedChunk));

        clock.Advance(TimeSpan.FromMinutes(2));
        sessions.PruneExpired();
        Assert.False(cache.TryGetValue(sessionId, out _));
        Assert.False(sessions.IsBound(sessionId));
    }

    [Fact]
    public async Task FinalChunk_RemovesCacheAndBinding()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ControlledTimeProvider();
        await using var sessions = new NativeSyncDownloadSessions(
            cache, NullLogger<NativeSyncDownloadSessions>.Instance, clock);
        using var services = new ServiceCollection().AddSingleton(sessions).BuildServiceProvider();
        var sessionId = Guid.NewGuid();
        cache.Set(sessionId, new object());
        sessions.Bind(sessionId, Owner, 5);
        var json = Array.Empty<SyncItem>();
        EndpointFilterDelegate finalChunk = _ => ValueTask.FromResult<object?>(json);

        Assert.Same(json, await GetChunkAsync(
            services, sessionId, Owner, finalChunk, skip: "4", take: "1000"));
        Assert.False(cache.TryGetValue(sessionId, out _));
        Assert.False(sessions.IsBound(sessionId));
        AssertStatus(StatusCodes.Status404NotFound,
            await GetChunkAsync(services, sessionId, Owner, finalChunk));
    }

    [Fact]
    public async Task InFlightChunk_SurvivesSweepAndRenewsAfterSuccess()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ControlledTimeProvider();
        await using var sessions = new NativeSyncDownloadSessions(
            cache, NullLogger<NativeSyncDownloadSessions>.Instance, clock);
        using var services = new ServiceCollection().AddSingleton(sessions).BuildServiceProvider();
        var sessionId = Guid.NewGuid();
        cache.Set(sessionId, new object());
        sessions.Bind(sessionId, Owner, 5);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var json = Array.Empty<SyncItem>();

        async ValueTask<object?> WaitForChunk(EndpointFilterInvocationContext _)
        {
            entered.SetResult();
            await release.Task;
            return json;
        }

        var pending = GetChunkAsync(services, sessionId, Owner, WaitForChunk).AsTask();
        await entered.Task;
        try
        {
            clock.Advance(TimeSpan.FromMinutes(21));
            sessions.PruneExpired();
            Assert.True(cache.TryGetValue(sessionId, out _));
            Assert.True(sessions.IsBound(sessionId));
        }
        finally
        {
            release.SetResult();
        }

        Assert.Same(json, await pending);
        clock.Advance(TimeSpan.FromMinutes(19));
        sessions.PruneExpired();
        Assert.True(sessions.IsBound(sessionId));
        Assert.True(cache.TryGetValue(sessionId, out _));
    }

    [Fact]
    public async Task WaitingChunk_CannotRenewSessionAfterAnotherChunkCompletesIt()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ControlledTimeProvider();
        await using var sessions = new NativeSyncDownloadSessions(
            cache, NullLogger<NativeSyncDownloadSessions>.Instance, clock);
        using var services = new ServiceCollection().AddSingleton(sessions).BuildServiceProvider();
        var sessionId = Guid.NewGuid();
        cache.Set(sessionId, new object());
        sessions.Bind(sessionId, Owner, 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var json = Array.Empty<SyncItem>();

        async ValueTask<object?> WaitForFinalChunk(EndpointFilterInvocationContext _)
        {
            entered.SetResult();
            await release.Task;
            return json;
        }

        var completing = GetChunkAsync(
            services, sessionId, Owner, WaitForFinalChunk, skip: "1").AsTask();
        await entered.Task;
        var waitingHandlerCalled = false;
        EndpointFilterDelegate waitingChunk = _ =>
        {
            waitingHandlerCalled = true;
            return ValueTask.FromResult<object?>(json);
        };
        var waiting = GetChunkAsync(services, sessionId, Owner, waitingChunk).AsTask();
        try
        {
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            release.SetResult();
        }

        Assert.Same(json, await completing);
        AssertStatus(StatusCodes.Status404NotFound, await waiting);
        Assert.False(waitingHandlerCalled);
        Assert.False(cache.TryGetValue(sessionId, out _));
        Assert.False(sessions.IsBound(sessionId));
    }

    [Fact]
    public async Task Sweep_ExpiresIdleNativeSessions_WithoutRemovingLegacySessions()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ControlledTimeProvider();
        await using var sessions = new NativeSyncDownloadSessions(
            cache, NullLogger<NativeSyncDownloadSessions>.Instance, clock);
        var nativeId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        cache.Set(nativeId, new object());
        cache.Set(legacyId, new object());
        sessions.Bind(nativeId, Owner, 1);

        clock.Advance(TimeSpan.FromMinutes(21));
        sessions.PruneExpired();

        Assert.False(cache.TryGetValue(nativeId, out _));
        Assert.False(sessions.IsBound(nativeId));
        Assert.True(cache.TryGetValue(legacyId, out _));
        using var services = new ServiceCollection().AddSingleton(sessions).BuildServiceProvider();
        var json = Array.Empty<SyncItem>();
        EndpointFilterDelegate legacyChunk = _ => ValueTask.FromResult<object?>(json);
        Assert.Same(json, await GetChunkAsync(
            services, legacyId, Owner, legacyChunk, nativeHeader: false));
        AssertStatus(StatusCodes.Status404NotFound,
            await GetChunkAsync(services, legacyId, Owner, legacyChunk));
    }

    private static ValueTask<object?> GetChunkAsync(
        IServiceProvider services, Guid sessionId, string? owner, EndpointFilterDelegate next,
        string skip = "0", string take = "1", bool nativeHeader = true, string? routeId = null)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        if (owner is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(AuthClaimTypes.UserProfileId, owner) }, "Bearer"));
            http.Request.Headers.Authorization = "Bearer test";
            if (nativeHeader)
                http.Request.Headers[SharedSyncRegistration.NativeOwnerHeader] = owner;
        }
        http.Request.RouteValues["sessionId"] = routeId ?? sessionId.ToString();
        http.Request.RouteValues["skip"] = skip;
        http.Request.RouteValues["take"] = take;
        return NativeSyncDownloadSessions.GuardChunkAsync(
            EndpointFilterInvocationContext.Create(http), next);
    }

    private static void AssertStatus(int status, object? response) =>
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
