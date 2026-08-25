using Microsoft.Extensions.AI;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// A chat client that records every call and refuses to answer. Any coach path that is
/// supposed to be model-free fails loudly if it reaches the model.
/// </summary>
public sealed class RecordingChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        throw new InvalidOperationException("The coach called the model on a path that must be model-free.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        throw new InvalidOperationException("The coach streamed from the model on a path that must be model-free.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// A chat client that answers with one canned JSON payload, so structured output can be
/// exercised without a network call.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly string _json;

    public ScriptedChatClient(string json) => _json = json;

    public int CallCount { get; private set; }

    public ChatOptions? LastOptions { get; private set; }

    /// <summary>
    /// Why the model stopped. Set <see cref="ChatFinishReason.Length"/> to reproduce a response
    /// that hit the output-token cap — on a reasoning model that arrives with no visible text,
    /// because hidden reasoning tokens count against the same cap.
    /// </summary>
    public ChatFinishReason? FinishReason { get; init; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _json))
        {
            FinishReason = FinishReason
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The coach never streams in version 1.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
