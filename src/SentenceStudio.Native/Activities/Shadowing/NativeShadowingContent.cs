using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public enum NativeShadowingContentStatus { Ready, Empty, Failed, Refused }

public sealed record NativeShadowingContentResult(
    NativeShadowingContentStatus Status,
    ImmutableArray<NativeShadowingSentence> Sentences,
    string TargetLanguage);

/// <summary>
/// Source ShadowingService.GetOrGenerateSentencesAsync bound to one owner:
/// a resource transcript yields random extracted sentences (no translation,
/// no AI); otherwise up to 10 random eligible vocabulary words, where Word
/// entries go through the source GetShadowingSentences prompt and
/// Phrase/Sentence/Unknown entries are used as-is. The resource must belong
/// to the owner; every read and the AI result are refused if the owner changes.
/// </summary>
public sealed class NativeShadowingContent(
    NativeActivityOwner owner,
    LearningResourceRepository resources,
    UserProfileRepository profiles,
    IServiceScopeFactory scopes,
    ILogger<NativeShadowingContent> logger,
    IOwnerBoundNativeAiGateway? ai = null)
{
    public const int SentenceCount = 10;
    private const string TemplateResource =
        "SentenceStudio.Native.Prompts.GetShadowingSentences.scriban-txt";
    private readonly TranscriptSentenceExtractor _extractor = new();

    public async Task<NativeShadowingContentResult> LoadAsync(
        string userId, string? resourceId, CancellationToken cancellationToken)
    {
        if (!owner.IsActive(userId))
            return Refused();
        // Source LoadSentences returns silently without a resource id: "No sentences".
        if (string.IsNullOrWhiteSpace(resourceId))
            return new(NativeShadowingContentStatus.Empty, [], "Korean");

        var profile = await profiles.GetByIdAsync(userId, cancellationToken);
        var resource = await resources.GetResourceAsync(resourceId, userId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsActive(userId))
            return Refused();
        if (resource is null || resource.UserProfileId != userId)
        {
            logger.LogWarning("Shadowing resource was not found for the active owner.");
            return new(NativeShadowingContentStatus.Empty, [], "Korean");
        }
        var targetLanguage = resource.Language ?? profile?.TargetLanguage ?? "Korean";

        if (!string.IsNullOrWhiteSpace(resource.Transcript))
        {
            // Transcript mode: pure shadowing, no translation or notes.
            var extracted = _extractor.ExtractRandomSentences(
                resource.Transcript, resource.Language ?? targetLanguage, SentenceCount);
            var transcript = extracted
                .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
                .Select(sentence => new NativeShadowingSentence(sentence.Trim(), null, null))
                .ToImmutableArray();
            return new(transcript.IsEmpty ? NativeShadowingContentStatus.Empty
                : NativeShadowingContentStatus.Ready, transcript, targetLanguage);
        }

        return await GenerateAsync(userId, resource, profile, targetLanguage, cancellationToken);
    }

    private async Task<NativeShadowingContentResult> GenerateAsync(
        string userId, LearningResource resource, UserProfile? profile,
        string targetLanguage, CancellationToken cancellationToken)
    {
        var vocabulary = (resource.Vocabulary ?? [])
            .Where(word => !string.IsNullOrWhiteSpace(word.TargetLanguageTerm))
            .GroupBy(word => word.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (vocabulary.Count == 0)
            return new(NativeShadowingContentStatus.Empty, [], targetLanguage);

        // Exclude Familiar words in their grace period (source, like Cloze/Translation).
        Dictionary<string, VocabularyProgress> progress;
        using (var scope = scopes.CreateScope())
            progress = await scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>()
                .GetProgressForWordsAsync(vocabulary.Select(word => word.Id).ToList(), userId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsActive(userId))
            return Refused();
        var words = vocabulary
            .Where(word => !progress.TryGetValue(word.Id, out var item) || !item.IsInGracePeriod)
            .OrderBy(_ => Random.Shared.Next())
            .Take(SentenceCount)
            .ToList();
        var nativeLanguage = profile?.NativeLanguage ?? "English";
        var needAi = words.Where(word => word.LexicalUnitType == LexicalUnitType.Word).ToList();
        var asIs = words
            .Where(word => word.LexicalUnitType is LexicalUnitType.Phrase
                or LexicalUnitType.Sentence or LexicalUnitType.Unknown)
            .Select(word => new NativeShadowingSentence(
                word.TargetLanguageTerm!.Trim(),
                string.IsNullOrWhiteSpace(word.NativeLanguageTerm) ? null : word.NativeLanguageTerm.Trim(),
                null))
            .ToList();

        var generated = new List<NativeShadowingSentence>();
        var aiFailed = false;
        if (needAi.Count > 0)
        {
            try
            {
                if (ai is null)
                    throw new InvalidOperationException("The owner-bound AI gateway is unavailable.");
                var prompt = await RenderPromptAsync(needAi, nativeLanguage, targetLanguage, cancellationToken);
                var response = await ai.SendPromptAsync<ShadowingSentencesResponse>(
                    userId, prompt, cancellationToken);
                generated.AddRange((response?.Sentences ?? [])
                    .Where(sentence => !string.IsNullOrWhiteSpace(sentence.TargetLanguageText))
                    .Select(sentence => new NativeShadowingSentence(
                        sentence.TargetLanguageText!.Trim(),
                        string.IsNullOrWhiteSpace(sentence.NativeLanguageText)
                            ? null : sentence.NativeLanguageText.Trim(),
                        string.IsNullOrWhiteSpace(sentence.PronunciationNotes)
                            ? null : sentence.PronunciationNotes.Trim())));
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                // Source GenerateSentencesAsync logs and keeps the as-is sentences.
                logger.LogError("Shadowing sentence generation failed: {Failure}.", ex.GetType().Name);
                aiFailed = true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsActive(userId))
            return Refused();

        // Source order: as-is entries first, then AI sentences.
        var sentences = asIs.Concat(generated).ToImmutableArray();
        if (sentences.IsEmpty)
            return new(aiFailed ? NativeShadowingContentStatus.Failed
                : NativeShadowingContentStatus.Empty, [], targetLanguage);
        return new(NativeShadowingContentStatus.Ready, sentences, targetLanguage);
    }

    private static async Task<string> RenderPromptAsync(
        IReadOnlyList<VocabularyWord> terms, string nativeLanguage, string targetLanguage,
        CancellationToken cancellationToken)
    {
        await using var stream = typeof(NativeShadowingContent).Assembly
            .GetManifestResourceStream(TemplateResource)
            ?? throw new InvalidOperationException("The source shadowing template is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException("The source shadowing template is invalid.");
        return await template.RenderAsync(new
        {
            terms,
            native_language = nativeLanguage,
            target_language = targetLanguage
        });
    }

    private static NativeShadowingContentResult Refused() =>
        new(NativeShadowingContentStatus.Refused, [], "Korean");
}
