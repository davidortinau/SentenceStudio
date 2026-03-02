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
