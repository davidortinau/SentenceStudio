using FluentAssertions;
using Microsoft.Extensions.AI;
using SentenceStudio.Services;
using OpenAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using OpenAIChatReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel;

namespace SentenceStudio.UnitTests.Services;

public class AiChatOptionsFactoryTests
{
    [Fact]
    public void Create_WithNoInstructionsOrEffort_ReturnsNull()
    {
        AiChatOptionsFactory.Create().Should().BeNull();
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void IsSupportedReasoningEffort_AcceptsKnownEffortValues(string effort)
    {
        AiChatOptionsFactory.IsSupportedReasoningEffort(effort).Should().BeTrue();
    }

    [Fact]
    public void IsSupportedReasoningEffort_RejectsUnknownEffortValue()
    {
        AiChatOptionsFactory.IsSupportedReasoningEffort("maximum").Should().BeFalse();
    }

    [Fact]
    public void Create_WithMinimalEffort_SetsOpenAiRawOptions()
    {
        var options = AiChatOptionsFactory.Create(reasoningEffort: "minimal");

        options.Should().NotBeNull();
        options!.RawRepresentationFactory.Should().NotBeNull();

#pragma warning disable OPENAI001
        var raw = options.RawRepresentationFactory!(new NullChatClient()).Should().BeOfType<OpenAIChatCompletionOptions>().Subject;
        raw.ReasoningEffortLevel.Should().Be(OpenAIChatReasoningEffortLevel.Minimal);
#pragma warning restore OPENAI001
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
