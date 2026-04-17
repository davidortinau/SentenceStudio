using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Controls;
using Plugin.Maui.HelpKit.Diagnostics;
using Plugin.Maui.HelpKit.Ingestion;
using Plugin.Maui.HelpKit.Rag;
using Plugin.Maui.HelpKit.RateLimit;
using Plugin.Maui.HelpKit.Storage;

namespace Plugin.Maui.HelpKit;

/// <summary>
/// Default <see cref="IHelpKit"/> implementation. Wires the presenter,
/// storage, ingestion, retrieval, streaming chat, and citation validation
/// into one orchestration path.
/// </summary>
internal sealed class HelpKitService : IHelpKit
{
    private readonly IServiceProvider _services;
    private readonly IHelpKitPresenter _presenter;
    private readonly HelpKitOptions _options;
    private readonly HelpKitAiResolver _ai;
    private readonly IHelpKitContentFilter _contentFilter;
    private readonly HelpKitDatabase _db;
    private readonly ConversationRepository _conversations;
    private readonly MessageRepository _messages;
    private readonly VectorStore _vectorStore;
    private readonly IngestionCoordinator _ingestion;
    private readonly AnswerCache _answerCache;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<HelpKitService> _logger;

    private Page? _currentPage;

    public HelpKitService(
        IServiceProvider services,
        IHelpKitPresenter presenter,
        IOptions<HelpKitOptions> options,
        HelpKitAiResolver ai,
        IHelpKitContentFilter contentFilter,
        HelpKitDatabase db,
        ConversationRepository conversations,
        MessageRepository messages,
        VectorStore vectorStore,
        IngestionCoordinator ingestion,
        AnswerCache answerCache,
        RateLimiter rateLimiter,
        ILogger<HelpKitService> logger)
    {
        _services = services;
        _presenter = presenter;
        _options = options.Value;
        _ai = ai;
        _contentFilter = contentFilter;
        _db = db;
        _conversations = conversations;
        _messages = messages;
        _vectorStore = vectorStore;
        _ingestion = ingestion;
        _answerCache = answerCache;
        _rateLimiter = rateLimiter;
        _logger = logger;

        // Purge stale history eagerly — cheap, non-blocking. Fire-and-forget
        // so we don't deadlock on a sync context during DI construction.
        _ = Task.Run(async () =>
        {
            try { await _conversations.PurgeOlderThanAsync(_options.HistoryRetention).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "History retention purge failed; continuing."); }
        });
    }

    public async Task ShowAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("HelpKit.ShowAsync invoked");
        _currentPage = _services.GetRequiredService<HelpKitPage>();
        await _presenter.PresentAsync(_currentPage, ct).ConfigureAwait(false);
    }

    public async Task HideAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("HelpKit.HideAsync invoked");
        await _presenter.DismissAsync(ct).ConfigureAwait(false);
        _currentPage = null;
    }

    public Task ClearHistoryAsync(CancellationToken ct = default)
    {
        var userId = ResolveCurrentUserId();
        return _conversations.ClearAsync(userId, ct);
    }

    public Task IngestAsync(CancellationToken ct = default)
        => _ingestion.IngestAsync(ct);

    public async IAsyncEnumerable<HelpKitMessage> StreamAskAsync(
        string question,
        string? conversationId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            yield return new HelpKitMessage("assistant", "Please enter a question.", Array.Empty<HelpKitCitation>());
            yield break;
        }

        var userId = ResolveCurrentUserId();

        // 1. Rate limit.
        var allowed = await _rateLimiter.TryAcquireAsync(userId, ct).ConfigureAwait(false);
        if (!allowed)
        {
            yield return new HelpKitMessage(
                "assistant",
                "Rate limit exceeded — please wait a moment.",
                Array.Empty<HelpKitCitation>());
            yield break;
        }

        // 2. Ensure conversation exists.
        var convId = conversationId;
        if (string.IsNullOrWhiteSpace(convId))
            convId = await _conversations.CreateAsync(userId, TruncateTitle(question), ct).ConfigureAwait(false);
        else
            await _conversations.TouchAsync(convId!, ct).ConfigureAwait(false);

        // 3. Persist the user turn immediately so it survives a crash.
        await _messages.AppendAsync(convId!, "user", question, null, ct).ConfigureAwait(false);

        // 4. Answer cache lookup (keyed by question + fingerprint).
        string fingerprint;
        try { fingerprint = _ingestion.GetCurrentFingerprint(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not compute pipeline fingerprint; ensure an IEmbeddingGenerator is registered.");
            yield return new HelpKitMessage(
                "assistant",
                "Help is not fully configured yet. Register an IEmbeddingGenerator and an IChatClient, then call IngestAsync.",
                Array.Empty<HelpKitCitation>());
            yield break;
        }

        var cached = await _answerCache.TryGetAsync(question, fingerprint, ct).ConfigureAwait(false);
        if (cached is { } hit)
        {
            HelpKitMetrics.Increment(HelpKitMetrics.AnswerCacheHits);
            await _messages.AppendAsync(convId!, "assistant", hit.Answer, hit.Citations, ct).ConfigureAwait(false);
            yield return new HelpKitMessage("assistant", hit.Answer, hit.Citations);
            yield break;
        }
        HelpKitMetrics.Increment(HelpKitMetrics.AnswerCacheMisses);

        // 5. Resolve AI components.
        IChatClient chat;
        IEmbeddingGenerator<string, Embedding<float>> embeddings;
        try
        {
            chat = _ai.ResolveChatClient();
            embeddings = _ai.ResolveEmbeddingGenerator();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI resolution failed.");
            var errorMsg = ex.Message;
            await _messages.AppendAsync(convId!, "assistant", errorMsg, null, ct).ConfigureAwait(false);
            yield return new HelpKitMessage("assistant", errorMsg, Array.Empty<HelpKitCitation>());
            yield break;
        }

        // 6. Retrieve.
        HelpKitMetrics.Increment(HelpKitMetrics.RetrievalQueries);
        var modelId = _ingestion.GetEmbeddingModelId();
        var threshold = _options.SimilarityThresholdOverride ?? SimilarityThresholds.DefaultFor(modelId);

        var queryEmbedding = await EmbedQueryAsync(embeddings, question, ct).ConfigureAwait(false);
        var hits = await _vectorStore.SearchAsync(queryEmbedding, _options.RetrievalTopK, ct).ConfigureAwait(false);
        var aboveThreshold = hits.Where(h => h.Score >= threshold).ToList();

        if (aboveThreshold.Count == 0)
        {
            HelpKitMetrics.Increment(HelpKitMetrics.RetrievalThresholdRefusals);
            const string refusal = "I don't have documentation about that.";
            await _messages.AppendAsync(convId!, "assistant", refusal, null, ct).ConfigureAwait(false);
            yield return new HelpKitMessage("assistant", refusal, Array.Empty<HelpKitCitation>());
            yield break;
        }

        var retrievedChunks = aboveThreshold
            .Select(h => new HelpKitChunk(
                h.Chunk.Id, h.Chunk.SourcePath, h.Chunk.HeadingPath, h.Chunk.SectionAnchor, h.Chunk.Content))
            .ToList();

        // 7. Build prompt + history.
        var history = await LoadHistoryForPromptAsync(convId!, ct).ConfigureAwait(false);
        var appName = AppInfoSafeName();
        var promptMessages = SystemPrompt.Build(question, retrievedChunks, history, appName, _options.Language);

        // 8. Stream the reply. We accumulate the full text so we can run
        //    the citation validator + prompt-injection filter on the final
        //    response before persisting + caching.
        var buffer = new StringBuilder();
        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = chat.GetStreamingResponseAsync(promptMessages, options: null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat client failed.");
            await _messages.AppendAsync(convId!, "assistant", "The help service is temporarily unavailable.", null, ct).ConfigureAwait(false);
            yield return new HelpKitMessage(
                "assistant", "The help service is temporarily unavailable.", Array.Empty<HelpKitCitation>());
            yield break;
        }

        await foreach (var update in stream.ConfigureAwait(false).WithCancellation(ct))
        {
            var text = update?.Text;
            if (string.IsNullOrEmpty(text)) continue;
            buffer.Append(text);
            yield return new HelpKitMessage("assistant", buffer.ToString(), Array.Empty<HelpKitCitation>());
        }

        // 9. Validate citations + filter system-prompt leakage.
        var rawAnswer = buffer.ToString();
        if (PromptInjectionFilter.TryDetectLeak(rawAnswer, out var sanitized))
        {
            rawAnswer = sanitized;
        }

        var validated = CitationValidator.Validate(rawAnswer, retrievedChunks);
        var publicCitations = validated.ValidCitations
            .Select(c => new HelpKitCitation(c.SourcePath, c.HeadingPath, c.SectionAnchor))
            .ToList();
        var displayContent = CitationValidator.RenderForDisplay(validated);

        // 10. Persist and cache.
        await _messages.AppendAsync(convId!, "assistant", displayContent, publicCitations, ct).ConfigureAwait(false);
        await _answerCache.PutAsync(question, fingerprint, displayContent, publicCitations, ttl: null, ct).ConfigureAwait(false);

        yield return new HelpKitMessage("assistant", displayContent, publicCitations);
    }

    private string? ResolveCurrentUserId()
    {
        if (_options.CurrentUserProvider is null) return null;
        try { return _options.CurrentUserProvider(_services); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CurrentUserProvider threw; falling back to anonymous scope.");
            return null;
        }
    }

    private async Task<IReadOnlyList<Rag.HelpKitMessage>> LoadHistoryForPromptAsync(
        string conversationId, CancellationToken ct)
    {
        try
        {
            var rows = await _messages.GetForConversationAsync(conversationId, ct).ConfigureAwait(false);
            if (rows.Count == 0) return Array.Empty<Rag.HelpKitMessage>();

            // Skip the just-persisted user turn (caller re-adds it as the
            // final user message). Keep the last ~10 turns to bound tokens.
            var prior = rows.Take(Math.Max(0, rows.Count - 1)).TakeLast(10).ToList();
            var history = new List<Rag.HelpKitMessage>(prior.Count);
            foreach (var row in prior)
            {
                var role = string.Equals(row.Role, "user", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.User
                    : ChatRole.Assistant;
                history.Add(new Rag.HelpKitMessage(role, row.Content));
            }
            return history;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load conversation history; continuing with empty context.");
            return Array.Empty<Rag.HelpKitMessage>();
        }
    }

    private static async Task<float[]> EmbedQueryAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        string question,
        CancellationToken ct)
    {
        var result = await embeddings.GenerateAsync(new[] { question }, options: null, ct).ConfigureAwait(false);
        return result.Count == 0 ? Array.Empty<float>() : result[0].Vector.ToArray();
    }

    private static string TruncateTitle(string question)
    {
        var trimmed = question.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "…";
    }

    private static string AppInfoSafeName()
    {
        try { return Microsoft.Maui.ApplicationModel.AppInfo.Current.Name ?? "this app"; }
        catch { return "this app"; }
    }
}
