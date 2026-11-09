using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Operations.Handlers;

/// <summary>Arguments for adding an example sentence to one of the learner's words.</summary>
public sealed record CoachExampleSentenceEntryArgs(
    [property: Description("Identifier of the learner's vocabulary word the sentence is for.")]
    string WordId,
    [property: Description("The example sentence in the language being learned. It should use the word.")]
    string TargetSentence,
    [property: Description("Optional meaning of the sentence in the learner's own language.")]
    string? NativeSentence = null,
    [property: Description("True when the learner wrote the sentence themselves; false when you wrote it for them.")]
    bool WrittenByLearner = false);

/// <summary>Arguments for changing one of the learner's example sentences.</summary>
public sealed record CoachExampleSentenceEditArgs(
    [property: Description("Identifier of the example sentence to change, from get_vocabulary_example_sentences.")]
    string SentenceId,
    [property: Description("Replacement sentence in the language being learned. Omit to leave unchanged.")]
    string? TargetSentence = null,
    [property: Description("Replacement meaning in the learner's own language. Omit to leave unchanged.")]
    string? NativeSentence = null);

/// <summary>Arguments for deleting one of the learner's example sentences.</summary>
public sealed record CoachExampleSentenceRemovalArgs(
    [property: Description("Identifier of the example sentence to delete, from get_vocabulary_example_sentences.")]
    string SentenceId);

/// <summary>What an addition created, so undo deletes exactly that sentence.</summary>
public sealed record CoachExampleSentenceEntryUndoState(string SentenceId);

/// <summary>The fields an edit replaced, kept so the change can be put back.</summary>
public sealed record CoachExampleSentencePriorState(string TargetSentence, string? NativeSentence);

/// <summary>
/// The lookups the example-sentence handlers share: the learner's own word and sentence (the one
/// ownership rule, both recorded to them), and whether the word's review is due.
/// </summary>
/// <remarks>
/// <para>
/// Writes go straight to <see cref="ApplicationDbContext"/> rather than through
/// <c>ExampleSentenceRepository</c>, whose owner comes from the ambient active user. Here the
/// owner is the one the approval ledger proved, and it is written explicitly, the way the other
/// Sam handlers pass it to their repositories.
/// </para>
/// <para>
/// A due word's sentences stay hidden (they are part of the answer to that review, and
/// <c>get_vocabulary_example_sentences</c> withholds them), so editing or deleting one is
/// refused: its preview would show the hidden sentence. Adding one shows only what the learner
/// asked to add, so it is allowed.
/// </para>
/// </remarks>
public sealed class CoachExampleSentenceStore
{
    /// <summary>
    /// The longest sentence Sam may write. Under the 500-character column, and short enough that
    /// a labelled card line stays inside <see cref="CoachWriteLimits.LineMaxLength"/>, so the
    /// learner never approves a sentence the card had to cut.
    /// </summary>
    public const int SentenceMaxLength = 380;

    private readonly ApplicationDbContext _db;
    private readonly IPlanDateContext _dates;

    public CoachExampleSentenceStore(ApplicationDbContext db, IPlanDateContext dates)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _dates = dates ?? throw new ArgumentNullException(nameof(dates));
    }

    /// <summary>The database context the handlers save through.</summary>
    public ApplicationDbContext Db => _db;

    /// <summary>The learner's own word, or null.</summary>
    public Task<VocabularyWord?> FindWordAsync(string userProfileId, string wordId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(wordId)
            ? Task.FromResult<VocabularyWord?>(null)
            : _db.VocabularyWords.OwnedBy(userProfileId).FirstOrDefaultAsync(w => w.Id == wordId, ct);

    /// <summary>The learner's own sentence on one of their own words, tracked for change, or null.</summary>
    public async Task<(ExampleSentence Sentence, VocabularyWord Word)?> FindSentenceAsync(
        string userProfileId, string sentenceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sentenceId))
        {
            return null;
        }

        var sentence = await _db.ExampleSentences.OwnedBy(userProfileId)
            .FirstOrDefaultAsync(e => e.Id == sentenceId, ct).ConfigureAwait(false);
        if (sentence is null)
        {
            return null;
        }

        var word = await FindWordAsync(userProfileId, sentence.VocabularyWordId, ct).ConfigureAwait(false);
        return word is null ? null : (sentence, word);
    }

    /// <summary>
    /// True when this learner's review of the word is due now: the same rule the coach's leak
    /// check and the read tool use.
    /// </summary>
    public Task<bool> IsDueAsync(string userProfileId, string wordId, CancellationToken ct)
    {
        var now = _dates.UtcNow;
        return _db.VocabularyProgresses.AnyAsync(
            p => p.UserId == userProfileId && p.VocabularyWordId == wordId
                 && p.NextReviewDate != null && p.NextReviewDate <= now,
            ct);
    }

    /// <summary>True when the learner already has this exact sentence on the word.</summary>
    public Task<bool> HasSentenceAsync(string userProfileId, string wordId, string targetSentence, CancellationToken ct) =>
        _db.ExampleSentences.OwnedBy(userProfileId)
            .AnyAsync(e => e.VocabularyWordId == wordId && e.TargetSentence == targetSentence, ct);

    /// <summary>True when the learner has any sentence on the word.</summary>
    public Task<bool> HasAnySentenceAsync(string userProfileId, string wordId, CancellationToken ct) =>
        _db.ExampleSentences.OwnedBy(userProfileId).AnyAsync(e => e.VocabularyWordId == wordId, ct);
}

/// <summary>Shared argument cleaning for the example-sentence handlers.</summary>
internal static class CoachExampleSentenceText
{
    /// <summary>
    /// Cleans a sentence and refuses one over the column bound instead of cutting it: half a
    /// sentence saved as an example teaches the wrong thing.
    /// </summary>
    public static string? Sentence(string? value, Func<string, CoachToolException> invalid)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = Clean(value);
        if (cleaned.Length > CoachExampleSentenceStore.SentenceMaxLength)
        {
            throw invalid($"An example sentence can be at most {CoachExampleSentenceStore.SentenceMaxLength} characters.");
        }

        return cleaned;
    }

    /// <summary>A stored sentence for a card line; one saved elsewhere may be longer than Sam's bound.</summary>
    public static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "(empty)"
            : value.Length <= CoachExampleSentenceStore.SentenceMaxLength
                ? value
                : value[..CoachExampleSentenceStore.SentenceMaxLength] + "…";

    private static string Clean(string value)
    {
        var chars = value.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        return new string(chars).Trim();
    }
}

// ---------------------------------------------------------------------------- add

/// <summary>Adds an example sentence to a word the learner owns.</summary>
public sealed class CoachExampleSentenceEntryHandler : CoachWriteHandlerBase<CoachExampleSentenceEntryArgs>
{
    private readonly CoachExampleSentenceStore _store;

    public CoachExampleSentenceEntryHandler(CoachExampleSentenceStore store) => _store = store;

    public override string ToolName => CoachToolNames.ProposeExampleSentenceEntry;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.DeleteCreatedEntity;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.ExampleSentence;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachExampleSentenceEntryArgs args, CancellationToken cancellationToken)
    {
        var (word, target, native) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        return new CoachWritePreview(
            $"Add an example sentence to “{Clean(word.TargetLanguageTerm, 60)}”",
            Lines(word, target, native),
            word.Id,
            Canonical(new CoachExampleSentenceEntryArgs(word.Id, target, native, args.WrittenByLearner)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachExampleSentenceEntryArgs args, CancellationToken cancellationToken)
    {
        var (word, target, native) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var sentence = new ExampleSentence
        {
            Id = Guid.NewGuid().ToString(),
            // The learner who approves it owns it (the one ownership rule).
            UserProfileId = userProfileId,
            VocabularyWordId = word.Id,
            TargetSentence = target,
            NativeSentence = native,
            Source = args.WrittenByLearner ? ExampleSentenceSource.UserAuthored : ExampleSentenceSource.AiGenerated,
            // The learner approved this exact sentence, which is what curating means; the same
            // status the word screen gives a sentence the learner keeps.
            Status = ExampleSentenceStatus.Curated,
            // The first sentence on a word is its main one, as on the word screen.
            IsCore = !await _store.HasAnySentenceAsync(userProfileId, word.Id, cancellationToken).ConfigureAwait(false),
            CreatedAt = now,
            UpdatedAt = now
        };

        _store.Db.ExampleSentences.Add(sentence);
        try
        {
            await _store.Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw DataAccessFailure(ex);
        }

        return new CoachWriteExecution(
            $"Added an example sentence to “{Clean(word.TargetLanguageTerm, 60)}”",
            Lines(word, target, native),
            sentence.Id,
            Canonical(new CoachExampleSentenceEntryUndoState(sentence.Id)));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachExampleSentenceEntryArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachExampleSentenceEntryUndoState>(priorStateJson);

        // Ownership is proved again: the undo window is minutes long.
        var found = await _store.FindSentenceAsync(userProfileId, prior.SentenceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        _store.Db.ExampleSentences.Remove(found.Sentence);
        try
        {
            await _store.Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw DataAccessFailure(ex);
        }

        return new CoachWriteExecution(
            $"Removed the example sentence from “{Clean(found.Word.TargetLanguageTerm, 60)}”",
            Array.Empty<string>(),
            prior.SentenceId,
            PriorStateJson: null);
    }

    private async Task<(VocabularyWord Word, string Target, string? Native)> ValidateAsync(
        string userProfileId, CoachExampleSentenceEntryArgs args, CancellationToken cancellationToken)
    {
        var target = CoachExampleSentenceText.Sentence(args.TargetSentence, InvalidArgument) ?? string.Empty;
        if (target.Length == 0)
        {
            throw InvalidArgument("An example sentence needs text in the language being learned.");
        }

        var native = CoachExampleSentenceText.Sentence(args.NativeSentence, InvalidArgument);
        if (string.IsNullOrEmpty(native))
        {
            native = null;
        }

        var word = await _store.FindWordAsync(userProfileId, args.WordId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        if (await _store.HasSentenceAsync(userProfileId, word.Id, target, cancellationToken).ConfigureAwait(false))
        {
            throw InvalidArgument("That word already has this example sentence.");
        }

        return (word, target, native);
    }

    private static string[] Lines(VocabularyWord word, string target, string? native) =>
        native is null
            ? [$"Word: {Clean(word.TargetLanguageTerm, 120)}", $"Sentence: {target}"]
            : [$"Word: {Clean(word.TargetLanguageTerm, 120)}", $"Sentence: {target}", $"Meaning: {native}"];
}

// ---------------------------------------------------------------------------- edit

/// <summary>Changes an example sentence the learner owns.</summary>
public sealed class CoachExampleSentenceEditHandler : CoachWriteHandlerBase<CoachExampleSentenceEditArgs>
{
    private readonly CoachExampleSentenceStore _store;

    public CoachExampleSentenceEditHandler(CoachExampleSentenceStore store) => _store = store;

    public override string ToolName => CoachToolNames.ProposeExampleSentenceEdit;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.RestoreFields;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.ExampleSentence;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachExampleSentenceEditArgs args, CancellationToken cancellationToken)
    {
        var (sentence, word, target, native, changes) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        return new CoachWritePreview(
            $"Update an example sentence for “{Clean(word.TargetLanguageTerm, 60)}”",
            changes,
            sentence.Id,
            Canonical(new CoachExampleSentenceEditArgs(sentence.Id, target, native)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachExampleSentenceEditArgs args, CancellationToken cancellationToken)
    {
        var (sentence, word, target, native, changes) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        var prior = new CoachExampleSentencePriorState(sentence.TargetSentence, sentence.NativeSentence);

        if (target is not null)
        {
            sentence.TargetSentence = target;
        }

        if (native is not null)
        {
            sentence.NativeSentence = native.Length == 0 ? null : native;
        }

        sentence.UpdatedAt = DateTime.UtcNow;
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        return new CoachWriteExecution(
            $"Updated an example sentence for “{Clean(word.TargetLanguageTerm, 60)}”",
            changes,
            sentence.Id,
            Canonical(prior));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachExampleSentenceEditArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachExampleSentencePriorState>(priorStateJson);

        var found = await _store.FindSentenceAsync(userProfileId, args.SentenceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        found.Sentence.TargetSentence = prior.TargetSentence;
        found.Sentence.NativeSentence = prior.NativeSentence;
        found.Sentence.UpdatedAt = DateTime.UtcNow;
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        return new CoachWriteExecution(
            $"Restored the example sentence for “{Clean(found.Word.TargetLanguageTerm, 60)}”",
            Array.Empty<string>(),
            found.Sentence.Id,
            PriorStateJson: null);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw DataAccessFailure(ex);
        }
    }

    private async Task<(ExampleSentence Sentence, VocabularyWord Word, string? Target, string? Native, string[] Changes)>
        ValidateAsync(string userProfileId, CoachExampleSentenceEditArgs args, CancellationToken cancellationToken)
    {
        var found = await _store.FindSentenceAsync(userProfileId, args.SentenceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();
        var (sentence, word) = found;

        if (await _store.IsDueAsync(userProfileId, word.Id, cancellationToken).ConfigureAwait(false))
        {
            throw InvalidArgument("That word is due for review, so its example sentences stay hidden until the review is done.");
        }

        var target = CoachExampleSentenceText.Sentence(args.TargetSentence, InvalidArgument);
        if (target is not null && target.Length == 0)
        {
            throw InvalidArgument("An example sentence needs text in the language being learned.");
        }

        var native = CoachExampleSentenceText.Sentence(args.NativeSentence, InvalidArgument);

        var changes = new List<string>();
        if (target is not null && !string.Equals(target, sentence.TargetSentence, StringComparison.Ordinal))
        {
            changes.Add($"Sentence was: {CoachExampleSentenceText.Display(sentence.TargetSentence)}");
            changes.Add($"Sentence now: {target}");
        }
        else
        {
            target = null;
        }

        if (native is not null && !string.Equals(native, sentence.NativeSentence ?? string.Empty, StringComparison.Ordinal))
        {
            changes.Add($"Meaning was: {CoachExampleSentenceText.Display(sentence.NativeSentence)}");
            changes.Add($"Meaning now: {CoachExampleSentenceText.Display(native)}");
        }
        else
        {
            native = null;
        }

        if (changes.Count == 0)
        {
            throw InvalidArgument("That change would leave the example sentence exactly as it is.");
        }

        if (target is not null
            && await _store.HasSentenceAsync(userProfileId, word.Id, target, cancellationToken).ConfigureAwait(false))
        {
            throw InvalidArgument("That word already has this example sentence.");
        }

        return (sentence, word, target, native, changes.ToArray());
    }
}

// ---------------------------------------------------------------------------- remove

/// <summary>Deletes an example sentence the learner owns. Irreversible, so it is confirmed.</summary>
/// <remarks>
/// No undo: putting a deleted row back under its old identifier would race the sync deletion
/// log that already told the learner's other devices it is gone, and a fresh copy under a new
/// identifier is an approximation the learner would believe was the original.
/// </remarks>
public sealed class CoachExampleSentenceRemovalHandler : CoachWriteHandlerBase<CoachExampleSentenceRemovalArgs>
{
    private readonly CoachExampleSentenceStore _store;

    public CoachExampleSentenceRemovalHandler(CoachExampleSentenceStore store) => _store = store;

    public override string ToolName => CoachToolNames.ProposeExampleSentenceRemoval;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteHard;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.ExampleSentence;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachExampleSentenceRemovalArgs args, CancellationToken cancellationToken)
    {
        var (sentence, word) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        return new CoachWritePreview(
            $"Delete an example sentence from “{Clean(word.TargetLanguageTerm, 60)}”",
            Lines(word, sentence, "This cannot be undone."),
            sentence.Id,
            Canonical(new CoachExampleSentenceRemovalArgs(sentence.Id)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachExampleSentenceRemovalArgs args, CancellationToken cancellationToken)
    {
        var (sentence, word) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);
        var lines = Lines(word, sentence, tail: null);

        _store.Db.ExampleSentences.Remove(sentence);
        try
        {
            await _store.Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw DataAccessFailure(ex);
        }

        return new CoachWriteExecution(
            $"Deleted an example sentence from “{Clean(word.TargetLanguageTerm, 60)}”",
            lines,
            sentence.Id,
            PriorStateJson: null);
    }

    private async Task<(ExampleSentence Sentence, VocabularyWord Word)> ValidateAsync(
        string userProfileId, CoachExampleSentenceRemovalArgs args, CancellationToken cancellationToken)
    {
        var found = await _store.FindSentenceAsync(userProfileId, args.SentenceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        if (await _store.IsDueAsync(userProfileId, found.Word.Id, cancellationToken).ConfigureAwait(false))
        {
            throw InvalidArgument("That word is due for review, so its example sentences stay hidden until the review is done.");
        }

        return found;
    }

    private static string[] Lines(VocabularyWord word, ExampleSentence sentence, string? tail)
    {
        var lines = new List<string>
        {
            $"Word: {Clean(word.TargetLanguageTerm, 120)}",
            $"Sentence: {CoachExampleSentenceText.Display(sentence.TargetSentence)}"
        };
        if (!string.IsNullOrWhiteSpace(sentence.NativeSentence))
        {
            lines.Add($"Meaning: {CoachExampleSentenceText.Display(sentence.NativeSentence)}");
        }

        if (tail is not null)
        {
            lines.Add(tail);
        }

        return lines.ToArray();
    }
}
