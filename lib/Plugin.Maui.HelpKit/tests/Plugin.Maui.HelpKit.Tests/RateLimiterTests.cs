using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Plugin.Maui.HelpKit.RateLimit;
using Xunit;

namespace Plugin.Maui.HelpKit.Tests;

public class RateLimiterTests
{
    private static RateLimiter CreateLimiter(int maxPerMinute)
    {
        var options = Options.Create(new HelpKitOptions { MaxQuestionsPerMinute = maxPerMinute });
        return new RateLimiter(options, NullLogger<RateLimiter>.Instance);
    }

    [Fact]
    public async Task TryAcquire_AllowsUpToLimitInOneWindow()
    {
        var limiter = CreateLimiter(3);

        Assert.True(await limiter.TryAcquireAsync("user-a"));
        Assert.True(await limiter.TryAcquireAsync("user-a"));
        Assert.True(await limiter.TryAcquireAsync("user-a"));
        Assert.False(await limiter.TryAcquireAsync("user-a"));
    }

    [Fact]
    public async Task TryAcquire_BucketsAreIsolatedPerUser()
    {
        var limiter = CreateLimiter(1);

        Assert.True(await limiter.TryAcquireAsync("user-a"));
        Assert.False(await limiter.TryAcquireAsync("user-a"));

        // Different user starts fresh.
        Assert.True(await limiter.TryAcquireAsync("user-b"));
    }

    [Fact]
    public async Task TryAcquire_NullOrWhitespaceUser_UsesAnonBucket()
    {
        var limiter = CreateLimiter(2);

        Assert.True(await limiter.TryAcquireAsync(null));
        Assert.True(await limiter.TryAcquireAsync("   "));
        Assert.False(await limiter.TryAcquireAsync(null));
    }

    [Fact]
    public async Task TryAcquire_LimitZeroOrNegative_AlwaysAllows()
    {
        var limiter = CreateLimiter(0);

        for (var i = 0; i < 100; i++)
            Assert.True(await limiter.TryAcquireAsync("user-a"));
    }

    [Fact]
    public async Task TryAcquire_ConcurrentCallers_DoNotExceedLimit()
    {
        var limiter = CreateLimiter(10);
        var allowed = 0;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            if (await limiter.TryAcquireAsync("user-a"))
                Interlocked.Increment(ref allowed);
        }));

        Assert.Equal(10, allowed);
    }
}
