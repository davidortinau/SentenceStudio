using Plugin.Maui.HelpKit.Storage;
using Xunit;

namespace Plugin.Maui.HelpKit.Tests;

/// <summary>
/// Tests for <see cref="AnswerCache"/> that do not require a live SQLite database.
/// The cache's storage semantics (TTL eviction, wholesale invalidation on fingerprint
/// change) are exercised at integration level via smoke tests in
/// <c>tests/smoke-tests/</c> and cross-cutting scenarios X02/X03 in VALIDATION-PLAN.md.
/// </summary>
public class AnswerCacheTests
{
    [Fact]
    public void ComputeKey_IsDeterministic()
    {
        var a = AnswerCache.ComputeKey("How do I add a word?", "fp-1");
        var b = AnswerCache.ComputeKey("How do I add a word?", "fp-1");

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA-256 hex lowercase
    }

    [Fact]
    public void ComputeKey_IsCaseAndWhitespaceInsensitiveOnQuestion()
    {
        var a = AnswerCache.ComputeKey("  Hello World  ", "fp-1");
        var b = AnswerCache.ComputeKey("hello world", "fp-1");

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeKey_ChangesWhenFingerprintChanges()
    {
        var a = AnswerCache.ComputeKey("hi", "fp-1");
        var b = AnswerCache.ComputeKey("hi", "fp-2");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeKey_ChangesWhenQuestionChanges()
    {
        var a = AnswerCache.ComputeKey("hi", "fp-1");
        var b = AnswerCache.ComputeKey("hello", "fp-1");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeKey_NullArgumentsAreToleratedAsEmpty()
    {
        var a = AnswerCache.ComputeKey(null!, null!);
        var b = AnswerCache.ComputeKey(string.Empty, string.Empty);

        Assert.Equal(a, b);
    }
}
