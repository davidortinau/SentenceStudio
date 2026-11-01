using Microsoft.Extensions.DependencyInjection;
using Scriban;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Source TeacherService.GradeDescription: the GradeMyDescription template
/// rendered with the learner's description and the stored image description,
/// sent through the owner-bound sentence gateway (Reasoning tier, effort
/// "minimal", as the source). Unlike the source, a failure is reported to the
/// caller instead of being turned into an empty 0% grade.
/// </summary>
public sealed class NativeSceneAi(IServiceProvider services) : INativeSceneAi
{
    private const string GradeTemplate = "SentenceStudio.Native.Prompts.GradeMyDescription.scriban-txt";

    public async Task<GradeResponse?> GradeAsync(string owner, string description,
        string sceneDescription, string targetLanguage, string nativeLanguage,
        CancellationToken cancellationToken)
    {
        var gateway = services.GetService<IOwnerBoundNativeSentenceGateway>()
            ?? throw new InvalidOperationException("Scene grading is not available on this host.");
        using var stream = typeof(NativeSceneAi).Assembly.GetManifestResourceStream(GradeTemplate)
            ?? throw new InvalidOperationException($"The source prompt {GradeTemplate} is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException($"The source prompt {GradeTemplate} is invalid.");
        var prompt = await template.RenderAsync(new
        {
            my_description = description,
            ai_description = sceneDescription,
            native_language = nativeLanguage,
            target_language = targetLanguage
        });
        return await gateway.GradeAsync(owner, prompt, cancellationToken);
    }
}
