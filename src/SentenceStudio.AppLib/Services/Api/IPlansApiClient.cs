using SentenceStudio.Contracts.Plans;

namespace SentenceStudio.Services.Api;

public interface IPlansApiClient
{
    Task<GeneratePlanResponse> GeneratePlanAsync(GeneratePlanRequest request, CancellationToken cancellationToken = default);
}
