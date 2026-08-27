using System.ComponentModel;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Operations.Handlers;

/// <summary>Arguments for creating a skill the learner will practise against.</summary>
public sealed record CoachSkillEntryArgs(
    [property: Description("Short name for the skill.")]
    string Title,
    [property: Description("What practising this skill should cover.")]
    string Description,
    [property: Description("Language the skill is practised in. Defaults to the learner's target language.")]
    string? Language = null);

/// <summary>Arguments for editing one of the learner's skills.</summary>
public sealed record CoachSkillEditArgs(
    [property: Description("Identifier of the skill to change.")]
    string SkillId,
    [property: Description("Replacement name. Omit to leave unchanged.")]
    string? Title = null,
    [property: Description("Replacement description. Omit to leave unchanged.")]
    string? Description = null);

/// <summary>Arguments for archiving one of the learner's skills.</summary>
public sealed record CoachSkillArchiveArgs(
    [property: Description("Identifier of the skill to archive.")]
    string SkillId);

/// <summary>The skill fields an edit replaced.</summary>
public sealed record CoachSkillPriorState(string? Title, string? Description);

/// <summary>What a skill creation produced, so undo can remove exactly that row.</summary>
public sealed record CoachSkillEntryUndoState(string SkillId);

/// <summary>Which skill an archive put away, so undo can restore exactly that one.</summary>
public sealed record CoachSkillArchiveUndoState(string SkillId);

// ---------------------------------------------------------------------------- create

/// <summary>Creates a skill owned by the learner.</summary>
public sealed class CoachSkillEntryHandler : CoachWriteHandlerBase<CoachSkillEntryArgs>
{
    private const int TitleMaxLength = 120;
    private const int DescriptionMaxLength = 2000;
    private const int LanguageMaxLength = 40;

    private readonly SkillProfileRepository _skills;
    private readonly CoachWriteOwnership _ownership;

    public CoachSkillEntryHandler(SkillProfileRepository skills, CoachWriteOwnership ownership)
    {
        _skills = skills;
        _ownership = ownership;
    }

    public override string ToolName => CoachToolNames.ProposeSkillEntry;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.DeleteCreatedEntity;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.SkillProfile;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachSkillEntryArgs args, CancellationToken cancellationToken)
    {
        var (title, description, language) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        return new CoachWritePreview(
            $"Create the skill \u201c{title}\u201d",
            new[] { $"Name: {title}", $"Focus: {description}", $"Language: {language}" },
            EntityId: null,
            Canonical(new CoachSkillEntryArgs(title, description, language)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachSkillEntryArgs args, CancellationToken cancellationToken)
    {
        var (title, description, language) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        var skill = new SkillProfile
        {
            Title = title,
            Description = description,
            Language = language
        };

        // The repository stamps ownership from the id passed here, refuses an empty one, and
        // returns an empty id rather than throwing when it declines — so a blank answer is a
        // refusal, not a success.
        var savedId = await _skills.SaveAsync(skill, userProfileId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(savedId))
        {
            throw DataAccessFailure(new InvalidOperationException("The skill was not saved."));
        }

        return new CoachWriteExecution(
            $"Created the skill \u201c{title}\u201d",
            new[] { $"Name: {title}", $"Focus: {description}" },
            skill.Id,
            Canonical(new CoachSkillEntryUndoState(skill.Id)));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachSkillEntryArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachSkillEntryUndoState>(priorStateJson);

        var skill = await _ownership.FindSkillAsync(userProfileId, prior.SkillId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        // The repository answers with the number of rows it removed, and answers zero when it
        // declined. Ignoring that answer is what makes a receipt claim a deletion that never
        // happened, so nothing below runs unless a row actually went.
        if (await _skills.DeleteAsync(skill, userProfileId).ConfigureAwait(false) <= 0)
        {
            throw DataAccessFailure(new InvalidOperationException("The skill was not removed."));
        }

        return new CoachWriteExecution(
            $"Removed the skill \u201c{Clean(skill.Title, 60)}\u201d",
            Array.Empty<string>(),
            skill.Id,
            PriorStateJson: null);
    }

    private async Task<(string Title, string Description, string Language)> ValidateAsync(
        string userProfileId, CoachSkillEntryArgs args, CancellationToken cancellationToken)
    {
        var title = Clean(args.Title, TitleMaxLength);
        var description = Clean(args.Description, DescriptionMaxLength);

        if (title.Length == 0)
        {
            throw InvalidArgument("A skill needs a name.");
        }

        if (description.Length == 0)
        {
            throw InvalidArgument("A skill needs a description of what it covers.");
        }

        var language = Clean(args.Language, LanguageMaxLength);
        if (language.Length == 0)
        {
            var profile = await _ownership.FindProfileAsync(userProfileId, cancellationToken)
                .ConfigureAwait(false);
            language = Clean(profile?.TargetLanguage, LanguageMaxLength);
            if (language.Length == 0)
            {
                language = "Korean";
            }
        }

        return (title, description, language);
    }
}

// ---------------------------------------------------------------------------- edit

/// <summary>Edits a skill the learner owns.</summary>
public sealed class CoachSkillEditHandler : CoachWriteHandlerBase<CoachSkillEditArgs>
{
    private const int TitleMaxLength = 120;
    private const int DescriptionMaxLength = 2000;

    private readonly SkillProfileRepository _skills;
    private readonly CoachWriteOwnership _ownership;

    public CoachSkillEditHandler(SkillProfileRepository skills, CoachWriteOwnership ownership)
    {
        _skills = skills;
        _ownership = ownership;
    }

    public override string ToolName => CoachToolNames.ProposeSkillEdit;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.RestoreFields;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.SkillProfile;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachSkillEditArgs args, CancellationToken cancellationToken)
    {
        var (skill, changes) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        return new CoachWritePreview(
            $"Update the skill \u201c{Clean(skill.Title, 60)}\u201d",
            changes,
            skill.Id,
            Canonical(new CoachSkillEditArgs(
                skill.Id,
                Optional(args.Title, TitleMaxLength),
                Optional(args.Description, DescriptionMaxLength))));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachSkillEditArgs args, CancellationToken cancellationToken)
    {
        var (skill, changes) = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);
        var prior = new CoachSkillPriorState(skill.Title, skill.Description);

        if (Optional(args.Title, TitleMaxLength) is { } title)
        {
            skill.Title = title;
        }

        if (Optional(args.Description, DescriptionMaxLength) is { } description)
        {
            skill.Description = description;
        }

        if (string.IsNullOrWhiteSpace(await _skills.SaveAsync(skill, userProfileId).ConfigureAwait(false)))
        {
            throw DataAccessFailure(new InvalidOperationException("The skill was not updated."));
        }

        return new CoachWriteExecution(
            $"Updated the skill \u201c{Clean(skill.Title, 60)}\u201d",
            changes,
            skill.Id,
            Canonical(prior));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachSkillEditArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachSkillPriorState>(priorStateJson);

        var skill = await _ownership.FindSkillAsync(userProfileId, args.SkillId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        skill.Title = prior.Title;
        skill.Description = prior.Description;

        if (string.IsNullOrWhiteSpace(await _skills.SaveAsync(skill, userProfileId).ConfigureAwait(false)))
        {
            throw DataAccessFailure(new InvalidOperationException("The skill was not restored."));
        }

        return new CoachWriteExecution(
            $"Restored the skill \u201c{Clean(skill.Title, 60)}\u201d",
            Array.Empty<string>(),
            skill.Id,
            PriorStateJson: null);
    }

    private async Task<(SkillProfile Skill, IReadOnlyList<string> Changes)> ValidateAsync(
        string userProfileId, CoachSkillEditArgs args, CancellationToken cancellationToken)
    {
        var skill = await _ownership.FindSkillAsync(userProfileId, args.SkillId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var title = Optional(args.Title, TitleMaxLength);
        var description = Optional(args.Description, DescriptionMaxLength);

        if (title is { Length: 0 })
        {
            throw InvalidArgument("A skill needs a name.");
        }

        var changes = new List<string>();
        if (title is not null && !string.Equals(skill.Title ?? string.Empty, title, StringComparison.Ordinal))
        {
            changes.Add($"Name: {Display(skill.Title)} \u2192 {Display(title)}");
        }

        if (description is not null
            && !string.Equals(skill.Description ?? string.Empty, description, StringComparison.Ordinal))
        {
            changes.Add($"Focus: {Display(skill.Description)} \u2192 {Display(description)}");
        }

        if (changes.Count == 0)
        {
            throw InvalidArgument("That change would leave the skill exactly as it is.");
        }

        return (skill, changes);
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Length <= 80 ? value : value[..80];

    private static string? Optional(string? value, int maxLength) =>
        value is null ? null : Clean(value, maxLength);
}

// ---------------------------------------------------------------------------- archive

/// <summary>
/// Archives a skill the learner owns, without deleting it.
/// </summary>
/// <remarks>
/// <para>
/// Deletion used to live here and was wrong twice over. A skill is referenced by resources and by
/// plan history, so removing the row leaves those references pointing at nothing; and recreating
/// it would produce a new identifier that none of them point at, so the "restored" skill would
/// look right and be disconnected from everything. Archiving keeps the row and its identifier and
/// simply stops offering the skill for new practice, which is what the learner meant.
/// </para>
/// <para>
/// Still protected. Archiving takes a skill out of every list the learner practises from, which is
/// disruptive enough to deserve an explicit confirmation even though it is reversible.
/// </para>
/// <para>
/// Reversible, but only for as long as the ledger's undo window is open, and the consent copy says
/// so in those words. There is no archived-skills view and no restore control anywhere in the app:
/// <c>Skills.razor</c> lists through <c>SkillProfileRepository.ListAsync</c>, which excludes
/// archived rows, and <c>SkillEdit.razor</c> offers only delete. So "you can restore it from your
/// skills" described a screen that does not exist, and "nothing is deleted, and you can restore
/// it" invited the learner to confirm on the strength of a safety net nobody built. Until there is
/// an archive view to restore from, the honest promise is the bounded one: hidden now, kept not
/// deleted, undoable for the length of the window and not after it.
/// </para>
/// </remarks>
public sealed class CoachSkillArchiveHandler : CoachWriteHandlerBase<CoachSkillArchiveArgs>
{
    /// <summary>
    /// The undo window in the words the learner reads, taken from the window the ledger enforces.
    /// </summary>
    /// <remarks>
    /// Derived rather than typed out, so shortening <see cref="CoachWriteLimits.UndoWindow"/>
    /// cannot leave a confirmation prompt promising the old one.
    /// </remarks>
    private static readonly string UndoWindowText =
        $"{(int)CoachWriteLimits.UndoWindow.TotalMinutes} minutes";

    private readonly SkillProfileRepository _skills;
    private readonly CoachWriteOwnership _ownership;

    public CoachSkillArchiveHandler(SkillProfileRepository skills, CoachWriteOwnership ownership)
    {
        _skills = skills;
        _ownership = ownership;
    }

    public override string ToolName => CoachToolNames.ProposeSkillArchive;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteHard;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.RestoreFields;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.SkillProfile;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachSkillArchiveArgs args, CancellationToken cancellationToken)
    {
        var skill = await FindArchivableAsync(userProfileId, args.SkillId, cancellationToken)
            .ConfigureAwait(false);

        return new CoachWritePreview(
            $"Archive the skill \u201c{Clean(skill.Title, 60)}\u201d",
            new[]
            {
                $"Name: {Clean(skill.Title, 120)}",
                "It is hidden from your skills list and from everything you practise from.",
                "The skill and its history are kept, not deleted.",
                $"You can undo this for {UndoWindowText} after you confirm. After that the app has no way to bring it back."
            },
            skill.Id,
            Canonical(new CoachSkillArchiveArgs(skill.Id)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachSkillArchiveArgs args, CancellationToken cancellationToken)
    {
        var skill = await FindArchivableAsync(userProfileId, args.SkillId, cancellationToken)
            .ConfigureAwait(false);

        var title = Clean(skill.Title, 60);

        // The repository answers with the number of rows it changed, and answers zero when it
        // declined or when the row was already archived. Reporting success without reading that
        // answer is exactly how a receipt comes to describe something that did not happen.
        if (await _skills.SetArchivedAsync(skill.Id, isArchived: true, userProfileId).ConfigureAwait(false) <= 0)
        {
            throw DataAccessFailure(new InvalidOperationException("The skill was not archived."));
        }

        return new CoachWriteExecution(
            $"Archived the skill \u201c{title}\u201d",
            new[]
            {
                "It is hidden from your skills list. Nothing was deleted.",
                $"You can undo this for {UndoWindowText}. After that the app has no way to bring it back."
            },
            skill.Id,
            Canonical(new CoachSkillArchiveUndoState(skill.Id)));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachSkillArchiveArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachSkillArchiveUndoState>(priorStateJson);

        // Deliberately the archived-inclusive lookup. The row this undo exists to restore is
        // archived by definition, so the ordinary lookup — which hides archived rows the way the
        // practice lists do — would refuse every single undo.
        var skill = await _ownership.FindSkillAsync(
            userProfileId, prior.SkillId, cancellationToken, includeArchived: true)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        if (await _skills.SetArchivedAsync(skill.Id, isArchived: false, userProfileId).ConfigureAwait(false) <= 0)
        {
            throw DataAccessFailure(new InvalidOperationException("The skill was not restored."));
        }

        return new CoachWriteExecution(
            $"Restored the skill \u201c{Clean(skill.Title, 60)}\u201d",
            Array.Empty<string>(),
            skill.Id,
            PriorStateJson: null);
    }

    /// <summary>Resolves a skill that is the learner's and is not already archived.</summary>
    /// <remarks>
    /// An already-archived skill produces the same refusal as one that does not exist. A distinct
    /// "already archived" answer would confirm a row's existence to somebody guessing
    /// identifiers, which is the thing the ownership lookup exists to prevent.
    /// </remarks>
    private async Task<SkillProfile> FindArchivableAsync(
        string userProfileId, string skillId, CancellationToken cancellationToken)
    {
        var skill = await _ownership.FindSkillAsync(userProfileId, skillId, cancellationToken)
            .ConfigureAwait(false);

        return skill is null || skill.IsArchived ? throw NotFoundOrNotOwned() : skill;
    }
}
