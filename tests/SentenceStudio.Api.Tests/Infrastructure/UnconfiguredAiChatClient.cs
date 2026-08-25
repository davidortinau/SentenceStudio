using Microsoft.Extensions.AI;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// A deterministic, no-network <see cref="IChatClient"/> used only to let the API test host
/// finish DI validation when there is no AI configuration.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.cs</c> registers <c>IChatClient</c> only when <c>AI:OpenAI:Endpoint</c> is set,
/// but registers <c>AiService</c> (and <c>VideoImportPipelineService</c> /
/// <c>TranscriptFormattingService</c>, which both take one) unconditionally. With
/// validate-on-build enabled in Development, a test host with no AI endpoint therefore fails
/// to start — taking every unrelated auth, profile, speech, and plan-tracking test with it.
/// </para>
/// <para>
/// This type exists to satisfy <i>construction</i> and nothing else. Every call throws.
/// That is deliberate: a stub that returned canned content would let an AI-dependent test
/// pass without ever proving anything, which is precisely the failure mode a stub is supposed
/// to prevent. If a test ever reaches the model through this client, it fails loudly and names
/// itself. Same contract as <c>RecordingChatClient</c> in the coach tests, which established
/// this pattern.
/// </para>
/// <para>
/// It never masks a real fake: <see cref="TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured"/>
/// registers it with <c>TryAdd</c> semantics and skips entirely when an AI endpoint is
/// configured, so any factory that supplies its own client keeps it.
/// </para>
/// </remarks>
public sealed class UnconfiguredAiChatClient : IChatClient
{
    private int _callCount;

    /// <summary>How many times a test reached the model through this stub. Must stay 0.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw Fail();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw Fail();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private InvalidOperationException Fail()
    {
        Interlocked.Increment(ref _callCount);
        return new InvalidOperationException(
            "A test called the AI model through UnconfiguredAiChatClient. This stub exists only " +
            "so the API host can pass DI validation with no AI configuration — it is not a " +
            "functional fake. If this test genuinely needs model output, register a purpose-built " +
            "client (see ScriptedChatClient) on the factory instead of relying on the stub.");
    }
}
