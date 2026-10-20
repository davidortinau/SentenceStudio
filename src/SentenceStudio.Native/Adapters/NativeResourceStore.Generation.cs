using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scriban;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

public sealed partial class NativeResourceStore
{
    private readonly SemaphoreSlim _starterGate = new(1, 1);

    public async Task<NativeResourceCreateResult> CreateStarterAsync(
        string owner, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return new(NativeResourceMutationResult.NotFoundOrForbidden);
        await _starterGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsActiveOwner(owner))
                return new(NativeResourceMutationResult.NotFoundOrForbidden);
            return await CreateStarterCoreAsync(owner, cancellationToken);
        }
        finally
        {
            _starterGate.Release();
        }
    }

    private async Task<NativeResourceCreateResult> CreateStarterCoreAsync(
        string owner, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.UserProfiles.AsNoTracking()
            .Where(user => user.Id == owner)
            .Select(user => new { user.NativeLanguage, user.TargetLanguage })
            .SingleOrDefaultAsync(cancellationToken);
        if (profile is null)
            return new(NativeResourceMutationResult.NotFoundOrForbidden);
        if (string.IsNullOrWhiteSpace(profile.NativeLanguage))
            return new(NativeResourceMutationResult.Invalid,
                ErrorKey: "Resources_SetNativeLanguage");
        if (string.IsNullOrWhiteSpace(profile.TargetLanguage))
            return new(NativeResourceMutationResult.Invalid,
                ErrorKey: "NativeResources_SetTargetLanguage");
        var targetLanguage = profile.TargetLanguage;
        if (await scope.ServiceProvider.GetRequiredService<LearningResourceRepository>()
                .StarterResourceExistsAsync(
                targetLanguage, userProfileId: owner))
            return new(NativeResourceMutationResult.AlreadyPresent,
                ErrorKey: "Resources_StarterExists");

        var gateway = scope.ServiceProvider.GetService<IOwnerBoundNativeAiGateway>()
            ?? throw new InvalidOperationException(
                "Native resource starter requires an owner-bound AI gateway.");
        var prompt = await RenderPromptAsync("GetStarterVocabulary.scriban-txt", new
        {
            native_language = profile.NativeLanguage,
            target_language = targetLanguage
        }, cancellationToken);
        var response = await gateway.SendPromptAsync<string>(
            owner, prompt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner))
            return new(NativeResourceMutationResult.NotFoundOrForbidden);
        if (string.IsNullOrWhiteSpace(response))
            return new(NativeResourceMutationResult.Failed,
                ErrorKey: "NativeResources_StarterFailed");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = VocabularyWord.ParseVocabularyWords(response)
            .Where(word => !string.IsNullOrWhiteSpace(word.TargetLanguageTerm)
                && !string.IsNullOrWhiteSpace(word.NativeLanguageTerm)
                && seen.Add(word.TargetLanguageTerm.Trim()))
            .Select(word => new NativeResourceVocabularyRow(word.Id,
                word.TargetLanguageTerm!.Trim(), word.NativeLanguageTerm!.Trim(),
                VocabularyClassificationBackfillService.ClassifyHeuristic(
                    word.TargetLanguageTerm!, word.Tags),
                Language: targetLanguage))
            .ToImmutableArray();
        if (rows.IsDefaultOrEmpty)
            return new(NativeResourceMutationResult.Failed,
                ErrorKey: "NativeResources_StarterFailed");

        return await CreateAsync(owner,
            new NativeResourceDraft(
                "Sentence Studio Starter Vocabulary",
                $"Starter vocabulary for learning {targetLanguage}",
                "starter,vocabulary",
                MediaType: "Vocabulary List", Language: targetLanguage,
                StagedVocabulary: rows), cancellationToken);
    }

    public async Task<IReadOnlyList<NativeResourceVocabularyRow>> GenerateVocabularyAsync(
        string owner, string resourceId, CancellationToken cancellationToken) =>
        (await GenerateVocabularyCoreAsync(owner, resourceId, null, cancellationToken)).Words;

    public Task<NativeResourceGeneratedVocabulary> GenerateVocabularyFromDraftAsync(
        string owner, string resourceId, NativeResourceDraft draft,
        CancellationToken cancellationToken) =>
        GenerateVocabularyCoreAsync(owner, resourceId,
            draft ?? throw new ArgumentNullException(nameof(draft)), cancellationToken);

    private async Task<NativeResourceGeneratedVocabulary> GenerateVocabularyCoreAsync(
        string owner, string resourceId, NativeResourceDraft? draft,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            throw new UnauthorizedAccessException("Vocabulary generation requires an active owner.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = await db.LearningResources.AsNoTracking()
            .Where(row => row.Id == resourceId && row.UserProfileId == owner
                && !row.IsSmartResource)
            .Select(row => new { row.Transcript, row.Language })
            .SingleOrDefaultAsync(cancellationToken);
        var transcript = draft is null ? resource?.Transcript : draft.Transcript;
        if (resource is null || string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException(
                "Vocabulary generation needs an owned editable resource with transcript.");
        var profile = await db.UserProfiles.AsNoTracking()
            .Where(profile => profile.Id == owner)
            .Select(profile => new { profile.NativeLanguage, profile.TargetLanguage })
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(profile?.NativeLanguage))
            throw new InvalidOperationException(
                "Vocabulary generation needs a configured native language.");
        var targetLanguage = draft is null ? resource.Language : draft.Language;
        if (string.IsNullOrWhiteSpace(targetLanguage))
            targetLanguage = profile.TargetLanguage;
        if (string.IsNullOrWhiteSpace(targetLanguage))
            throw new InvalidOperationException(
                "Vocabulary generation needs a configured target language.");

        var gateway = scope.ServiceProvider.GetService<IOwnerBoundNativeAiGateway>()
            ?? throw new InvalidOperationException(
                "Native resource generation requires an owner-bound AI gateway.");
        var prompt = await RenderPromptAsync("ExtractNativeResourceVocabulary.scriban-txt",
            new
            {
                native_language = profile.NativeLanguage,
                target_language = targetLanguage,
                transcript
            }, cancellationToken);
        var generated = await gateway.SendPromptAsync<VocabularyExtractionResponse>(
            owner, prompt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner))
            throw new UnauthorizedAccessException("Vocabulary owner changed during generation.");
        if (generated?.Vocabulary is not { Count: > 0 })
            throw new InvalidOperationException("Vocabulary generation returned no usable words.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<NativeResourceVocabularyRow>();
        foreach (var item in generated.Vocabulary)
        {
            var word = item.ToVocabularyWord(targetLanguage);
            var target = word.TargetLanguageTerm?.Trim();
            var native = word.NativeLanguageTerm?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target))
                continue;
            if (!IsLatinScriptLanguage(targetLanguage))
            {
                var targetIsLatin = IsLikelyLatinScript(target);
                var nativeIsLatin = IsLikelyLatinScript(native);
                if (targetIsLatin && !nativeIsLatin)
                    (target, native) = (native, target);
                else if (targetIsLatin && nativeIsLatin)
                    continue;
            }
            if (!seen.Add(target))
                continue;
            rows.Add(new NativeResourceVocabularyRow(Guid.NewGuid().ToString(),
                target, native,
                word.LexicalUnitType == LexicalUnitType.Unknown
                    ? VocabularyClassificationBackfillService
                        .ClassifyHeuristic(target, word.Tags)
                    : word.LexicalUnitType,
                word.Tags, string.IsNullOrWhiteSpace(word.Language)
                    ? targetLanguage : word.Language, word.Lemma,
                word.PartOfSpeech));
        }
        return new(rows, generated.Vocabulary.Count,
            generated.Vocabulary.Count - rows.Count);
    }

    private static async Task<string> RenderPromptAsync(
        string name, object context, CancellationToken cancellationToken)
    {
        using var stream = typeof(NativeResourceStore).Assembly.GetManifestResourceStream(
            "SentenceStudio.Native.Prompts." + name)
            ?? throw new FileNotFoundException("Native resource prompt is missing.", name);
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException("Native resource prompt failed to parse.");
        return await template.RenderAsync(context);
    }

    private static bool IsLikelyLatinScript(string text)
    {
        var latin = 0;
        var letters = 0;
        foreach (var character in text)
        {
            if (!char.IsLetter(character))
                continue;
            letters++;
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                or >= '\u00C0' and <= '\u024F')
                latin++;
        }
        return letters > 0 && latin * 5 > letters * 4;
    }

    private static bool IsLatinScriptLanguage(string language)
    {
        var normalized = language.Split('(')[0].Trim();
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "English", "Spanish", "French", "German", "Italian", "Portuguese",
            "Dutch", "Swedish", "Norwegian", "Danish", "Finnish", "Polish",
            "Czech", "Romanian", "Hungarian", "Turkish", "Vietnamese",
            "Indonesian", "Malay", "Tagalog", "Swahili"
        }.Contains(normalized);
    }
}
