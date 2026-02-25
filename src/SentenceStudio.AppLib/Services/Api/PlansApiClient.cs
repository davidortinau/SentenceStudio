using System.Net.Http;
using System.Net.Http.Json;
using SentenceStudio.Contracts.Plans;

namespace SentenceStudio.Services.Api;

public sealed class PlansApiClient(HttpClient httpClient) : IPlansApiClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<GeneratePlanResponse> GeneratePlanAsync(GeneratePlanRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/plans/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GeneratePlanResponse>(cancellationToken: cancellationToken)
            ?? new GeneratePlanResponse();
    }
}
