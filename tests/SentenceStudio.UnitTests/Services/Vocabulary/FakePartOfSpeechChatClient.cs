using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SentenceStudio.UnitTests.Services.Vocabulary;

/// <summary>
/// A scripted <see cref="IChatClient"/> that records every request and answers with caller-supplied
/// JSON, so the backfill's structured-output path is exercised without a network call.
/// </summary>
internal sealed class FakePartOfSpeechChatClient : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _responses = new();
    private Func<IReadOnlyList<ChatMessage>, ChatResponse>? _fallback;

    /// <summary>Every user-role payload the backfill sent, in order.</summary>
    public List<string> SentPayloads { get; } = new();

    /// <summary>Every message the backfill sent, flattened.</summary>
    public List<string> SentText { get; } = new();

    public int CallCount { get; private set; }

    /// <summary>Answers the next call by classifying every requested id with the given token.</summary>
    public FakePartOfSpeechChatClient RespondClassifyingAll(string token = "noun")
    {
        _responses.Enqueue(messages => Json(BuildFor(messages, id => (id, token))));
        return this;
    }

    /// <summary>Answers every remaining call by classifying each requested id with the given token.</summary>
    public FakePartOfSpeechChatClient AlwaysClassifyAll(string token = "noun")
    {
        _fallback = messages => Json(BuildFor(messages, id => (id, token)));
        return this;
    }

    /// <summary>Answers the next call with an exact literal payload.</summary>
    public FakePartOfSpeechChatClient RespondWithRaw(string json)
    {
        _responses.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        return this;
    }

    /// <summary>Answers the next call with a transform over the requested ids.</summary>
    public FakePartOfSpeechChatClient Respond(Func<IReadOnlyList<string>, IEnumerable<(string Id, string Token)>> transform)
    {
        _responses.Enqueue(messages =>
        {
            var ids = RequestedIds(messages);
            return Json(transform(ids).ToList());
        });
        return this;
    }

    /// <summary>Runs an action (for example a cancellation) when the next call arrives.</summary>
    public FakePartOfSpeechChatClient OnCall(Action action, string token = "noun")
    {
        _responses.Enqueue(messages =>
        {
            action();
            return Json(BuildFor(messages, id => (id, token)));
        });
        return this;
    }

    /// <summary>Throws on the next call, to exercise the model-failure path.</summary>
    public FakePartOfSpeechChatClient Throws()
    {
        _responses.Enqueue(_ => throw new InvalidOperationException("model unavailable"));
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var list = messages.ToList();
        CallCount++;

        foreach (var message in list)
        {
            SentText.Add(message.Text ?? string.Empty);
            if (message.Role == ChatRole.User)
            {
                SentPayloads.Add(message.Text ?? string.Empty);
            }
        }

        var responder = _responses.Count > 0
            ? _responses.Dequeue()
            : _fallback ?? throw new InvalidOperationException("No scripted response for this call.");

        var response = responder(list);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The backfill never streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>The word ids the backfill asked about in one call.</summary>
    public static IReadOnlyList<string> RequestedIds(IReadOnlyList<ChatMessage> messages)
    {
        var payload = messages.Last(m => m.Role == ChatRole.User).Text ?? "[]";
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("Id").GetString()!)
            .ToList();
    }

    private static List<(string Id, string Token)> BuildFor(
        IReadOnlyList<ChatMessage> messages,
        Func<string, (string, string)> map) =>
        RequestedIds(messages).Select(map).ToList();

    private static ChatResponse Json(IEnumerable<(string Id, string Token)> classifications)
    {
        var payload = new
        {
            classifications = classifications
                .Select(c => new { id = c.Id, partOfSpeech = c.Token })
                .ToList()
        };

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload)));
    }
}
