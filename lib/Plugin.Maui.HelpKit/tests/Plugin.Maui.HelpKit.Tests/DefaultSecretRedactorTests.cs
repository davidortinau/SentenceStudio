using Plugin.Maui.HelpKit;
using Xunit;

namespace Plugin.Maui.HelpKit.Tests;

public class DefaultSecretRedactorTests
{
    private readonly DefaultSecretRedactor _sut = new();

    [Theory]
    [InlineData("api_key: sk-ABCDEFGHIJKLMNOPQRSTUV", "REDACTED")]
    [InlineData("password = \"p@ssw0rd-1234567\"", "REDACTED")]
    [InlineData("Contact me at dev@example.com", "REDACTED")]
    public void Redact_removes_common_secret_patterns(string input, string expectedMarker)
    {
        var result = _sut.Redact(input);
        Assert.Contains(expectedMarker, result);
    }

    [Fact]
    public void Redact_passes_through_benign_content()
    {
        const string input = "This is a normal sentence about kimchi.";
        Assert.Equal(input, _sut.Redact(input));
    }

    [Fact]
    public void Redact_handles_empty_input()
    {
        Assert.Equal(string.Empty, _sut.Redact(string.Empty));
    }

    // Extended provider-prefixed secret coverage (Jayne).
    [Theory]
    [InlineData("token=ghp_1234567890abcdefghij1234567890abcdefgh")]
    [InlineData("slack: xoxb-12345-67890-ABCDEFGHIJKL")]
    [InlineData("aws id: AKIAIOSFODNN7EXAMPLE")]
    [InlineData("Bearer sk-proj-ABCDEFGHIJKLMNOPQRSTUVWX")]
    public void Redact_strips_known_provider_secret_prefixes(string input)
    {
        var result = _sut.Redact(input);
        Assert.Contains("REDACTED", result);
        // Belt-and-suspenders: confirm the raw token tail is gone.
        Assert.DoesNotContain("ghp_1234567890abcdefghij1234567890abcdefgh", result);
        Assert.DoesNotContain("xoxb-12345-67890-ABCDEFGHIJKL", result);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result);
        Assert.DoesNotContain("sk-proj-ABCDEFGHIJKLMNOPQRSTUVWX", result);
    }

    [Fact]
    public void Redact_does_not_redact_short_lookalike_strings()
    {
        // Tokens shorter than the regex floor should pass through untouched.
        const string input = "Use sk-test as a placeholder name in docs.";
        var result = _sut.Redact(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Redact_handles_multiple_secrets_in_one_chunk()
    {
        const string input = "Set api_key: sk-AAAAAAAAAAAAAAAAAAAAA and email dev@example.com.";
        var result = _sut.Redact(input);
        Assert.Contains("REDACTED", result);
        Assert.DoesNotContain("sk-AAAAAAAAAAAAAAAAAAAAA", result);
        Assert.DoesNotContain("dev@example.com", result);
    }
}
