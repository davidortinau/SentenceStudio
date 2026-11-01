using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Production Translation AI over the existing owner-bound API gateways:
/// generation through <see cref="IOwnerBoundNativeAiGateway"/> (source
/// AiService default tier) and grading through
/// <see cref="IOwnerBoundNativeSentenceGateway"/> (Reasoning tier, effort
/// "minimal", as TeacherService.GradeTranslation). A head without a configured
/// API has neither gateway and every call fails instead of guessing.
/// </summary>
public sealed class OwnerBoundNativeTranslationAi(
    IOwnerBoundNativeAiGateway? generation,
    IOwnerBoundNativeSentenceGateway? grading) : INativeTranslationAi
{
    public Task<TranslationResponse?> GenerateAsync(
        string owner, string renderedPrompt, CancellationToken cancellationToken) =>
        (generation ?? throw new InvalidOperationException(
                "Translation sentence generation is not configured."))
            .SendPromptAsync<TranslationResponse>(owner, renderedPrompt, cancellationToken);

    public Task<GradeResponse?> GradeAsync(
        string owner, string renderedPrompt, CancellationToken cancellationToken) =>
        (grading ?? throw new InvalidOperationException(
                "Translation grading is not configured."))
            .GradeAsync(owner, renderedPrompt, cancellationToken);
}
