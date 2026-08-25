using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Operations.Handlers;

/// <summary>Arguments for adding a vocabulary entry to one of the learner's resources.</summary>
public sealed record CoachVocabularyEntryArgs(
    [property: Description("Identifier of the learner's learning resource the word belongs to.")]
    string ResourceId,
    [property: Description("The word or phrase in the language being learned.")]
    string TargetTerm,
    [property: Description("The meaning in the learner's own language.")]
    string NativeTerm,
    [property: Description("Optional comma-separated tags.")]
    string? Tags = null);

/// <summary>Arguments for editing a vocabulary entry the learner already has.</summary>
public sealed record CoachVocabularyEditArgs(
    [property: Description("Identifier of the vocabulary entry to change.")]
    string WordId,
    [property: Description("Replacement term in the language being learned. Omit to leave unchanged.")]
    string? TargetTerm = null,
    [property: Description("Replacement meaning in the learner's own language. Omit to leave unchanged.")]
    string? NativeTerm = null,
    [property: Description("Replacement comma-separated tags. Omit to leave unchanged.")]
    string? Tags = null,
    [property: Description("Replacement memory aid. Omit to leave unchanged.")]
    string? Mnemonic = null);

/// <summary>Arguments for putting an existing entry onto another of the learner's resources.</summary>
public sealed record CoachVocabularyLinkArgs(
    [property: Description("Identifier of the vocabulary entry to add.")]
    string WordId,
    [property: Description("Identifier of the learner's learning resource to add it to.")]
    string ResourceId);

/// <summary>Arguments for removing a vocabulary entry from the learner's collection.</summary>
public sealed record CoachVocabularyRemovalArgs(
    [property: Description("Identifier of the vocabulary entry to remove.")]
    string WordId);

/// <summary>The fields an edit replaced, kept so the change can be put back.</summary>
public sealed record CoachVocabularyPriorState(
    string? TargetTerm, string? NativeTerm, string? Tags, string? Mnemonic);

/// <summary>What an addition created, so undo knows whether to delete or merely unlink.</summary>
public sealed record CoachVocabularyEntryUndoState(string WordId, string ResourceId, bool CreatedWord);

// ---------------------------------------------------------------------------- add

/// <summary>
/// Adds a vocabulary entry to a resource the learner owns.
/// </summary>
/// <remarks>
/// The resource is required rather than optional. A vocabulary word carries no owner of its own
/// and belongs to a learner only through a mapping onto their resource, so a word created without
/// one would be an unowned row that no later ownership check could claim — invisible to the
/// learner who asked for it, and editable by anybody.
/// </remarks>
public sealed class CoachVocabularyEntryHandler : CoachWriteHandlerBase<CoachVocabularyEntryArgs>
{
    private const int TermMaxLength = 200;
    private const int TagsMaxLength = 400;

    private readonly CoachWriteOwnership _ownership;
    private readonly LearningResourceRepository _resources;
    private readonly ILogger _logger;

    public CoachVocabularyEntryHandler(
        CoachWriteOwnership ownership,
        LearningResourceRepository resources,
        ILogger<CoachVocabularyEntryHandler>? logger = null)
    {
        _ownership = ownership;
        _resources = resources;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public override string ToolName => CoachToolNames.ProposeVocabularyEntry;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.DeleteCreatedEntity;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.VocabularyWord;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachVocabularyEntryArgs args, CancellationToken cancellationToken)
    {
        var (resource, target, native, tags) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        var existing = await _ownership
            .FindOwnedWordByTermAsync(userProfileId, target, cancellationToken)
            .ConfigureAwait(false);

        var lines = new List<string>
        {
            $"Word: {target}",
            $"Meaning: {native}",
            $"Resource: {Clean(resource.Title, 120)}"
        };

        if (tags.Length > 0)
        {
            lines.Add($"Tags: {tags}");
        }

        if (existing is not null)
        {
            lines.Add("You already have this word; it will be added to this resource rather than duplicated.");
        }

        return new CoachWritePreview(
            existing is null
                ? $"Add \u201c{target}\u201d to {Clean(resource.Title, 60)}"
                : $"Add your existing \u201c{target}\u201d to {Clean(resource.Title, 60)}",
            lines,
            existing?.Id,
            Canonical(new CoachVocabularyEntryArgs(resource.Id, target, native, NullIfEmpty(tags))));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachVocabularyEntryArgs args, CancellationToken cancellationToken)
    {
        var (resource, target, native, tags) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        // Re-uses the learner's existing entry when they already have this term. Two resources
        // pointing at one word is the shape the app already uses; two rows with the same term is
        // a duplicate the learner would have to clean up.
        var word = await _ownership
            .FindOwnedWordByTermAsync(userProfileId, target, cancellationToken)
            .ConfigureAwait(false);

        var created = word is null;
        if (created)
        {
            word = new VocabularyWord
            {
                Id = Guid.NewGuid().ToString(),
                TargetLanguageTerm = target,
                NativeLanguageTerm = native,
                Tags = NullIfEmpty(tags),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var saved = await _resources.SaveWordAsync(word).ConfigureAwait(false);
            if (saved <= 0)
            {
                throw DataAccessFailure(new InvalidOperationException("The vocabulary entry was not saved."));
            }
        }

        var linked = await _resources
            .AddVocabularyToResourceAsync(resource.Id, word!.Id, userProfileId)
            .ConfigureAwait(false);

        if (!linked && created)
        {
            // The word exists but is attached to nothing, which is exactly the unowned state this
            // handler exists to avoid. Removing it leaves the learner's data as it was. The
            // compensation is best effort by necessity — the caller is already being refused, and
            // a failure here must not replace that refusal — but it is not allowed to be silent,
            // because a word left orphaned is a row nobody will ever find again.
            if (!await _resources.DeleteVocabularyWordAsync(word.Id).ConfigureAwait(false))
            {
                _logger.LogError(
                    "[Coach] A vocabulary entry created for a link that failed could not be removed. Entity {EntityId}.",
                    word.Id);
            }

            throw DataAccessFailure(new InvalidOperationException("The vocabulary entry could not be linked."));
        }

        return new CoachWriteExecution(
            $"Added \u201c{target}\u201d to {Clean(resource.Title, 60)}",
            new[] { $"Word: {target}", $"Meaning: {native}", $"Resource: {Clean(resource.Title, 120)}" },
            word.Id,
            Canonical(new CoachVocabularyEntryUndoState(word.Id, resource.Id, created)));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachVocabularyEntryArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachVocabularyEntryUndoState>(priorStateJson);

        var resource = await _ownership
            .FindResourceAsync(userProfileId, prior.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        // The link is what this operation created, so failing to remove it means the undo did not
        // happen. Reporting Undone anyway would leave the word attached and the learner told it
        // was not.
        if (!await _resources
                .RemoveVocabularyFromResourceAsync(resource.Id, prior.WordId, userProfileId)
                .ConfigureAwait(false))
        {
            throw DataAccessFailure(new InvalidOperationException("The vocabulary link was not removed."));
        }

        // Only a word this operation brought into existence is deleted. One the learner already
        // had is merely unlinked, because the undo is of the addition, not of their collection.
        if (prior.CreatedWord)
        {
            var remaining = await _ownership.CountAllLinksAsync(prior.WordId, cancellationToken)
                .ConfigureAwait(false);
            if (remaining == 0
                && !await _resources.DeleteVocabularyWordAsync(prior.WordId).ConfigureAwait(false))
            {
                throw DataAccessFailure(new InvalidOperationException("The vocabulary entry was not removed."));
            }
        }

        return new CoachWriteExecution(
            $"Removed that word from {Clean(resource.Title, 60)}",
            Array.Empty<string>(),
            prior.WordId,
            PriorStateJson: null);
    }

    private async Task<(LearningResource Resource, string Target, string Native, string Tags)> ValidateAsync(
        string userProfileId, CoachVocabularyEntryArgs args, CancellationToken cancellationToken)
    {
        var target = Clean(args.TargetTerm, TermMaxLength);
        var native = Clean(args.NativeTerm, TermMaxLength);

        if (target.Length == 0)
        {
            throw InvalidArgument("A vocabulary entry needs a term in the language being learned.");
        }

        if (native.Length == 0)
        {
            throw InvalidArgument("A vocabulary entry needs a meaning.");
        }

        var resource = await _ownership
            .FindResourceAsync(userProfileId, args.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        return (resource, target, native, Clean(args.Tags, TagsMaxLength));
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

// ---------------------------------------------------------------------------- edit

/// <summary>Edits a vocabulary entry the learner owns.</summary>
public sealed class CoachVocabularyEditHandler : CoachWriteHandlerBase<CoachVocabularyEditArgs>
{
    private const int TermMaxLength = 200;
    private const int TagsMaxLength = 400;
    private const int MnemonicMaxLength = 1000;

    private readonly CoachWriteOwnership _ownership;
    private readonly LearningResourceRepository _resources;

    public CoachVocabularyEditHandler(CoachWriteOwnership ownership, LearningResourceRepository resources)
    {
        _ownership = ownership;
        _resources = resources;
    }

    public override string ToolName => CoachToolNames.ProposeVocabularyEdit;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.RestoreFields;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.VocabularyWord;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachVocabularyEditArgs args, CancellationToken cancellationToken)
    {
        var (word, changes) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        return new CoachWritePreview(
            $"Update \u201c{Clean(word.TargetLanguageTerm, 60)}\u201d",
            changes.Select(c => $"{c.Label}: {c.From} \u2192 {c.To}").ToArray(),
            word.Id,
            Canonical(new CoachVocabularyEditArgs(
                word.Id,
                Optional(args.TargetTerm, TermMaxLength),
                Optional(args.NativeTerm, TermMaxLength),
                Optional(args.Tags, TagsMaxLength),
                Optional(args.Mnemonic, MnemonicMaxLength))));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachVocabularyEditArgs args, CancellationToken cancellationToken)
    {
        var (word, changes) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        var prior = new CoachVocabularyPriorState(
            word.TargetLanguageTerm, word.NativeLanguageTerm, word.Tags, word.MnemonicText);

        Apply(word, args);
        word.UpdatedAt = DateTime.UtcNow;

        if (!await _resources.UpdateVocabularyWordAsync(word).ConfigureAwait(false))
        {
            throw DataAccessFailure(new InvalidOperationException("The vocabulary entry was not updated."));
        }

        return new CoachWriteExecution(
            $"Updated \u201c{Clean(word.TargetLanguageTerm, 60)}\u201d",
            changes.Select(c => $"{c.Label}: {c.From} \u2192 {c.To}").ToArray(),
            word.Id,
            Canonical(prior));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachVocabularyEditArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachVocabularyPriorState>(priorStateJson);

        // Ownership is re-proved rather than assumed from the receipt: the window is minutes
        // long, and the learner may have unlinked the word in between.
        var word = await _ownership.FindOwnedWordAsync(userProfileId, args.WordId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        word.TargetLanguageTerm = prior.TargetTerm;
        word.NativeLanguageTerm = prior.NativeTerm;
        word.Tags = prior.Tags;
        word.MnemonicText = prior.Mnemonic;
        word.UpdatedAt = DateTime.UtcNow;

        if (!await _resources.UpdateVocabularyWordAsync(word).ConfigureAwait(false))
        {
            throw DataAccessFailure(new InvalidOperationException("The vocabulary entry was not restored."));
        }

        return new CoachWriteExecution(
            $"Restored \u201c{Clean(word.TargetLanguageTerm, 60)}\u201d",
            Array.Empty<string>(),
            word.Id,
            PriorStateJson: null);
    }

    private async Task<(VocabularyWord Word, IReadOnlyList<FieldChange> Changes)> ValidateAsync(
        string userProfileId, CoachVocabularyEditArgs args, CancellationToken cancellationToken)
    {
        var word = await _ownership.FindOwnedWordAsync(userProfileId, args.WordId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var changes = new List<FieldChange>();
        AddChange(changes, "Word", word.TargetLanguageTerm, Optional(args.TargetTerm, TermMaxLength));
        AddChange(changes, "Meaning", word.NativeLanguageTerm, Optional(args.NativeTerm, TermMaxLength));
        AddChange(changes, "Tags", word.Tags, Optional(args.Tags, TagsMaxLength));
        AddChange(changes, "Memory aid", word.MnemonicText, Optional(args.Mnemonic, MnemonicMaxLength));

        if (changes.Count == 0)
        {
            throw InvalidArgument("That change would leave the entry exactly as it is.");
        }

        var newTarget = Optional(args.TargetTerm, TermMaxLength);
        if (newTarget is not null && newTarget.Length == 0)
        {
            throw InvalidArgument("A vocabulary entry needs a term in the language being learned.");
        }

        return (word, changes);
    }

    private static void Apply(VocabularyWord word, CoachVocabularyEditArgs args)
    {
        if (Optional(args.TargetTerm, TermMaxLength) is { } target)
        {
            word.TargetLanguageTerm = target;
        }

        if (Optional(args.NativeTerm, TermMaxLength) is { } native)
        {
            word.NativeLanguageTerm = native;
        }

        if (Optional(args.Tags, TagsMaxLength) is { } tags)
        {
            word.Tags = tags;
        }

        if (Optional(args.Mnemonic, MnemonicMaxLength) is { } mnemonic)
        {
            word.MnemonicText = mnemonic;
        }
    }

    private static void AddChange(List<FieldChange> changes, string label, string? from, string? to)
    {
        if (to is null || string.Equals(from ?? string.Empty, to, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new FieldChange(label, Display(from), Display(to)));
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Length <= 80 ? value : value[..80];

    private static string? Optional(string? value, int maxLength) =>
        value is null ? null : Clean(value, maxLength);

    private sealed record FieldChange(string Label, string From, string To);
}

// ---------------------------------------------------------------------------- link

/// <summary>Adds an entry the learner already has to another of their resources.</summary>
public sealed class CoachVocabularyLinkHandler : CoachWriteHandlerBase<CoachVocabularyLinkArgs>
{
    private readonly CoachWriteOwnership _ownership;
    private readonly LearningResourceRepository _resources;

    public CoachVocabularyLinkHandler(CoachWriteOwnership ownership, LearningResourceRepository resources)
    {
        _ownership = ownership;
        _resources = resources;
    }

    public override string ToolName => CoachToolNames.ProposeVocabularyLink;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.UnlinkVocabulary;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.ResourceVocabularyLink;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachVocabularyLinkArgs args, CancellationToken cancellationToken)
    {
        var (word, resource) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        if (await _ownership.IsWordLinkedAsync(resource.Id, word.Id, cancellationToken).ConfigureAwait(false))
        {
            throw InvalidArgument("That resource already has this word.");
        }

        return new CoachWritePreview(
            $"Add \u201c{Clean(word.TargetLanguageTerm, 60)}\u201d to {Clean(resource.Title, 60)}",
            new[] { $"Word: {Clean(word.TargetLanguageTerm, 120)}", $"Resource: {Clean(resource.Title, 120)}" },
            word.Id,
            Canonical(new CoachVocabularyLinkArgs(word.Id, resource.Id)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachVocabularyLinkArgs args, CancellationToken cancellationToken)
    {
        var (word, resource) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        if (!await _resources
                .AddVocabularyToResourceAsync(resource.Id, word.Id, userProfileId)
                .ConfigureAwait(false))
        {
            throw DataAccessFailure(new InvalidOperationException("The word could not be added."));
        }

        return new CoachWriteExecution(
            $"Added \u201c{Clean(word.TargetLanguageTerm, 60)}\u201d to {Clean(resource.Title, 60)}",
            new[] { $"Word: {Clean(word.TargetLanguageTerm, 120)}", $"Resource: {Clean(resource.Title, 120)}" },
            word.Id,
            Canonical(new CoachVocabularyLinkArgs(word.Id, resource.Id)));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachVocabularyLinkArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachVocabularyLinkArgs>(priorStateJson);
        var (word, resource) = await ValidateAsync(
                userProfileId, new CoachVocabularyLinkArgs(prior.WordId, prior.ResourceId), cancellationToken)
            .ConfigureAwait(false);

        // The link is the entire effect of this operation, so a refusal to remove it means the
        // undo did not happen and must not be reported as Undone.
        if (!await _resources
                .RemoveVocabularyFromResourceAsync(resource.Id, word.Id, userProfileId)
                .ConfigureAwait(false))
        {
            throw DataAccessFailure(new InvalidOperationException("The vocabulary link was not removed."));
        }

        return new CoachWriteExecution(
            $"Removed \u201c{Clean(word.TargetLanguageTerm, 60)}\u201d from {Clean(resource.Title, 60)}",
            Array.Empty<string>(),
            word.Id,
            PriorStateJson: null);
    }

    private async Task<(VocabularyWord Word, LearningResource Resource)> ValidateAsync(
        string userProfileId, CoachVocabularyLinkArgs args, CancellationToken cancellationToken)
    {
        var word = await _ownership.FindOwnedWordAsync(userProfileId, args.WordId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var resource = await _ownership.FindResourceAsync(userProfileId, args.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        return (word, resource);
    }
}

// ---------------------------------------------------------------------------- removal

/// <summary>
/// Removes a vocabulary entry from the learner's collection.
/// </summary>
/// <remarks>
/// Protected, and offers no undo. The entry carries progress history that deletion discards, and
/// an "undo" that recreated the row without that history would be restoring something the learner
/// did not have back.
/// </remarks>
public sealed class CoachVocabularyRemovalHandler : CoachWriteHandlerBase<CoachVocabularyRemovalArgs>
{
    private readonly CoachWriteOwnership _ownership;
    private readonly LearningResourceRepository _resources;

    public CoachVocabularyRemovalHandler(CoachWriteOwnership ownership, LearningResourceRepository resources)
    {
        _ownership = ownership;
        _resources = resources;
    }

    public override string ToolName => CoachToolNames.ProposeVocabularyRemoval;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteHard;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.VocabularyWord;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachVocabularyRemovalArgs args, CancellationToken cancellationToken)
    {
        var word = await _ownership.FindOwnedWordAsync(userProfileId, args.WordId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var mine = await _ownership.CountOwnedLinksAsync(userProfileId, word.Id, cancellationToken)
            .ConfigureAwait(false);

        var lines = new List<string>
        {
            $"Word: {Clean(word.TargetLanguageTerm, 120)}",
            $"Meaning: {Clean(word.NativeLanguageTerm, 120)}",
            mine == 1 ? "In 1 of your resources" : $"In {mine} of your resources",
            "This cannot be undone."
        };

        return new CoachWritePreview(
            $"Remove \u201c{Clean(word.TargetLanguageTerm, 60)}\u201d from your vocabulary",
            lines,
            word.Id,
            Canonical(new CoachVocabularyRemovalArgs(word.Id)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachVocabularyRemovalArgs args, CancellationToken cancellationToken)
    {
        var word = await _ownership.FindOwnedWordAsync(userProfileId, args.WordId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var term = Clean(word.TargetLanguageTerm, 60);

        // Unlink from every resource of the learner's first. Whether the row itself goes depends
        // on whether anybody else still points at it.
        var owned = await _ownership.OwnedResourceIdsForWordAsync(userProfileId, word.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var resourceId in owned)
        {
            // Every one of these has to succeed. A partial unlink that reports "removed from your
            // vocabulary" leaves the word in resources the learner was told it had left.
            if (!await _resources
                    .RemoveVocabularyFromResourceAsync(resourceId, word.Id, userProfileId)
                    .ConfigureAwait(false))
            {
                throw DataAccessFailure(
                    new InvalidOperationException("The vocabulary entry was not fully unlinked."));
            }
        }

        var remaining = await _ownership.CountAllLinksAsync(word.Id, cancellationToken).ConfigureAwait(false);
        var deleted = false;
        if (remaining == 0)
        {
            // Deleting a row another learner still maps would be a cross-tenant write, so the
            // row only goes when nothing points at it any more.
            deleted = await _resources.DeleteVocabularyWordAsync(word.Id).ConfigureAwait(false);
            if (!deleted)
            {
                throw DataAccessFailure(new InvalidOperationException("The vocabulary entry was not deleted."));
            }
        }

        return new CoachWriteExecution(
            $"Removed \u201c{term}\u201d from your vocabulary",
            new[] { deleted ? "The entry was deleted." : "The entry was removed from your resources." },
            word.Id,
            PriorStateJson: null);
    }
}
