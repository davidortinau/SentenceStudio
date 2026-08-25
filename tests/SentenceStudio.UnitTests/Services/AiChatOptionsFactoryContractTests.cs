using FluentAssertions;
using Microsoft.Extensions.AI;
using SentenceStudio.Services;
using OpenAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using OpenAIChatReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel;

namespace SentenceStudio.UnitTests.Services;

// OpenAI.Chat.ChatReasoningEffortLevel and ChatCompletionOptions.ReasoningEffortLevel
// are gated behind OPENAI001, which the OpenAI SDK raises at *error* severity — this
// file does not compile without the suppression. AiChatOptionsFactory suppresses the
// same diagnostic at its own usage sites, and so does AiChatOptionsFactoryTests; this
// baseline has to touch the APIs throughout to assert the mapping, so the suppression
// is file-scoped here rather than repeated per member.
//
// Note this pragma is NOT a tripwire: C# emits nothing for an unnecessary
// `#pragma warning disable`, so if a future OpenAI package promotes these APIs out of
// evaluation status the suppression will simply go quiet. Removing it then is a manual
// cleanup step, not something the build will remind us about.
#pragma warning disable OPENAI001

/// <summary>
/// Phase 0 package-upgrade baseline for <see cref="AiChatOptionsFactory"/>.
///
/// <c>AiChatOptionsFactory</c> is the single seam where our request contract
/// (<c>ChatRequest.Scenario</c> / <c>ChatRequest.ReasoningEffort</c>) is translated
/// into Microsoft.Extensions.AI <see cref="ChatOptions"/> and, through
/// <see cref="ChatOptions.RawRepresentationFactory"/>, into an OpenAI
/// <c>ChatCompletionOptions</c>. That makes it the most upgrade-fragile code in
/// the AI path: it is coupled to MEAI *and* to the OpenAI SDK's
/// <c>ChatReasoningEffortLevel</c> (an OPENAI001-experimental type).
///
/// <c>AiChatOptionsFactoryTests</c> already covers the null case, the supported /
/// unsupported effort predicate, and the "minimal" mapping. This file widens the
/// baseline so that after the Agent Framework / MEAI / OpenAI package upgrade we
/// can diff behavior rather than guess: every effort level's mapping, the
/// instructions path, trimming/casing normalization, and the interaction between
/// an unsupported effort and a valid scenario.
/// </summary>
public class AiChatOptionsFactoryContractTests
{
    [Fact]
    public void Create_WithScenarioOnly_SetsInstructionsAndNoRawFactory()
    {
        var options = AiChatOptionsFactory.Create(instructions: "You are a Korean tutor.");

        options.Should().NotBeNull();
        options!.Instructions.Should().Be("You are a Korean tutor.");
        options.RawRepresentationFactory.Should().BeNull(
            "no reasoning effort was requested, so nothing should reach the OpenAI raw options");
    }

    [Fact]
    public void Create_WithEffortOnly_LeavesInstructionsUnset()
    {
        var options = AiChatOptionsFactory.Create(reasoningEffort: "high");

        options.Should().NotBeNull();
        options!.Instructions.Should().BeNull();
        options.RawRepresentationFactory.Should().NotBeNull();
    }

    [Fact]
    public void Create_WithScenarioAndEffort_SetsBoth()
    {
        var options = AiChatOptionsFactory.Create("Order coffee in Korean.", "medium");

        options.Should().NotBeNull();
        options!.Instructions.Should().Be("Order coffee in Korean.");
        options.RawRepresentationFactory.Should().NotBeNull();
        ReasoningEffortOf(options).Should().Be(OpenAIChatReasoningEffortLevel.Medium);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithWhitespaceOnlyInputs_ReturnsNull(string blank)
    {
        AiChatOptionsFactory.Create(blank, blank).Should().BeNull(
            "whitespace is treated as absent, so the endpoint sends no ChatOptions at all");
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void Create_WithEachSupportedEffort_ProducesOpenAiRawOptions(string effort)
    {
        var options = AiChatOptionsFactory.Create(reasoningEffort: effort);

        options.Should().NotBeNull();
        ReasoningEffortOf(options!).Should().Be(ExpectedLevel(effort));
    }

    [Theory]
    [InlineData("MINIMAL", "minimal")]
    [InlineData("High", "high")]
    [InlineData("  medium  ", "medium")]
    [InlineData("\tLOW\t", "low")]
    public void Create_NormalizesEffortCasingAndWhitespace(string raw, string canonical)
    {
        var options = AiChatOptionsFactory.Create(reasoningEffort: raw);

        options.Should().NotBeNull();
        ReasoningEffortOf(options!).Should().Be(ExpectedLevel(canonical));
    }

    [Theory]
    [InlineData("MINIMAL")]
    [InlineData("  high ")]
    [InlineData("Medium")]
    public void IsSupportedReasoningEffort_IsCaseAndWhitespaceInsensitive(string effort)
    {
        AiChatOptionsFactory.IsSupportedReasoningEffort(effort).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSupportedReasoningEffort_TreatsAbsentEffortAsValid(string? effort)
    {
        AiChatOptionsFactory.IsSupportedReasoningEffort(effort).Should().BeTrue(
            "ChatRequest.ReasoningEffort is optional — absent means 'use the provider default', " +
            "and /api/v1/ai/chat must not 400 on it");
    }

    [Theory]
    [InlineData("maximum")]
    [InlineData("none")]
    [InlineData("verylow")]
    [InlineData("2")]
    public void IsSupportedReasoningEffort_RejectsUnknownValues(string effort)
    {
        AiChatOptionsFactory.IsSupportedReasoningEffort(effort).Should().BeFalse(
            "/api/v1/ai/chat returns 400 for any effort this predicate rejects");
    }

    [Fact]
    public void Create_WithUnsupportedEffort_StillHonorsInstructionsButSkipsRawFactory()
    {
        // The endpoint rejects unsupported efforts with a 400 before ever calling
        // Create, so this pins the factory's own defensive behavior: an unknown
        // effort is dropped rather than throwing or leaking a bogus raw option.
        var options = AiChatOptionsFactory.Create("Be encouraging.", "maximum");

        options.Should().NotBeNull();
        options!.Instructions.Should().Be("Be encouraging.");
        options.RawRepresentationFactory.Should().BeNull();
    }

    [Fact]
    public void Create_RawRepresentationFactory_ProducesAFreshInstancePerInvocation()
    {
        var options = AiChatOptionsFactory.Create(reasoningEffort: "low");
        options.Should().NotBeNull();

        var first = InvokeRawFactory(options!);
        var second = InvokeRawFactory(options!);

        first.Should().NotBeSameAs(second,
            "MEAI may invoke the factory once per request; a shared mutable " +
            "ChatCompletionOptions instance would leak state across calls");
        first.ReasoningEffortLevel.Should().Be(OpenAIChatReasoningEffortLevel.Low);
        second.ReasoningEffortLevel.Should().Be(OpenAIChatReasoningEffortLevel.Low);
    }

    private static OpenAIChatReasoningEffortLevel ExpectedLevel(string canonical) => canonical switch
    {
        "minimal" => OpenAIChatReasoningEffortLevel.Minimal,
        "low" => OpenAIChatReasoningEffortLevel.Low,
        "medium" => OpenAIChatReasoningEffortLevel.Medium,
        "high" => OpenAIChatReasoningEffortLevel.High,
        _ => throw new ArgumentOutOfRangeException(nameof(canonical), canonical, "Unmapped effort level.")
    };

    private static OpenAIChatReasoningEffortLevel? ReasoningEffortOf(ChatOptions options)
        => InvokeRawFactory(options).ReasoningEffortLevel;

    private static OpenAIChatCompletionOptions InvokeRawFactory(ChatOptions options)
    {
        options.RawRepresentationFactory.Should().NotBeNull();
        return options.RawRepresentationFactory!(new NullChatClient())
            .Should().BeOfType<OpenAIChatCompletionOptions>().Subject;
    }

    private sealed class NullChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
    }
}

#pragma warning restore OPENAI001
