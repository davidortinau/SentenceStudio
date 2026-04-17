using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Plugin.Maui.HelpKit.Eval;

/// <summary>
/// Deterministic scripted IChatClient used when HELPKIT_EVAL_LIVE is not "1".
/// Canned responses are keyed by a stable hash of the question text so the eval
/// harness produces identical results in CI across runs.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Dictionary<string, string> _responsesByHash;

    public FakeChatClient(IEnumerable<GoldenQaItem>? items = null)
    {
        _responsesByHash = new Dictionary<string, string>(StringComparer.Ordinal);

        var source = items ?? GoldenSet.Load();
        foreach (var item in source)
        {
            _responsesByHash[Hash(item.Question)] = BuildCannedResponse(item);
        }
    }

    public ChatClientMetadata Metadata { get; } = new("fake-helpkit-eval", null, "fake-1.0");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = ResolveResponse(messages);
        var message = new ChatMessage(ChatRole.Assistant, text);
        var response = new ChatResponse(message)
        {
            ResponseId = Guid.NewGuid().ToString("n"),
            ModelId = Metadata.DefaultModelId,
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = ResolveResponse(messages);
        // Emit in a few chunks so callers that rely on streaming semantics exercise that path.
        var chunkSize = Math.Max(1, text.Length / 4);
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = text.Substring(i, Math.Min(chunkSize, text.Length - i));
            yield return new ChatResponseUpdate(ChatRole.Assistant, slice);
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }
        return null;
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    private string ResolveResponse(IEnumerable<ChatMessage> messages)
    {
        var userText = ExtractLatestUserText(messages);
        if (userText is null)
        {
            return "I don't have documentation to answer that.";
        }

        if (_responsesByHash.TryGetValue(Hash(userText), out var canned))
        {
            return canned;
        }

        return "I don't have documentation to answer that. Please rephrase or ask about a SentenceStudio feature.";
    }

    private static string? ExtractLatestUserText(IEnumerable<ChatMessage> messages)
    {
        ChatMessage? latestUser = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                latestUser = message;
            }
        }

        if (latestUser is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var content in latestUser.Contents)
        {
            if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }
                builder.Append(textContent.Text);
            }
        }

        if (builder.Length == 0 && !string.IsNullOrEmpty(latestUser.Text))
        {
            builder.Append(latestUser.Text);
        }

        return builder.Length == 0 ? null : builder.ToString().Trim();
    }

    private static string Hash(string question)
    {
        var normalized = question.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private static string BuildCannedResponse(GoldenQaItem item)
    {
        if (item.MustRefuse)
        {
            return "I don't have documentation to answer that. That question is outside my scope — " +
                "I can only help with SentenceStudio features.";
        }

        // Compose a response that surfaces all expected keywords and cites every required path.
        // Kept intentionally blunt — the harness checks for keyword coverage, not prose quality.
        var keywordSentence = string.Join(", ", item.ExpectedAnswerKeywords);
        var citations = item.RequiredCitationPaths.Length == 0
            ? string.Empty
            : $"\n\nSources: {string.Join(", ", item.RequiredCitationPaths.Select(p => $"[{p}]"))}";

        return $"To answer your question: {keywordSentence}.{citations}";
    }
}
