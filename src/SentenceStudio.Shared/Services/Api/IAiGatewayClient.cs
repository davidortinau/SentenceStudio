namespace SentenceStudio.Services.Api;

public interface IAiGatewayClient
{
    Task<T?> SendPromptAsync<T>(string prompt, CancellationToken cancellationToken = default);
}
