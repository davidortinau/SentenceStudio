using System.Collections.Generic;
using SentenceStudio.Services.DTOs;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api;

/// <summary>
/// Hard-coded allow-list of Type objects the AI gateway is allowed to hydrate
/// when a client requests structured-output deserialization. Matches the audit
/// W6 finding: never call <c>Type.GetType</c> with attacker-influenced strings.
///
/// Adding a new response DTO requires adding a <c>typeof(...)</c> entry here so
/// any rename surfaces as a build error.
/// </summary>
internal static class AiResponseTypeRegistry
{
    public static readonly IReadOnlyDictionary<string, Type> AllowedTypes = BuildAllowedTypes();

    private static IReadOnlyDictionary<string, Type> BuildAllowedTypes()
    {
        var publicTypes = new[]
        {
            typeof(BulkTranslationResponse),
            typeof(ClozureResponse),
            typeof(DiaryFeedbackResponse),
            typeof(DiaryPromptResponse),
            typeof(FreeTextVocabularyExtractionResponse),
            typeof(GradeResponse),
            typeof(Reply),
            typeof(SentencesResponse),
            typeof(ShadowingSentencesResponse),
            typeof(StorytellerResponse),
            typeof(TranslationResponse),
            typeof(VocabularyExtractionResponse),
            typeof(WordAssociationGradeResponse),
            typeof(GeneratedExampleSentencesDto),
        };

        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var t in publicTypes)
        {
            if (t.FullName is { } fullName)
            {
                map[fullName] = t;
            }
        }

        // ContentClassificationAiResponse is internal to SentenceStudio.Shared,
        // so the Api project can't reference it via typeof. Resolve through the
        // Shared assembly. If the type ever moves or is renamed, the allow-list
        // simply omits it and the endpoint falls back to plain-string responses.
        var sharedAssembly = typeof(Reply).Assembly;
        var classificationType = sharedAssembly.GetType(
            "SentenceStudio.Services.ContentClassificationAiResponse",
            throwOnError: false);
        if (classificationType?.FullName is { } classificationFullName)
        {
            map[classificationFullName] = classificationType;
        }

        return map;
    }
}
