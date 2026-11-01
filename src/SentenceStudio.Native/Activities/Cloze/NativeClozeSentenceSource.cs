using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>The AI could not produce usable cloze sentences; surfaced to the learner, never shown as an empty list.</summary>
public sealed class NativeClozeGenerationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Owner-bound port of the source ClozureService.GetSentences: focus words
/// (required first) -> due words from the owner's full pool (DueOnly or no
/// resource) -> resource vocabulary (grace-period words excluded, 40 sampled),
/// rendered through the source GetClozuresV2 prompt on the Fast tier, then the
/// same self-healing of the as-used form and the five guesses. Unlike the
/// source, an AI failure throws <see cref="NativeClozeGenerationException"/>
/// instead of returning an empty list, and every read takes the explicit owner.
/// </summary>
public sealed class NativeClozeSentenceSource(
    NativeActivityOwner owner,
    LearningResourceRepository resources,
    SkillProfileRepository skills,
    UserProfileRepository profiles,
    IServiceScopeFactory scopes,
    ILogger<NativeClozeSentenceSource> logger,
    TimeProvider? clock = null)
{
    public const int MaxVocabSample = 40;
    private const string TemplateResource =
        "SentenceStudio.Native.Prompts.GetClozuresV2.scriban-txt";

    /// <summary>Returns no sentences only when the owner has no eligible vocabulary.</summary>
    public async Task<IReadOnlyList<NativeClozeChallenge>> GetSentencesAsync(
        NativeActivityLaunch launch, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var userId = launch.UserId;
        RequireOwner(userId);
        // Source Cloze.razor: DueOnly ignores the resource for content selection.
        var resourceId = launch.DueOnly ? null
            : launch.ResourceIds.IsDefaultOrEmpty ? null : launch.ResourceIds[0];
        var skillId = string.IsNullOrWhiteSpace(launch.SkillId) ? null : launch.SkillId;

        if (!launch.FocusVocabularyIds.IsDefaultOrEmpty)
        {
            var focus = await FromFocusWordsAsync(userId, launch.FocusVocabularyIds,
                resourceId, count, skillId, cancellationToken);
            if (focus.Count > 0)
                return focus;
            logger.LogWarning("Cloze focus vocabulary produced no sentences; using the source fallback selection.");
        }
        else
            logger.LogWarning("Cloze launched without focus vocabulary; using the source fallback selection.");

        if (resourceId is null)
            return await FromDueWordsAsync(userId, count, skillId, cancellationToken);

        var resource = await resources.GetResourceAsync(resourceId, userId);
        RequireOwner(userId);
        if (resource is null || resource.UserProfileId != userId
            || resource.Vocabulary is not { Count: > 0 } vocabulary)
            return [];
        var all = vocabulary.Where(FocusVocabularySelection.IsUsableVocabularyWord).ToList();
        var progress = await ProgressAsync(userId, all, cancellationToken);
        var eligible = all
            .Where(word => !progress.TryGetValue(word.Id, out var row) || !row.IsInGracePeriod)
            .ToList();
        return await GenerateAsync(userId, Sample(eligible), count, skillId,
            resource.Language, null, cancellationToken);
    }

    private async Task<IReadOnlyList<NativeClozeChallenge>> FromFocusWordsAsync(
        string userId, ImmutableArray<string> focusIds, string? resourceId, int count,
        string? skillId, CancellationToken cancellationToken)
    {
        var all = await resources.GetAllVocabularyWordsWithResourcesAsync(userId);
        RequireOwner(userId);
        var focus = FocusVocabularySelection.SelectFocusWords(all, focusIds);
        if (focus.Count == 0)
            return [];

        var context = new List<VocabularyWord>();
        string? targetLanguage = null;
        if (resourceId is not null)
        {
            var resource = await resources.GetResourceAsync(resourceId, userId);
            if (resource?.UserProfileId == userId && resource.Vocabulary is { Count: > 0 } words)
            {
                context.AddRange(words);
                targetLanguage = resource.Language;
            }
        }
        if (context.Count == 0)
            context.AddRange(await DueWordsAsync(userId, all, cancellationToken));
        var prompt = FocusVocabularySelection.BuildRequiredFirstPromptVocabulary(focus, context);
        return await GenerateAsync(userId, prompt, count, skillId, targetLanguage,
            focus, cancellationToken);
    }

    private async Task<IReadOnlyList<NativeClozeChallenge>> FromDueWordsAsync(
        string userId, int count, string? skillId, CancellationToken cancellationToken)
    {
        var all = await resources.GetAllVocabularyWordsWithResourcesAsync(userId);
        RequireOwner(userId);
        var due = await DueWordsAsync(userId, all, cancellationToken);
        if (due.Count == 0)
            return [];
        return await GenerateAsync(userId, Sample(due), count, skillId, null, null,
            cancellationToken);
    }

    /// <summary>
    /// Source due filter: unseen, never attempted, or review date reached; grace period excluded.
    /// NextReviewDate is a UTC instant (VocabularyProgress.IsDueForReview, the quiz's
    /// VocabQuizSourcePolicy), so it is compared with the current UTC instant; the source's
    /// device-local DateTime.Now made words due early east of UTC and late west of it.
    /// </summary>
    private async Task<List<VocabularyWord>> DueWordsAsync(
        string userId, List<VocabularyWord> all, CancellationToken cancellationToken)
    {
        var usable = all.Where(FocusVocabularySelection.IsUsableVocabularyWord).ToList();
        var progress = await ProgressAsync(userId, usable, cancellationToken);
        var nowUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return usable.Where(word =>
        {
            if (!progress.TryGetValue(word.Id, out var row))
                return true;
            if (row.IsInGracePeriod)
                return false;
            return row.TotalAttempts == 0
                || row.NextReviewDate is { } next
                    && DateTime.SpecifyKind(next, DateTimeKind.Utc) <= nowUtc;
        }).ToList();
    }

    private async Task<Dictionary<string, VocabularyProgress>> ProgressAsync(
        string userId, List<VocabularyWord> words, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (words.Count == 0)
            return [];
        using var scope = scopes.CreateScope();
        var progress = await scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>()
            .GetProgressForWordsAsync(words.Select(word => word.Id).ToList(), userId);
        RequireOwner(userId);
        return progress;
    }

    private static List<VocabularyWord> Sample(List<VocabularyWord> words) =>
        words.Count > MaxVocabSample
            ? words.OrderBy(_ => Random.Shared.Next()).Take(MaxVocabSample).ToList()
            : words;

    private async Task<IReadOnlyList<NativeClozeChallenge>> GenerateAsync(
        string userId, List<VocabularyWord> words, int count, string? skillId,
        string? targetLanguage, IReadOnlyList<VocabularyWord>? requiredFocus,
        CancellationToken cancellationToken)
    {
        if (words.Count == 0)
            return [];
        // Skill is optional: it only biases grammar patterns in the prompt.
        SkillProfile? skill = null;
        if (skillId is not null)
            skill = await skills.GetSkillProfileAsync(skillId, userId);
        var profile = await profiles.GetByIdAsync(userId, cancellationToken);
        RequireOwner(userId);
        var nativeLanguage = string.IsNullOrWhiteSpace(profile?.NativeLanguage)
            ? "English" : profile.NativeLanguage;
        targetLanguage ??= string.IsNullOrWhiteSpace(profile?.TargetLanguage)
            ? "Korean" : profile.TargetLanguage;

        var required = requiredFocus?.Where(FocusVocabularySelection.IsUsableVocabularyWord).ToList() ?? [];
        var requiredIds = required.Select(word => word.Id).ToHashSet(StringComparer.Ordinal);
        var context = required.Count > 0
            ? words.Where(word => !requiredIds.Contains(word.Id)).ToList()
            : words;
        var prompt = await RenderAsync(new
        {
            terms = words,
            required_terms = required,
            context_terms = context,
            has_required_terms = required.Count > 0,
            number_of_sentences = count,
            skills = skill?.Description,
            native_language = nativeLanguage,
            target_language = targetLanguage
        }, cancellationToken);

        using var aiScope = scopes.CreateScope();
        var gateway = aiScope.ServiceProvider.GetService<IOwnerBoundNativeAiGateway>()
            ?? throw new NativeClozeGenerationException("Cloze generation needs the owner-bound AI gateway.");
        ClozureResponse? reply;
        try
        {
            reply = await gateway.SendPromptAsync<ClozureResponse>(userId, prompt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            and not UnauthorizedAccessException)
        {
            throw new NativeClozeGenerationException("Cloze sentence generation failed.", ex);
        }
        RequireOwner(userId);
        var challenges = (reply?.Sentences ?? [])
            .Select(dto => Heal(dto, words))
            .OfType<NativeClozeChallenge>()
            .ToList();
        if (challenges.Count == 0)
            throw new NativeClozeGenerationException("The AI returned no usable cloze sentences.");
        return challenges;
    }

    /// <summary>
    /// Source self-healing: repair the as-used form to a verbatim substring
    /// and make the guesses include the exact answer (5 unique). A sentence
    /// that still cannot be blanked would show the answer, so it is dropped.
    /// </summary>
    private NativeClozeChallenge? Heal(ClozureDto dto, List<VocabularyWord> words)
    {
        var sentence = dto.SentenceText ?? string.Empty;
        var answer = (dto.VocabularyWordAsUsed ?? string.Empty).Trim();
        var dictionary = (dto.VocabularyWord ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(sentence) && !string.IsNullOrEmpty(answer)
            && !sentence.Contains(answer, StringComparison.Ordinal))
        {
            var repaired = ClozureAnswerHelper.TryRepairWordAsUsed(sentence, answer, dictionary);
            if (!string.IsNullOrEmpty(repaired) && repaired != answer)
            {
                logger.LogWarning("Repaired a cloze as-used form to match its sentence.");
                answer = repaired;
            }
        }
        var guesses = ClozureAnswerHelper.EnsureGuessesIncludeAnswer(
            dto.VocabularyWordGuesses, answer, out var fixedGuesses);
        if (fixedGuesses)
            logger.LogWarning("Repaired cloze guesses to include the answer ({Count} options).", guesses.Count);
        var challenge = new NativeClozeChallenge(
            sentence, dto.RecommendedTranslation ?? string.Empty, dictionary, answer,
            guesses.ToImmutableArray(),
            words.FirstOrDefault(word => string.Equals(word.TargetLanguageTerm, dto.VocabularyWord,
                StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(answer)
            || challenge.BlankedSentence is null)
        {
            logger.LogWarning("Dropped a cloze sentence whose answer cannot be blanked.");
            return null;
        }
        if (challenge.Word is null)
            logger.LogWarning("A cloze sentence did not link to the owner's vocabulary; no progress will be recorded for it.");
        return challenge;
    }

    private static async Task<string> RenderAsync(object model, CancellationToken cancellationToken)
    {
        using var stream = typeof(NativeClozeSentenceSource).Assembly
            .GetManifestResourceStream(TemplateResource)
            ?? throw new InvalidOperationException("The source cloze prompt is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException("The source cloze prompt is invalid.");
        return await template.RenderAsync(model);
    }

    private void RequireOwner(string userId)
    {
        if (!owner.IsActive(userId))
            throw new UnauthorizedAccessException("Cloze requires the active owner.");
    }
}
