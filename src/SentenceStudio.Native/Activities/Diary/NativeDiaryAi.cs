using Microsoft.Extensions.DependencyInjection;
using Scriban;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Source DiaryService.GeneratePromptAsync / GetFeedbackAsync rendered from
/// the same Scriban templates and sent through the owner-bound gateway:
/// prompt generation on the default Fast tier, feedback on the Reasoning
/// tier, as the source. A host without a configured API has no gateway, which
/// surfaces as the source failure toast.
/// </summary>
public sealed class NativeDiaryAi(IServiceProvider services) : INativeDiaryAi
{
    private const string PromptTemplate = "SentenceStudio.Native.Prompts.GenerateDiaryPrompt.scriban-txt";
    private const string FeedbackTemplate = "SentenceStudio.Native.Prompts.GradeDiaryEntry.scriban-txt";

    public async Task<DiaryPromptResponse?> GeneratePromptAsync(string owner,
        string targetLanguage, string nativeLanguage, CancellationToken cancellationToken)
    {
        var gateway = Gateway();
        var prompt = await RenderAsync(PromptTemplate, new
        {
            target_language = targetLanguage,
            native_language = nativeLanguage
        }, cancellationToken);
        return await gateway.SendPromptAsync<DiaryPromptResponse>(
            owner, prompt, AiTier.Fast, cancellationToken);
    }

    public async Task<DiaryFeedbackResponse?> GetFeedbackAsync(string owner, string content,
        string? promptText, string targetLanguage, string nativeLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        var gateway = Gateway();
        var prompt = await RenderAsync(FeedbackTemplate, new
        {
            target_language = targetLanguage,
            native_language = nativeLanguage,
            prompt_text = promptText ?? string.Empty,
            entry_content = content
        }, cancellationToken);
        return await gateway.SendPromptAsync<DiaryFeedbackResponse>(
            owner, prompt, AiTier.Reasoning, cancellationToken);
    }

    private IOwnerBoundNativeAiGateway Gateway() =>
        services.GetService<IOwnerBoundNativeAiGateway>()
        ?? throw new InvalidOperationException("Diary AI is not available on this host.");

    private static async Task<string> RenderAsync(string resource, object model,
        CancellationToken cancellationToken)
    {
        using var stream = typeof(NativeDiaryAi).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The source prompt {resource} is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException($"The source prompt {resource} is invalid.");
        return await template.RenderAsync(model);
    }
}
