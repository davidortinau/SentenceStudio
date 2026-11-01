using Scriban;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Source WordAssociationService.GradeCluesAsync: the same
/// GradeWordAssociation Scriban template and model, sent at the Reasoning
/// tier without an effort setting. The gateway is absent in preview/offline
/// hosts, which surfaces as the source grading failure.
/// </summary>
public sealed class NativeWordAssociationAi(IOwnerBoundNativeReasoningAiGateway? gateway)
    : INativeWordAssociationAi
{
    internal const string Template = "SentenceStudio.Native.Prompts.GradeWordAssociation.scriban-txt";

    public async Task<WordAssociationGradeResponse?> GradeAsync(string owner, string targetTerm,
        string? nativeTerm, IReadOnlyList<string> clues, string targetLanguage,
        string nativeLanguage, CancellationToken cancellationToken)
    {
        var ai = gateway ?? throw new InvalidOperationException(
            "Word Association grading is not available on this host.");
        if (clues.Count == 0)
            return new WordAssociationGradeResponse();
        var prompt = await RenderAsync(new
        {
            native_language = nativeLanguage,
            target_language = targetLanguage,
            target_term = targetTerm,
            native_term = nativeTerm,
            entries = clues
        }, cancellationToken);
        return await ai.SendReasoningPromptAsync<WordAssociationGradeResponse>(
            owner, prompt, cancellationToken);
    }

    internal static async Task<string> RenderAsync(object model, CancellationToken cancellationToken)
    {
        using var stream = typeof(NativeWordAssociationAi).Assembly.GetManifestResourceStream(Template)
            ?? throw new InvalidOperationException($"The source prompt {Template} is missing.");
        using var reader = new StreamReader(stream);
        var template = Scriban.Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException($"The source prompt {Template} is invalid.");
        return await template.RenderAsync(model);
    }
}
