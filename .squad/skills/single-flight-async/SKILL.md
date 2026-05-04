# Single-Flight Async Pattern

**Purpose:** Collapse multiple concurrent async callers to a single shared operation.

**When to Use:**
- Service methods where concurrent calls should share one network request (token refresh, config fetch, feature flag load)
- Expensive async operations that should not be duplicated (database migrations, file downloads, cache warming)
- Any scenario where "exactly one in-flight operation" is a correctness requirement

**When NOT to Use:**
- Operations that must be independent per caller (unique per-request data)
- Fire-and-forget operations that don't return results
- Operations where concurrency is desired (parallel batch processing)

## Pattern

Use `SemaphoreSlim(1, 1)` for mutual exclusion + a cached `Task<T>?` to share the in-flight operation:

```csharp
public class MyService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Task<MyResult?>? _inflightOperation;

    public async Task<MyResult?> GetOrFetchAsync()
    {
        // Fast path: check cache OUTSIDE the lock (read-only, no race)
        if (CacheIsStillValid())
            return _cachedResult;

        // Slow path: need to fetch. Take the lock.
        await _lock.WaitAsync();
        try
        {
            // Re-check cache AFTER acquiring lock — another caller may have just refreshed
            if (CacheIsStillValid())
                return _cachedResult;

            // Check if an operation is already in-flight
            if (_inflightOperation is not null)
            {
                _logger.LogInformation("Operation already in-flight, awaiting existing task");
                return await _inflightOperation;
            }

            // Start the operation and cache the Task
            _inflightOperation = PerformExpensiveOperationAsync();
            var result = await _inflightOperation;

            // Update cache with result
            _cachedResult = result;
            _cachedExpiry = DateTimeOffset.UtcNow.AddMinutes(30);

            return result;
        }
        finally
        {
            // Clear the in-flight Task and release the lock
            _inflightOperation = null;
            _lock.Release();
        }
    }

    private async Task<MyResult?> PerformExpensiveOperationAsync()
    {
        // The actual expensive operation (network call, computation, etc.)
        // ...
    }
}
```

## Key Points

1. **Lock-Check-Start-Await-Finally-Null:**
   - `WaitAsync()` — take mutual exclusion
   - Re-check cache (another caller may have just finished)
   - Check `_inflightOperation is not null` (already running?)
   - If not running, start operation and cache the `Task<T>`
   - `await` the Task (whether we started it or found it in-flight)
   - `finally { _inflightOperation = null; _lock.Release(); }`

2. **Fast-path cache check OUTSIDE the lock:**
   - Avoids lock contention when the cache is valid
   - Safe because it's a read-only check

3. **Exceptions propagate to all waiters:**
   - If the in-flight Task throws, all concurrent callers see the same exception
   - This is correct — they would have all hit the same failure independently anyway

4. **Don't hold the lock across the await:**
   - We DO hold the lock across the await in this pattern
   - Acceptable because:
     - The lock is only held while the operation is truly in-flight
     - Concurrent callers wait for the result, not blocked on entering the critical section
     - Alternative (release lock, reacquire after await) is more complex and error-prone

5. **Consider using AsyncLock for cleaner syntax:**
   - Nito.AsyncEx provides `AsyncLock` which can be cleaner
   - But `SemaphoreSlim` is framework-provided and sufficient for this pattern

## Variations

### Without Cache (Pure Single-Flight)

If there's no cache, just the in-flight deduplication:

```csharp
private readonly SemaphoreSlim _lock = new(1, 1);
private Task<MyResult?>? _inflightOperation;

public async Task<MyResult?> DoOnceAsync()
{
    await _lock.WaitAsync();
    try
    {
        if (_inflightOperation is not null)
            return await _inflightOperation;

        _inflightOperation = PerformOperationAsync();
        return await _inflightOperation;
    }
    finally
    {
        _inflightOperation = null;
        _lock.Release();
    }
}
```

### With Multiple Entry Points

If multiple methods can trigger the same operation (like `SignInAsync()` and `GetAccessTokenAsync()` both triggering refresh):

```csharp
private readonly SemaphoreSlim _refreshLock = new(1, 1);
private Task<AuthResult?>? _inflightRefresh;

public async Task<AuthResult?> SignInAsync()
{
    // Check cache, then delegate to shared refresh logic
    if (CacheValid())
        return _cachedResult;

    var refreshToken = await _secureStorage.GetAsync(RefreshKey);
    if (string.IsNullOrEmpty(refreshToken))
        return null;

    // Call shared single-flight method
    return await RefreshWithLockAsync(refreshToken);
}

public async Task<string?> GetAccessTokenAsync()
{
    if (CacheValid())
        return _cachedToken;

    var refreshToken = await _secureStorage.GetAsync(RefreshKey);
    if (string.IsNullOrEmpty(refreshToken))
        return null;

    // Call shared single-flight method
    var result = await RefreshWithLockAsync(refreshToken);
    return result?.AccessToken;
}

private async Task<AuthResult?> RefreshWithLockAsync(string refreshToken)
{
    await _refreshLock.WaitAsync();
    try
    {
        // Re-check cache after lock
        if (CacheValid())
            return _cachedResult;

        if (_inflightRefresh is not null)
        {
            _logger.LogInformation("Refresh already in-flight");
            return await _inflightRefresh;
        }

        _inflightRefresh = RefreshTokenAsync(refreshToken);
        return await _inflightRefresh;
    }
    finally
    {
        _inflightRefresh = null;
        _refreshLock.Release();
    }
}
```

## Common Mistakes

❌ **Releasing the semaphore in `finally` without tracking acquisition:**

If `WaitAsync()` throws (e.g. the operation was cancelled before the lock was acquired), the
`finally` block still runs and `_lock.Release()` will throw `SemaphoreFullException` —
masking the original exception and corrupting the semaphore count.

```csharp
// BAD: Release runs even if WaitAsync threw
await _lock.WaitAsync(ct);
try { ... }
finally
{
    _inflightOperation = null;
    _lock.Release();   // 💥 SemaphoreFullException if WaitAsync threw
}
```

✅ **Use a `lockAcquired` guard:**

```csharp
bool lockAcquired = false;
try
{
    await _lock.WaitAsync(ct);
    lockAcquired = true;
    // ... critical section ...
}
finally
{
    _inflightOperation = null;
    if (lockAcquired)
        _lock.Release();
}
```

This pattern was added to `IdentityAuthService` after a code review caught it as a
High-severity finding. Apply it everywhere you have `WaitAsync` + `finally { Release() }`.

❌ **Forgetting to null out `_inflightOperation` in finally:**
```csharp
// BAD: next caller will await a completed Task forever
_inflightOperation = DoWorkAsync();
return await _inflightOperation;
// Missing: _inflightOperation = null in finally
```

❌ **Checking `_inflightOperation` outside the lock:**
```csharp
// BAD: race between check and lock acquisition
if (_inflightOperation is not null)
    return await _inflightOperation;

await _lock.WaitAsync();
// Another caller may have started between the check and the lock
```

❌ **Not re-checking cache after acquiring lock:**
```csharp
await _lock.WaitAsync();
try
{
    // BAD: another caller may have just refreshed the cache
    _inflightOperation = ExpensiveAsync();
    return await _inflightOperation;
}
```

✅ **Correct pattern always:**
1. Lock
2. Re-check cache
3. Check in-flight
4. Start if needed
5. Await
6. Finally: null + release

## Real-World Example

From `IdentityAuthService.cs`:

```csharp
private readonly SemaphoreSlim _refreshLock = new(1, 1);
private Task<AuthResult?>? _inflightRefresh;

public async Task<string?> GetAccessTokenAsync(string[] scopes)
{
    // Fast path: cached token still valid (outside lock)
    if (_cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow.AddSeconds(60))
        return _cachedToken;

    // Need refresh
    var refreshToken = await _secureStorage.GetAsync(RefreshKey);
    if (string.IsNullOrEmpty(refreshToken))
        return null;

    // Single-flight: collapse concurrent callers
    await _refreshLock.WaitAsync();
    try
    {
        // Re-check cache after lock
        if (_cachedToken is not null && _cachedExpires > DateTimeOffset.UtcNow.AddSeconds(60))
            return _cachedToken;

        // Check in-flight
        if (_inflightRefresh is not null)
        {
            _logger.LogInformation("Refresh already in-flight, awaiting existing task");
            var result = await _inflightRefresh;
            return result?.AccessToken;
        }

        // Start refresh
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

This pattern prevented the "concurrent refresh race" bug where two simultaneous callers each POSTed `/api/auth/refresh`, causing the second to get a 401 and destroy the session.

## Testing

To verify single-flight behaviour, test that N concurrent calls result in exactly 1 operation:

```csharp
[Fact]
public async Task ConcurrentCalls_OnlyStartsOneOperation()
{
    // Arrange
    var service = new MyService();
    int callCount = 0;
    service.SetOperationCallback(() => Interlocked.Increment(ref callCount));

    // Act: 10 concurrent calls
    var tasks = Enumerable.Range(0, 10)
        .Select(_ => service.GetOrFetchAsync())
        .ToArray();
    await Task.WhenAll(tasks);

    // Assert: only 1 operation was started
    Assert.Equal(1, callCount);
}
```

## References

- Applied in: `src/SentenceStudio.AppLib/Services/IdentityAuthService.cs`
- Decision: `.squad/decisions/inbox/kaylee-auth-single-flight.md`
- History: `.squad/agents/kaylee/history.md` (2026-07-29)
