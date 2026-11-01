using Microsoft.Extensions.DependencyInjection;
using Scriban;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Source TeacherService.GradeSentence(userInput, "") and the AI step of
/// TranslationService.TranslateAsync, rendered from the same Scriban
/// templates and sent through the owner-bound gateways. Grading uses the
/// sentence gateway (Reasoning tier, effort "minimal", as the source);
/// translation uses the default Fast tier. Either gateway is absent in
/// preview/offline hosts, which surfaces as the source failure toast.
/// </summary>
public sealed class NativeWritingAi(IServiceProvider services) : INativeWritingAi
{
    private const string GradeTemplate = "SentenceStudio.Native.Prompts.GradeSentence.scriban-txt";
    private const string TranslateTemplate = "SentenceStudio.Native.Prompts.Translate.scriban-txt";

    public async Task<GradeResponse?> GradeAsync(string owner, string sentence,
        string targetLanguage, string nativeLanguage, CancellationToken cancellationToken)
    {
        var gateway = services.GetService<IOwnerBoundNativeSentenceGateway>()
            ?? throw new InvalidOperationException("Writing grading is not available on this host.");
        var prompt = await RenderAsync(GradeTemplate, new
        {
            user_input = sentence,
            user_meaning = string.Empty,
            native_language = nativeLanguage,
            target_language = targetLanguage,
            target_word = (string?)null,
            target_word_meaning = (string?)null
        }, cancellationToken);
        return await gateway.GradeAsync(owner, prompt, cancellationToken);
    }

    public async Task<string?> TranslateAsync(string owner, string text,
        CancellationToken cancellationToken)
    {
        var gateway = services.GetService<IOwnerBoundNativeAiGateway>()
            ?? throw new InvalidOperationException("Writing translation is not available on this host.");
        var prompt = await RenderAsync(TranslateTemplate, new
        {
            user_input = text,
            context = string.Empty
        }, cancellationToken);
        return await gateway.SendPromptAsync<string>(owner, prompt, cancellationToken);
    }

    private static async Task<string> RenderAsync(string resource, object model,
        CancellationToken cancellationToken)
    {
        using var stream = typeof(NativeWritingAi).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The source prompt {resource} is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException($"The source prompt {resource} is invalid.");
        return await template.RenderAsync(model);
    }
}
