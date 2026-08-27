using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Services.Api;

/// <summary>
/// Authenticated HTTP client for the coach API group. Follows the repository's existing typed
/// client pattern (constructor-injected <see cref="HttpClient"/> supplied by
/// <c>AddHttpClient&lt;TInterface, TImplementation&gt;</c> with the auth + activity handlers).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every request and every response goes through <see cref="WireJson.Client"/>.</b> That is the
/// one place enum tolerance is installed, so a server that ships a new member of a coach enum
/// degrades one card rather than throwing inside <c>ReadFromJsonAsync</c> and taking the whole
/// conversation down with it. A new call site that forgets the options is a call site that is
/// strict again, which is why the read path funnels through a single
/// <see cref="ReadAsync{T}"/> helper instead of calling <c>ReadFromJsonAsync</c> per method.
/// </para>
/// <para>
/// It is deliberately the client's options and not the server's. The API keeps strict binding, so
/// a bad value in a learner's request body is still a bad request, and the coach's structured
/// model output stays strict so a model cannot invent an enum member.
/// </para>
/// </remarks>
public sealed class CoachApiClient : ICoachApiClient
{
    private const string BasePath = "/api/v1/coach";
    private const string MemoriesPath = BasePath + "/memories";

    private readonly HttpClient _httpClient;

    public CoachApiClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;

        // The client-adoption gate's client half. The server does not read this yet — no enum
        // value is gated at the current revision — but the header has to be in the field before a
        // gate can ever be useful, because a gate can only hold a value back from clients that
        // announced themselves. A server that ignores an unknown header is unaffected.
        if (!_httpClient.DefaultRequestHeaders.Contains(WireHeaders.ClientProtocolVersion))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                WireHeaders.ClientProtocolVersion,
                WireProtocolVersion.Current.ToString(CultureInfo.InvariantCulture));
        }
    }

    public async Task<CoachAvailabilityResponse> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"{BasePath}/availability", cancellationToken)
            .ConfigureAwait(false);

        // Feature flag off, learner outside the cohort, or route group absent: the whole group 404s.
        // That is "no entry point", not an error worth surfacing.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Unavailable();
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<CoachAvailabilityResponse>(response, cancellationToken).ConfigureAwait(false)
            ?? Unavailable();
    }

    public async Task<CoachSessionResponse> StartSessionAsync(StartCoachSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _httpClient.PostAsJsonAsync($"{BasePath}/sessions", request, WireJson.Client, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync<CoachSessionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachSessionResponse?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var response = await _httpClient.GetAsync($"{BasePath}/sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken)
            .ConfigureAwait(false);

        // A non-owner and a missing session are indistinguishable by design (both 404).
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachSessionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<CoachTurnResponse> SubmitTurnAsync(string sessionId, CoachTurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        return PostForTurnAsync($"{BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/turns", request, cancellationToken);
    }

    public Task<CoachTurnResponse> AcceptSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionId);
        ArgumentNullException.ThrowIfNull(request);

        var path = $"{BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/suggestions/{Uri.EscapeDataString(suggestionId)}/accept";
        return PostForTurnAsync(path, request, cancellationToken);
    }

    public Task<CoachTurnResponse> RejectSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionId);
        ArgumentNullException.ThrowIfNull(request);

        var path = $"{BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/suggestions/{Uri.EscapeDataString(suggestionId)}/reject";
        return PostForTurnAsync(path, request, cancellationToken);
    }

    public Task<CoachTurnResponse> UndoAsync(string sessionId, CoachUndoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        return PostForTurnAsync($"{BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/undo", request, cancellationToken);
    }

    public async Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var response = await _httpClient
            .PostAsync($"{BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/cancel", content: null, cancellationToken)
            .ConfigureAwait(false);

        // Nothing running, or the session is already gone: the learner pressed Stop and the
        // desired end state is reached either way.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var response = await _httpClient.DeleteAsync($"{BasePath}/sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken)
            .ConfigureAwait(false);

        // Already gone is the desired end state for a delete.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ conversations

    private const string ConversationsPath = BasePath + "/conversations";

    public async Task<CoachConversationDto> CreateConversationAsync(
        StartCoachConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, ConversationsPath)
        {
            Content = JsonContent.Create(request, options: WireJson.Client)
        };

        // Also sent as a header so a proxy or a generic retry layer can see that this request is
        // safe to repeat without having to understand the coach's body shape.
        AddIdempotencyKey(message, request.IdempotencyKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync<CoachConversationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachConversationPageDto?> ListConversationsAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var query = Query(("pageSize", pageSize?.ToString()), ("cursor", cursor));

        using var response = await _httpClient.GetAsync($"{ConversationsPath}{query}", cancellationToken)
            .ConfigureAwait(false);

        // The whole conversations group answers 404 when durable history is switched off, so the
        // list doubles as the feature probe. Reading that as null keeps "the feature is not on
        // here" out of the exception path, where a caller would have to catch to learn something
        // that is not an error.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync<CoachConversationPageDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachConversationDto?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        using var response = await _httpClient
            .GetAsync($"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}", cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachConversationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachMessagePageDto?> GetConversationMessagesAsync(
        string conversationId,
        int? pageSize = null,
        string? before = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var query = Query(("pageSize", pageSize?.ToString()), ("before", before));
        var path = $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}/messages{query}";

        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachMessagePageDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachConversationDto> UpdateConversationAsync(
        string conversationId,
        UpdateCoachConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}")
        {
            Content = JsonContent.Create(request, options: WireJson.Client)
        };

        if (request.ExpectedStateVersion is { } version)
        {
            message.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync<CoachConversationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachTurnOperationDto> SubmitConversationTurnAsync(
        string conversationId,
        CoachConversationTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(request);

        // Both handles are filled before the request leaves, and both are the caller's to keep.
        // The idempotency key makes a retry harmless; the operation id makes a lost response
        // recoverable. A caller that supplies neither would have no way to find out what happened
        // to a turn whose response never arrived, so the client refuses to send one that way.
        var sent = request;
        if (string.IsNullOrWhiteSpace(sent.OperationId) || string.IsNullOrWhiteSpace(sent.IdempotencyKey))
        {
            sent = sent with
            {
                OperationId = string.IsNullOrWhiteSpace(sent.OperationId) ? NewClientId() : sent.OperationId,
                IdempotencyKey = string.IsNullOrWhiteSpace(sent.IdempotencyKey) ? NewClientId() : sent.IdempotencyKey
            };
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}/turns")
        {
            Content = JsonContent.Create(sent, options: WireJson.Client)
        };

        AddIdempotencyKey(message, sent.IdempotencyKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync<CoachTurnOperationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachTurnOperationDto?> GetConversationOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var path = $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}" +
                   $"/operations/{Uri.EscapeDataString(operationId)}";

        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachTurnOperationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachTurnOperationDto?> CancelConversationTurnAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var path = $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}" +
                   $"/operations/{Uri.EscapeDataString(operationId)}/cancel";

        using var response = await _httpClient.PostAsync(path, content: null, cancellationToken)
            .ConfigureAwait(false);

        // The learner pressed Stop. A turn that has already finished or vanished is the desired
        // end state, so the UI releases either way.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachTurnOperationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ proposed changes

    /// <summary>The header the one-use confirmation travels in.</summary>
    /// <remarks>
    /// The same literal as the server's <c>CoachWriteHeaders.Confirmation</c>. It is written out
    /// here rather than shared, because the client cannot reference the API assembly and a wire
    /// name is a contract in its own right; both sides pin the literal in their own tests.
    /// </remarks>
    internal const string ConfirmationHeader = "X-Coach-Write-Confirmation";

    public Task<CoachWriteOperationDto?> GetWriteOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        return SendWriteAsync(
            HttpMethod.Get, conversationId, operationId, segment: null, confirmation: null, cancellationToken);
    }

    public Task<CoachWriteOperationDto?> AcceptWriteAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        return SendWriteAsync(
            HttpMethod.Post, conversationId, operationId, "accept", confirmation: null, cancellationToken);
    }

    public Task<CoachWriteOperationDto?> RejectWriteAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        return SendWriteAsync(
            HttpMethod.Post, conversationId, operationId, "reject", confirmation: null, cancellationToken);
    }

    public async Task<CoachWriteConfirmation?> RequestWriteConfirmationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        using var response = await _httpClient
            .PostAsync(WritePath(conversationId, operationId, "confirmation"), content: null, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachWriteConfirmation>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<CoachWriteOperationDto?> ConfirmWriteAsync(
        string conversationId,
        string operationId,
        CoachWriteConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(confirmation);

        return SendWriteAsync(
            HttpMethod.Post, conversationId, operationId, "confirm", confirmation, cancellationToken);
    }

    public Task<CoachWriteOperationDto?> UndoWriteAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        return SendWriteAsync(
            HttpMethod.Post, conversationId, operationId, "undo", confirmation: null, cancellationToken);
    }

    /// <summary>
    /// Sends one write-approval request and reads the state it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method for all six because the differences between them are the verb and one path
    /// segment, and the things that must not differ — no request body, the confirmation in a
    /// header and never in the URL, a 404 read as an indistinguishable not-found — are exactly
    /// the things worth writing once.
    /// </para>
    /// <para>
    /// Every route answers with the operation's state afterwards, so a caller never has to infer
    /// what happened from a status code. That matters most for the cases a 200 cannot describe:
    /// a replayed acceptance, a reversal that has already closed its window, a decline that found
    /// the change already applied.
    /// </para>
    /// </remarks>
    private async Task<CoachWriteOperationDto?> SendWriteAsync(
        HttpMethod method,
        string conversationId,
        string operationId,
        string? segment,
        CoachWriteConfirmation? confirmation,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, WritePath(conversationId, operationId, segment));

        if (confirmation is not null)
        {
            // TryAddWithoutValidation, because a rejected header would otherwise throw and the
            // resulting exception message is built from the value.
            message.Headers.TryAddWithoutValidation(ConfirmationHeader, confirmation.Value);
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachWriteOperationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    private static string WritePath(string conversationId, string operationId, string? segment)
    {
        var path = $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}" +
                   $"/writes/{Uri.EscapeDataString(operationId)}";

        return segment is null ? path : $"{path}/{segment}";
    }

    public async Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        using var response = await _httpClient
            .DeleteAsync($"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}", cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> ExportConversationAsync(
        string conversationId,
        CoachExportFormat format = CoachExportFormat.Json,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var path = $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}/export?format={format}";

        // Headers-only: the body is a stream the caller consumes, so a long export starts saving
        // immediately instead of being buffered whole on a phone.
        var response = await _httpClient
            .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var content = response.Content;
            response = null!;
            return await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// A fresh opaque client identifier for a turn.
    /// </summary>
    /// <remarks>
    /// Random rather than derived from the request, because two identical turns sent deliberately
    /// are two turns. Deriving the id from the payload would silently collapse them into one.
    /// </remarks>
    private static string NewClientId() => Guid.NewGuid().ToString("n");

    private static void AddIdempotencyKey(HttpRequestMessage message, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }
    }

    private static string Query(params (string Name, string? Value)[] parts)
    {
        var pairs = parts
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}")
            .ToArray();

        return pairs.Length == 0 ? string.Empty : "?" + string.Join('&', pairs);
    }

    private async Task<CoachTurnResponse> PostForTurnAsync<TRequest>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(path, request, WireJson.Client, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<CoachTurnResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The answer when the server could not be reached or answered unusably.</summary>
    /// <remarks>
    /// The feature flags are deliberately left at their <see langword="false"/> defaults rather
    /// than set explicitly: a client that could not reach the server knows nothing about which
    /// features that server has, and must not claim durable history or memory are usable.
    /// </remarks>
    private static CoachAvailabilityResponse Unavailable() => new()
    {
        IsAvailable = false,
        State = CoachAvailabilityState.Disabled
    };

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(WireJson.Client, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var value = await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            throw new CoachApiException(
                response.StatusCode,
                problemType: null,
                title: "Empty coach response",
                detail: $"Expected a {typeof(T).Name} body.");
        }

        return value;
    }

    /// <summary>
    /// Translates a non-success response into a <see cref="CoachApiException"/> carrying the
    /// RFC 7807 problem type when one is present.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? problemType = null;
        string? title = null;
        string? detail = null;

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    problemType = ReadString(document.RootElement, "type");
                    title = ReadString(document.RootElement, "title");
                    detail = ReadString(document.RootElement, "detail");
                }
            }
        }
        catch (JsonException)
        {
            // Non-problem body (HTML error page, plain text). Fall through with the status code only.
        }

        throw new CoachApiException(response.StatusCode, problemType, title, detail);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ================================================================ what Sam remembers

    public async Task<CoachMemoryPageDto?> ListActiveMemoriesAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => await GetMemoryPageAsync(MemoriesPath, pageSize, cursor, cancellationToken).ConfigureAwait(false);

    public async Task<CoachMemoryPageDto?> ListMemoryCandidatesAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => await GetMemoryPageAsync($"{MemoriesPath}/candidates", pageSize, cursor, cancellationToken)
            .ConfigureAwait(false);

    public async Task<CoachMemoryFactDto?> ApproveMemoryAsync(
        string factId,
        CoachMemoryApproveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _httpClient
            .PostAsJsonAsync($"{MemoriesPath}/{Uri.EscapeDataString(factId)}/approve", request, WireJson.Client, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachMemoryFactDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectMemoryAsync(
        string factId,
        CoachMemoryRejectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _httpClient
            .PostAsJsonAsync($"{MemoriesPath}/{Uri.EscapeDataString(factId)}/reject", request, WireJson.Client, cancellationToken)
            .ConfigureAwait(false);

        // Declining something that is already gone reached the outcome the learner asked for.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachMemoryFactDto?> EditMemoryAsync(
        string factId,
        CoachMemoryEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _httpClient
            .PutAsJsonAsync($"{MemoriesPath}/{Uri.EscapeDataString(factId)}", request, WireJson.Client, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachMemoryFactDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetMemoryAsync(string factId, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);

        using var response = await _httpClient
            .DeleteAsync($"{MemoriesPath}/{Uri.EscapeDataString(factId)}?expectedVersion={expectedVersion}", cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachMemoryForgetAllResponse?> ForgetAllMemoriesAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(MemoriesPath, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachMemoryForgetAllResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one page of memory facts, treating a 404 as "there is no memory surface here".
    /// </summary>
    /// <remarks>
    /// The feature flag being off, the learner being outside the cohort, and the fact belonging to
    /// somebody else all answer 404, and this method keeps them indistinguishable. Telling them
    /// apart would turn the endpoint into a probe for whether another learner's data exists.
    /// </remarks>
    private async Task<CoachMemoryPageDto?> GetMemoryPageAsync(
        string path,
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var query = Query(
            ("pageSize", pageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("cursor", cursor));

        using var response = await _httpClient.GetAsync(path + query, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachMemoryPageDto>(response, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ response reports

    public async Task<CoachReportedResponsesDto?> GetReportedResponsesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var path = $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}/responses/reported";

        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

        // Reporting switched off answers 404 exactly as an unknown route does, so this read
        // doubles as the feature probe. Null means "do not offer the control"; an empty list
        // means "the control is available and nothing here is reported yet".
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachReportedResponsesDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoachResponseReportResponse?> ReportResponseAsync(
        string conversationId,
        string messageId,
        CoachResponseReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(request);

        var path = $"{ConversationsPath}/{Uri.EscapeDataString(conversationId)}" +
                   $"/responses/{Uri.EscapeDataString(messageId)}/report";

        using var response = await _httpClient.PostAsJsonAsync(path, request, WireJson.Client, cancellationToken)
            .ConfigureAwait(false);

        // Reporting off, an unknown conversation, and somebody else's conversation are one
        // answer. Keeping them indistinguishable here is what stops a caller from turning the
        // route into a probe for whether a conversation exists.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CoachResponseReportResponse>(response, cancellationToken).ConfigureAwait(false);
    }
}
