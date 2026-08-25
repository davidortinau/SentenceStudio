using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Provider failures must never be logged as exceptions.
/// </summary>
/// <remarks>
/// A model provider echoes the prompt back in its error messages, and the prompt contains the
/// learner's own words. <c>LogError(ex, ...)</c> serialises <c>ToString()</c>, which walks the
/// message, every inner exception, and the stack trace — so a single failed turn can put an
/// entire conversation into a log sink that has none of the protections the database has.
/// </remarks>
public class CoachExceptionSanitizerTests
{
    private const string Sentinel = CoachPersistenceSamples.LearnerSentinel;

    [Fact]
    public void Describe_DoesNotCarryTheExceptionMessage()
    {
        var facts = CoachExceptionSanitizer.Describe(
            new InvalidOperationException($"model rejected prompt: {Sentinel}"));

        Render(facts).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_DoesNotCarryInnerExceptionMessages()
    {
        var exception = new InvalidOperationException(
            "outer",
            new HttpRequestException($"upstream said: {Sentinel}"));

        Render(CoachExceptionSanitizer.Describe(exception)).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_DoesNotCarryExceptionData()
    {
        var exception = new InvalidOperationException("failure");
        exception.Data["prompt"] = Sentinel;

        Render(CoachExceptionSanitizer.Describe(exception)).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_DoesNotCarryDeeplyNestedLearnerText()
    {
        var exception = new InvalidOperationException(
            "a", new InvalidOperationException(
                "b", new InvalidOperationException(
                    "c", new TimeoutException(Sentinel))));

        Render(CoachExceptionSanitizer.Describe(exception)).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_ReportsATimeoutCategory()
    {
        var facts = CoachExceptionSanitizer.Describe(new TimeoutException(Sentinel));

        facts.Category.Should().NotBe(CoachExceptionSanitizer.UnclassifiedCategory,
            "an operator has to be able to tell a timeout from a bad request without the message");
    }

    [Fact]
    public void Describe_UnwrapsAggregateExceptions()
    {
        var direct = CoachExceptionSanitizer.Describe(new TimeoutException("x"));
        var wrapped = CoachExceptionSanitizer.Describe(
            new AggregateException(new TimeoutException("x")));

        wrapped.Category.Should().Be(direct.Category,
            "an async call site wraps the cause, and the wrapper is never the interesting fact");
    }

    [Fact]
    public void Describe_ClassifiesAnUnknownExceptionWithoutGuessing()
    {
        var facts = CoachExceptionSanitizer.Describe(new CustomProviderException(Sentinel));

        facts.Category.Should().Be(CoachExceptionSanitizer.UnclassifiedCategory,
            "an unrecognised type must fall back to a constant, never to the type's own message");
        Render(facts).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_CapturesAProviderStatusCode()
    {
        var facts = CoachExceptionSanitizer.Describe(new StatusCarryingException(429, Sentinel));

        facts.ProviderStatus.Should().Be(429,
            "rate limiting is the one provider failure an operator must be able to see immediately");
        Render(facts).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_IgnoresAStatusOutsideTheHttpRange()
    {
        // A property named Status is not necessarily an HTTP status; anything implausible is
        // dropped rather than reported as one.
        CoachExceptionSanitizer.Describe(new StatusCarryingException(99, "x")).ProviderStatus.Should().BeNull();
        CoachExceptionSanitizer.Describe(new StatusCarryingException(9999, "x")).ProviderStatus.Should().BeNull();
    }

    [Fact]
    public void Describe_CapturesAShortSymbolicErrorCode()
    {
        var facts = CoachExceptionSanitizer.Describe(new CodeCarryingException("content_filter", Sentinel));

        facts.ProviderErrorCode.Should().Be("content_filter");
        Render(facts).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_RejectsAnErrorCodeThatIsActuallyProse()
    {
        // Some providers put the whole failure sentence in a field called "code", and that
        // sentence quotes the prompt.
        var facts = CoachExceptionSanitizer.Describe(
            new CodeCarryingException($"the request failed because {Sentinel} was rejected", "outer"));

        facts.ProviderErrorCode.Should().BeNull();
        Render(facts).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_ReportsTheInnerDepthWithoutTheChainContents()
    {
        var facts = CoachExceptionSanitizer.Describe(
            new InvalidOperationException(Sentinel, new TimeoutException(Sentinel)));

        facts.InnerDepth.Should().BeGreaterThan(0, "the depth is a useful shape signal on its own");
        Render(facts).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Describe_HandlesNull()
    {
        CoachExceptionSanitizer.Describe(null).Should().Be(CoachSafeExceptionFacts.None);
    }

    /// <summary>Everything the sanitizer would allow a log statement to emit.</summary>
    private static string Render(CoachSafeExceptionFacts facts) =>
        $"{facts.Category}|{facts.ProviderStatus}|{facts.ProviderErrorCode}|{facts.InnerDepth}|{facts}";

    private sealed class CustomProviderException(string message) : Exception(message);

    private sealed class StatusCarryingException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private sealed class CodeCarryingException(string code, string message) : Exception(message)
    {
        public string ErrorCode { get; } = code;
    }
}
