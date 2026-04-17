using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace HelpKitSample.SharedStubs;

/// <summary>
/// A deterministic canned <see cref="IChatClient"/> used by the HelpKit samples so
/// they run without real credentials. Cycles through a small rotation of answers
/// keyed off a hash of the last user message. Streams each response as short
/// chunks so the UI behaves like a real model.
///
/// Replace with a real provider in production — see <c>MauiProgram.cs</c> in each
/// sample for the registration pattern (keyed singleton under the HelpKit key).
/// </summary>
public sealed class StubChatClient : IChatClient
{
    private static readonly string[] s_cannedAnswers =
    {
        "HelpKit lets your users ask questions about this app in natural language. In this sample the answers come from a stub client, but the wiring is identical to a real IChatClient. [cite:getting-started.md]",
        "The help surface you are looking at is a plain MAUI page presented by an IHelpKitPresenter. No Blazor, no WebView — CollectionView and Entry. [cite:features.md]",
        "When the help pane fails to open, check that your app registered both an IChatClient and an IEmbeddingGenerator under the HelpKit service key. The resolver throws a clear message if either is missing. [cite:troubleshooting.md]",
        "Your markdown files live under the configured ContentDirectories. HelpKit chunks, embeds, and stores them locally; no documentation content leaves the device during retrieval. [cite:features.md]",
    };

    public ChatClientMetadata Metadata { get; } = new("stub-chat-client", new Uri("https://example.invalid/stub"), "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var answer = PickAnswer(messages);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, answer));
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var answer = PickAnswer(messages);

        foreach (var chunk in Chunk(answer, size: 24))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose() { }

    private static string PickAnswer(IEnumerable<ChatMessage> messages)
    {
        var lastUser = string.Empty;
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User)
                lastUser = m.Text ?? string.Empty;
        }

        var hash = 0;
        foreach (var c in lastUser)
            hash = unchecked((hash * 31) + c);

        var index = (int)((uint)hash % (uint)s_cannedAnswers.Length);
        return s_cannedAnswers[index];
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
