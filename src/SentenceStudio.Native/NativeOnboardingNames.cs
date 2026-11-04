using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Source onboarding name suggestions (Onboarding.razor LoadSuggestedNames,
/// NameGenerationService): eight names in the target language's script.
/// </summary>
public interface INativeOnboardingNames
{
    Task<ImmutableArray<string>> SuggestAsync(
        string owner, string targetLanguage, CancellationToken cancellationToken);
}

/// <summary>
/// Sends the source prompt through the owner-bound API gateway and keeps the
/// source rules: exactly eight names, otherwise the built-in names for the
/// language. A host without a gateway, an offline device or a failed call gets
/// the built-in names, as the source does without an AI key or connection.
/// </summary>
public sealed class NativeOnboardingNames(
    IServiceProvider services, ILogger<NativeOnboardingNames> logger) : INativeOnboardingNames
{
    public async Task<ImmutableArray<string>> SuggestAsync(
        string owner, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(targetLanguage))
            return [];
        if (services.GetService<IOwnerBoundNativeAiGateway>() is { } gateway)
        {
            try
            {
                var response = await gateway.SendPromptAsync<string>(
                    owner, NameGenerationService.BuildPrompt(targetLanguage), cancellationToken);
                if (NameGenerationService.ParseNames(response) is { } names)
                    return [.. names];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Onboarding name suggestions fell back to the built-in names.");
            }
        }
        return [.. NameGenerationService.GetHardcodedNames(targetLanguage)];
    }
}
