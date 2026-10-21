using SentenceStudio.Contracts.Ai;

namespace SentenceStudio.Services.Api;

public interface IRealtimeApiClient
{
    /// <summary>Asks the API for a short-lived secret to open one realtime voice session.</summary>
    Task<RealtimeClientSecretResponse> CreateClientSecretAsync(CancellationToken cancellationToken = default);
}
