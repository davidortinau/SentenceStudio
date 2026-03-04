using System.Text.Json;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Ai;

namespace SentenceStudio.Services.Api;

public sealed class AiGatewayClient(IAiApiClient aiApiClient, ILogger<AiGatewayClient> logger) : IAiGatewayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAiApiClient _aiApiClient = aiApiClient;
    private readonly ILogger<AiGatewayClient> _logger = logger;

    public async Task<T?> SendPromptAsync<T>(string prompt, CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest
        {
            Message = prompt,
            ResponseType = typeof(T).AssemblyQualifiedName
        };

        var response = await _aiApiClient.SendChatAsync(request, cancellationToken);
        return DeserializeResponse<T>(response);
    }

    public async Task<T?> SendMessagesAsync<T>(List<(string role, string content)> messages, string? instructions = null, CancellationToken cancellationToken = default)
    {
        var request = new ChatMessagesRequest
        {
            Messages = messages.Select(m => new ChatMessageDto { Role = m.role, Content = m.content }).ToList(),
            Instructions = instructions,
            ResponseType = typeof(T) == typeof(string) ? null : typeof(T).AssemblyQualifiedName
        };

        var response = await _aiApiClient.SendChatMessagesAsync(request, cancellationToken);
        return DeserializeResponse<T>(response);
    }

    public async Task<string> AnalyzeImageAsync(string prompt, string imageBase64, string mediaType = "image/jpeg", CancellationToken cancellationToken = default)
    {
        var request = new AnalyzeImageRequest
        {
            Prompt = prompt,
            ImageBase64 = imageBase64,
            MediaType = mediaType
        };

        var response = await _aiApiClient.AnalyzeImageAsync(request, cancellationToken);
        return response.Response ?? string.Empty;
    }

    private T? DeserializeResponse<T>(ChatResponse response)
    {
        if (typeof(T) == typeof(string))
        {
            return (T)(object)(response.Response ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(response.Response))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(response.Response, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize AI gateway response to {ResponseType}", typeof(T).FullName);
            return default;
        }
    }
}
