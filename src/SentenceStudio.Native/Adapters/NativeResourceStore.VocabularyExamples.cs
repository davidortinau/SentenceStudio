using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.DTOs;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// The word editor's example sentences (source ReferenceSentencesSection). Only the owner's own
/// word is changed, and only the owner's own sentences on it (recorded to them, the one
/// ownership rule): a sentence another learner owns, or no one owns, is never read, changed or
/// demoted. Each write saves at once, as the source does, outside the word's own Save.
/// </summary>
public sealed partial class NativeResourceStore
{
    private const int MaxExampleLength = NativeExampleSentenceDraft.MaxLength;

    public Task<NativeExampleSentenceResult> AddOwnedExampleAsync(
        string owner, string wordId, NativeExampleSentenceDraft draft,
        ExampleSentenceSource source, bool coreWhenFirst,
        CancellationToken cancellationToken) =>
        WriteOwnedExampleAsync(owner, wordId, cancellationToken, (db, owned) =>
        {
            if (Cleaned(draft) is not { } clean || !Enum.IsDefined(source))
                return NativeExampleSentenceStatus.Invalid;
            var now = DateTime.UtcNow;
            db.ExampleSentences.Add(new ExampleSentence
            {
                VocabularyWordId = wordId,
                UserProfileId = owner,
                TargetSentence = clean.TargetSentence,
                NativeSentence = clean.NativeSentence,
                Register = clean.Register,
                DifficultyLevel = clean.DifficultyLevel,
                Source = source,
                Status = ExampleSentenceStatus.Curated,
                IsCore = coreWhenFirst && owned.Count == 0,
                CreatedAt = now,
                UpdatedAt = now
            });
            return NativeExampleSentenceStatus.Saved;
        });

    public Task<NativeExampleSentenceResult> EditOwnedExampleAsync(
        string owner, string wordId, string exampleId, NativeExampleSentenceDraft draft,
        CancellationToken cancellationToken) =>
        WriteOwnedExampleAsync(owner, wordId, cancellationToken, (_, owned) =>
        {
            if (owned.Find(example => example.Id == exampleId) is not { } sentence)
                return NativeExampleSentenceStatus.NotFoundOrForbidden;
            if (Cleaned(draft) is not { } clean)
                return NativeExampleSentenceStatus.Invalid;
            sentence.TargetSentence = clean.TargetSentence;
            sentence.NativeSentence = clean.NativeSentence;
            sentence.Register = clean.Register;
            sentence.UpdatedAt = DateTime.UtcNow;
            return NativeExampleSentenceStatus.Saved;
        });

    public Task<NativeExampleSentenceResult> SetOwnedExampleCoreAsync(
        string owner, string wordId, string exampleId, bool isCore,
        CancellationToken cancellationToken) =>
        WriteOwnedExampleAsync(owner, wordId, cancellationToken, (_, owned) =>
        {
            if (owned.Find(example => example.Id == exampleId) is not { } sentence)
                return NativeExampleSentenceStatus.NotFoundOrForbidden;
            var now = DateTime.UtcNow;
            // One core example per word (source DemoteOtherCoresAsync), among the owner's own.
            if (isCore)
                foreach (var other in owned.Where(other => other.IsCore && other.Id != exampleId))
                {
                    other.IsCore = false;
                    other.UpdatedAt = now;
                }
            if (sentence.IsCore != isCore)
            {
                sentence.IsCore = isCore;
                sentence.UpdatedAt = now;
            }
            return NativeExampleSentenceStatus.Saved;
        });

    public Task<NativeExampleSentenceResult> SetOwnedExampleFlagAsync(
        string owner, string wordId, string exampleId, bool isFlagged,
        CancellationToken cancellationToken) =>
        WriteOwnedExampleAsync(owner, wordId, cancellationToken, (_, owned) =>
        {
            if (owned.Find(example => example.Id == exampleId) is not { } sentence)
                return NativeExampleSentenceStatus.NotFoundOrForbidden;
            if (sentence.IsFlagged != isFlagged)
            {
                sentence.IsFlagged = isFlagged;
                sentence.UpdatedAt = DateTime.UtcNow;
            }
            return NativeExampleSentenceStatus.Saved;
        });

    public Task<NativeExampleSentenceResult> DeleteOwnedExampleAsync(
        string owner, string wordId, string exampleId,
        CancellationToken cancellationToken) =>
        WriteOwnedExampleAsync(owner, wordId, cancellationToken, (db, owned) =>
        {
            if (owned.Find(example => example.Id == exampleId) is not { } sentence)
                return NativeExampleSentenceStatus.NotFoundOrForbidden;
            db.ExampleSentences.Remove(sentence);
            return NativeExampleSentenceStatus.Saved;
        });

    public async Task<IReadOnlyList<NativeExampleSentenceDraft>> SuggestOwnedExamplesAsync(
        string owner, string wordId, string targetLanguage, string nativeLanguage, int count,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            throw new UnauthorizedAccessException("Example suggestions require an active owner.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
                .SingleOrDefaultAsync(candidate => candidate.Id == wordId, cancellationToken)
            ?? throw new InvalidOperationException("Example suggestions need one of the owner's words.");
        var gateway = scope.ServiceProvider.GetService<IOwnerBoundNativeAiGateway>()
            ?? throw new InvalidOperationException(
                "Native example suggestions require an owner-bound AI gateway.");
        // The source's prompt, sent as the owner through the gateway (no key on the device).
        var prompt = VocabularyExampleGenerationService.BuildPrompt(
            word, nativeLanguage, targetLanguage, count, SpeechRegister.Unspecified);
        var reply = await gateway.SendPromptAsync<GeneratedExampleSentencesDto>(
            owner, prompt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner))
            throw new UnauthorizedAccessException("Example suggestion owner changed during generation.");
        return VocabularyExampleGenerationService.ToSuggestions(word, reply?.Sentences)
            .Select(suggestion => new NativeExampleSentenceDraft(
                Clip(suggestion.TargetSentence), suggestion.NativeSentence is { } native
                    ? Clip(native) : null,
                suggestion.Register, suggestion.DifficultyLevel))
            .ToArray();
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the owner's sentences of the owner's word (tracked),
    /// in one transaction that commits only while the owner is unchanged, then reads them back.
    /// </summary>
    private async Task<NativeExampleSentenceResult> WriteOwnedExampleAsync(
        string owner, string wordId, CancellationToken cancellationToken,
        Func<ApplicationDbContext, List<ExampleSentence>, NativeExampleSentenceStatus> change)
    {
        static NativeExampleSentenceResult Refused(NativeExampleSentenceStatus status) =>
            new(status, [], string.Empty);
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(wordId))
            return Refused(NativeExampleSentenceStatus.NotFoundOrForbidden);

        var stamp = userScope.CaptureStamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var word = await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
            .SingleOrDefaultAsync(candidate => candidate.Id == wordId, cancellationToken);
        if (word is null)
            return Refused(NativeExampleSentenceStatus.NotFoundOrForbidden);
        var owned = await db.ExampleSentences.OwnedBy(owner)
            .Where(example => example.VocabularyWordId == wordId)
            .ToListAsync(cancellationToken);
        var status = change(db, owned);
        if (status != NativeExampleSentenceStatus.Saved)
            return Refused(status);
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return Refused(NativeExampleSentenceStatus.NotFoundOrForbidden);
        await db.SaveChangesAsync(cancellationToken);
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return Refused(NativeExampleSentenceStatus.NotFoundOrForbidden);
        await transaction.CommitAsync(cancellationToken);

        var examples = await OwnedExampleRows(db, owner, wordId)
            .ToListAsync(cancellationToken);
        var (_, label) = EncodingScoreHelper.CalculateWithLabel(word, examples.Count);
        try
        {
            pendingWrites.MarkPending(owner);
            return new(NativeExampleSentenceStatus.Saved, examples, label);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Example sentence write committed but sync tracking was not confirmed.");
            return new(NativeExampleSentenceStatus.SavedSyncUnconfirmed, examples, label);
        }
    }

    /// <summary>The owner's sentences on the word as the editor shows them: core first, then oldest first.</summary>
    private static IQueryable<NativeVocabularyExample> OwnedExampleRows(
        ApplicationDbContext db, string owner, string wordId) =>
        OwnedExamples(db, owner)
            .Where(example => example.VocabularyWordId == wordId)
            .OrderByDescending(example => example.IsCore)
            .ThenBy(example => example.CreatedAt)
            .ThenBy(example => example.Id)
            .Select(example => new NativeVocabularyExample(
                example.Id, example.TargetSentence, example.NativeSentence,
                example.IsCore, example.IsFlagged, example.Source,
                example.Register, example.DifficultyLevel));

    /// <summary>Trimmed text within the column limits, a known register; null when the sentence is blank or too long.</summary>
    private static NativeExampleSentenceDraft? Cleaned(NativeExampleSentenceDraft? draft)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.TargetSentence)
            || !Enum.IsDefined(draft.Register))
            return null;
        var target = draft.TargetSentence.Trim();
        var native = string.IsNullOrWhiteSpace(draft.NativeSentence)
            ? null : draft.NativeSentence.Trim();
        if (target.Length > MaxExampleLength || native?.Length > MaxExampleLength)
            return null;
        return new(target, native, draft.Register,
            draft.DifficultyLevel is >= 1 and <= 5 ? draft.DifficultyLevel : null);
    }

    /// <summary>A suggestion longer than the column is cut, so Keep cannot be refused for its length.</summary>
    private static string Clip(string text) =>
        text.Length <= MaxExampleLength ? text : text[..MaxExampleLength];
}
