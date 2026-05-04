# Async Single-Flight Testing Pattern

**Category:** Unit Testing  
**Language:** C# / .NET  
**Test Framework:** xUnit (adaptable to NUnit/MSTest)

## When to Use This Skill

Use this pattern when writing unit tests for services that implement **single-flight** (also called "request coalescing" or "in-flight deduplication") to prevent concurrent duplicate operations. Common scenarios:

- **Auth services** refreshing tokens (prevents concurrent refresh requests)
- **Cache loaders** fetching expensive data (prevents duplicate fetches)
- **API clients** with retry logic (prevents duplicate retries)
- **Database migrations** (prevents concurrent schema changes)

The pattern verifies:
1. Multiple concurrent callers trigger exactly **ONE** backend operation (not N)
2. All callers receive the same result
3. Subsequent calls after the operation completes use cached results (no new operations)

## Pattern Components

### 1. Custom HttpMessageHandler with Request Tracking

```csharp
/// <summary>
/// Tracks every HTTP request and returns canned responses.
/// Use Interlocked.Increment for thread-safe counting.
/// </summary>
private class TrackingHttpMessageHandler : HttpMessageHandler
{
    private readonly object _responseData; // Your canned DTO
    private int _requestCount;

    public int RefreshRequestCount => _requestCount;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, 
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.PathAndQuery.Contains("/api/target") == true)
        {
            Interlocked.Increment(ref _requestCount);
            
            // Simulate network delay to widen the race window
            await Task.Delay(50, cancellationToken);
            
            var json = JsonSerializer.Serialize(_responseData, ...);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
        
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
```

**Key techniques:**
- `Interlocked.Increment(ref _requestCount)` — thread-safe counter
- `await Task.Delay(50)` — widens the race window so concurrent calls overlap
- Return canned JSON response for the target endpoint

### 2. In-Memory Storage Stub

```csharp
/// <summary>
/// Thread-safe in-memory storage for testing.
/// </summary>
private class InMemorySecureStorageService : ISecureStorageService
{
    private readonly Dictionary<string, string> _store = new();
    private readonly object _lock = new();

    public Task<string?> GetAsync(string key)
    {
        lock (_lock)
        {
            _store.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }
    }

    public Task SetAsync(string key, string value)
    {
        lock (_lock)
        {
            _store[key] = value;
        }
        return Task.CompletedTask;
    }

    public bool Remove(string key)
    {
        lock (_lock) => _store.Remove(key);
    }
}
```

**Key techniques:**
- Dictionary-backed storage
- `lock (_lock)` for thread-safety (or use `ConcurrentDictionary`)
- `Task.FromResult` to match async signatures without I/O

### 3. Concurrency Race Simulation

```csharp
[Fact]
public async Task ServiceMethod_ConcurrentCallers_TriggersExactlyOneOperation()
{
    // Arrange
    var handler = new TrackingHttpMessageHandler(cannedResponse);
    var storage = new InMemorySecureStorageService();
    await storage.SetAsync("key", "initial-value");
    
    var service = new TargetService(
        new HttpClient(handler) { BaseAddress = new Uri("https://localhost") },
        storage,
        NullLogger<TargetService>.Instance);

    // Act — fire two concurrent calls
    var call1Task = service.ExpensiveMethodAsync();
    var call2Task = service.ExpensiveMethodAsync();
    var results = await Task.WhenAll(call1Task, call2Task);

    // Assert — both got the same result
    results[0].Should().Be(expectedValue);
    results[1].Should().Be(expectedValue);
    
    // Assert — exactly one backend operation
    handler.RefreshRequestCount.Should().Be(1, 
        "concurrent calls should trigger exactly ONE operation, not two");

    // Act — third call after completion
    var call3Result = await service.ExpensiveMethodAsync();

    // Assert — cached result, no new operation
    call3Result.Should().Be(expectedValue);
    handler.RefreshRequestCount.Should().Be(1, 
        "cached result should NOT trigger another operation");
}
```

**Key techniques:**
- `Task.WhenAll(call1Task, call2Task)` — fires both concurrently
- First assertion: verify both callers got the same result
- Second assertion: verify exactly ONE backend call (`Should().Be(1)`)
- Third call: verify cache hit (counter still `1`)

## Common Pitfalls

1. **Not widening the race window**: Without `Task.Delay` in the handler, both calls may complete before the second one starts → false pass.
2. **Non-thread-safe counters**: Use `Interlocked.Increment(ref _count)`, not `_count++`.
3. **Forgetting to test cache hit**: The third call verifies the fix doesn't break caching.
4. **Using real HttpClient without handler**: You'll hit a real network, and the test will fail unpredictably.

## Expected Test Outcome

- **Without single-flight fix**: Test FAILS with `RefreshRequestCount == 2` (race condition exposed)
- **With single-flight fix** (e.g. `SemaphoreSlim(1,1)` + in-flight task check): Test PASSES with `RefreshRequestCount == 1`

This test is a **regression guard** — once the fix is in place, the test locks it in and will catch future regressions.

## Example: IdentityAuthService Concurrency Test

See `tests/SentenceStudio.AppLib.Tests/IdentityAuthServiceConcurrencyTests.cs` for a real-world implementation:
- Custom `TrackingHttpMessageHandler` tracking POST `/api/auth/refresh`
- In-memory `InMemorySecureStorageService` stub
- Test: `GetAccessTokenAsync_ConcurrentCallers_TriggersExactlyOneRefresh`

The production fix (in `IdentityAuthService.GetAccessTokenAsync`):
```csharp
// Single-flight locking to prevent concurrent refresh races
private readonly SemaphoreSlim _refreshLock = new(1, 1);
private Task<AuthResult?>? _inflightRefresh;

public async Task<string?> GetAccessTokenAsync(string[] scopes)
{
    if (_cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow.AddSeconds(60))
        return _cachedToken;

    await _refreshLock.WaitAsync();
    try
    {
        // Re-check cache after acquiring lock
        if (_cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow.AddSeconds(60))
            return _cachedToken;

        // Check if another caller already started the refresh
        if (_inflightRefresh is not null)
        {
            var result = await _inflightRefresh;
            return result?.AccessToken;
        }

        // Start the refresh and track it
        _inflightRefresh = RefreshTokenAsync(refreshToken);
        var refreshResult = await _inflightRefresh;
        return refreshResult?.AccessToken;
    }
    finally
    {
        _inflightRefresh = null;
        _refreshLock.Release();
    }
}
```

Pattern:
1. Fast path: return cached value if still valid
2. Acquire lock with `SemaphoreSlim`
3. Re-check cache (another caller may have just refreshed)
4. Check for in-flight refresh task (`_inflightRefresh`)
5. If in-flight, await it; otherwise start a new one
6. Release lock in `finally`

## References

- **LazyInitializer pattern** (similar concept): https://learn.microsoft.com/en-us/dotnet/api/system.threading.lazyinitializer
- **AsyncLazy<T>** (Stephen Toub): https://devblogs.microsoft.com/pfxteam/asynclazyt/
- **Request coalescing** in distributed systems: https://aws.amazon.com/builders-library/caching-challenges-and-strategies/
