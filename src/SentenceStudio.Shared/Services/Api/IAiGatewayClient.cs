namespace SentenceStudio.Services.Api;

public interface IAiGatewayClient
{
    Task<T?> SendPromptAsync<T>(string prompt, CancellationToken cancellationToken = default);
    Task<T?> SendMessagesAsync<T>(List<(string role, string content)> messages, string? instructions = null, CancellationToken cancellationToken = default);
    Task<string> AnalyzeImageAsync(string prompt, string imageBase64, string mediaType = "image/jpeg", CancellationToken cancellationToken = default);
}
