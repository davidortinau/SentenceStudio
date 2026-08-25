using System.ComponentModel;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Operations.Handlers;

/// <summary>Arguments for creating a learning resource.</summary>
public sealed record CoachResourceEntryArgs(
    [property: Description("Title of the resource.")]
    string Title,
    [property: Description("What the resource contains or is for.")]
    string Description,
    [property: Description("Kind of resource, for example Vocabulary List, Article, or Video.")]
    string? MediaType = null,
    [property: Description("Language of the resource. Defaults to the learner's target language.")]
    string? Language = null,
    [property: Description("Optional comma-separated tags.")]
    string? Tags = null);

/// <summary>Arguments for editing one of the learner's resources.</summary>
public sealed record CoachResourceEditArgs(
    [property: Description("Identifier of the resource to change.")]
    string ResourceId,
    [property: Description("Replacement title. Omit to leave unchanged.")]
    string? Title = null,
    [property: Description("Replacement description. Omit to leave unchanged.")]
    string? Description = null,
    [property: Description("Replacement comma-separated tags. Omit to leave unchanged.")]
    string? Tags = null);

/// <summary>Arguments for deleting one of the learner's resources.</summary>
public sealed record CoachResourceRemovalArgs(
    [property: Description("Identifier of the resource to delete.")]
    string ResourceId);

/// <summary>The resource fields an edit replaced.</summary>
public sealed record CoachResourcePriorState(string? Title, string? Description, string? Tags);

/// <summary>What a resource creation produced, so undo can remove exactly that row.</summary>
public sealed record CoachResourceEntryUndoState(string ResourceId);

// ---------------------------------------------------------------------------- create

/// <summary>Creates a learning resource owned by the learner.</summary>
public sealed class CoachResourceEntryHandler : CoachWriteHandlerBase<CoachResourceEntryArgs>
{
    private const int TitleMaxLength = 200;
    private const int DescriptionMaxLength = 2000;
    private const int MediaTypeMaxLength = 60;
    private const int LanguageMaxLength = 40;
    private const int TagsMaxLength = 400;

    /// <summary>
    /// The resource kinds this tool may create.
    /// </summary>
    /// <remarks>
    /// A closed set rather than free text. <c>LearningResource.IsVocabularyList</c> keys off the
    /// exact string "Vocabulary List", and <c>IsSmartResource</c> marks rows the app generates
    /// itself, so an unconstrained value here could either miss a behaviour the learner expects
    /// or claim provenance the row does not have.
    /// </remarks>
    private static readonly string[] AllowedMediaTypes =
    [
        "Vocabulary List", "Article", "Video", "Audio", "Book", "Conversation", "Other"
    ];

    private readonly LearningResourceRepository _resources;
    private readonly CoachWriteOwnership _ownership;

    public CoachResourceEntryHandler(LearningResourceRepository resources, CoachWriteOwnership ownership)
    {
        _resources = resources;
        _ownership = ownership;
    }

    public override string ToolName => CoachToolNames.ProposeResourceEntry;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.DeleteCreatedEntity;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.LearningResource;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachResourceEntryArgs args, CancellationToken cancellationToken)
    {
        var normalized = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        return new CoachWritePreview(
            $"Create the resource \u201c{normalized.Title}\u201d",
            new[]
            {
                $"Title: {normalized.Title}",
                $"About: {normalized.Description}",
                $"Kind: {normalized.MediaType}",
                $"Language: {normalized.Language}"
            },
            EntityId: null,
            Canonical(normalized));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachResourceEntryArgs args, CancellationToken cancellationToken)
    {
        var normalized = await ValidateAsync(userProfileId, args, cancellationToken).ConfigureAwait(false);

        var resource = new LearningResource
        {
            Title = normalized.Title,
            Description = normalized.Description,
            MediaType = normalized.MediaType,
            Language = normalized.Language,
            Tags = string.IsNullOrEmpty(normalized.Tags) ? null : normalized.Tags,
            // Never true from here. That flag means the app's own pipeline produced the row, and
            // a learner-requested resource has different provenance even when the text came from
            // the model.
            IsSmartResource = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var id = await _resources.SaveResourceAsync(resource, userProfileId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw DataAccessFailure(new InvalidOperationException("The resource was not saved."));
        }

        return new CoachWriteExecution(
            $"Created the resource \u201c{normalized.Title}\u201d",
            new[] { $"Title: {normalized.Title}", $"Kind: {normalized.MediaType}" },
            id,
            Canonical(new CoachResourceEntryUndoState(id)));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachResourceEntryArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachResourceEntryUndoState>(priorStateJson);

        var resource = await _ownership.FindResourceAsync(userProfileId, prior.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        // The repository answers with the number of rows it removed, and answers zero when it
        // declined. An undo that ignores that answer reports Undone for a resource that is still
        // there, which is worse than refusing: the learner stops looking for it.
        if (await _resources.DeleteResourceAsync(resource, userProfileId).ConfigureAwait(false) <= 0)
        {
            throw DataAccessFailure(new InvalidOperationException("The resource was not removed."));
        }

        return new CoachWriteExecution(
            $"Removed the resource \u201c{Clean(resource.Title, 60)}\u201d",
            Array.Empty<string>(),
            resource.Id,
            PriorStateJson: null);
    }

    private async Task<CoachResourceEntryArgs> ValidateAsync(
        string userProfileId, CoachResourceEntryArgs args, CancellationToken cancellationToken)
    {
        var title = Clean(args.Title, TitleMaxLength);
        var description = Clean(args.Description, DescriptionMaxLength);

        if (title.Length == 0)
        {
            throw InvalidArgument("A resource needs a title.");
        }

        if (description.Length == 0)
        {
            throw InvalidArgument("A resource needs a description.");
        }

        var mediaType = Clean(args.MediaType, MediaTypeMaxLength);
        if (mediaType.Length == 0)
        {
            mediaType = "Other";
        }
        else if (!AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            throw InvalidArgument(
                $"A resource kind must be one of: {string.Join(", ", AllowedMediaTypes)}.");
        }
        else
        {
            mediaType = AllowedMediaTypes.First(t => string.Equals(t, mediaType, StringComparison.OrdinalIgnoreCase));
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

        return new CoachResourceEntryArgs(
            title, description, mediaType, language, Clean(args.Tags, TagsMaxLength));
    }
}

// ---------------------------------------------------------------------------- edit

/// <summary>Edits a learning resource the learner owns.</summary>
public sealed class CoachResourceEditHandler : CoachWriteHandlerBase<CoachResourceEditArgs>
{
    private const int TitleMaxLength = 200;
    private const int DescriptionMaxLength = 2000;
    private const int TagsMaxLength = 400;

    private readonly LearningResourceRepository _resources;
    private readonly CoachWriteOwnership _ownership;

    public CoachResourceEditHandler(LearningResourceRepository resources, CoachWriteOwnership ownership)
    {
        _resources = resources;
        _ownership = ownership;
    }

    public override string ToolName => CoachToolNames.ProposeResourceEdit;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.RestoreFields;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.LearningResource;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachResourceEditArgs args, CancellationToken cancellationToken)
    {
        var (resource, changes) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        return new CoachWritePreview(
            $"Update the resource \u201c{Clean(resource.Title, 60)}\u201d",
            changes,
            resource.Id,
            Canonical(new CoachResourceEditArgs(
                resource.Id,
                Optional(args.Title, TitleMaxLength),
                Optional(args.Description, DescriptionMaxLength),
                Optional(args.Tags, TagsMaxLength))));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachResourceEditArgs args, CancellationToken cancellationToken)
    {
        var (resource, changes) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        var prior = new CoachResourcePriorState(resource.Title, resource.Description, resource.Tags);

        if (Optional(args.Title, TitleMaxLength) is { } title)
        {
            resource.Title = title;
        }

        if (Optional(args.Description, DescriptionMaxLength) is { } description)
        {
            resource.Description = description;
        }

        if (Optional(args.Tags, TagsMaxLength) is { } tags)
        {
            resource.Tags = tags.Length == 0 ? null : tags;
        }

        resource.UpdatedAt = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(
                await _resources.SaveResourceAsync(resource, userProfileId).ConfigureAwait(false)))
        {
            throw DataAccessFailure(new InvalidOperationException("The resource was not updated."));
        }

        return new CoachWriteExecution(
            $"Updated the resource \u201c{Clean(resource.Title, 60)}\u201d",
            changes,
            resource.Id,
            Canonical(prior));
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachResourceEditArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachResourcePriorState>(priorStateJson);

        var resource = await _ownership.FindResourceAsync(userProfileId, args.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        resource.Title = prior.Title;
        resource.Description = prior.Description;
        resource.Tags = prior.Tags;
        resource.UpdatedAt = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(
                await _resources.SaveResourceAsync(resource, userProfileId).ConfigureAwait(false)))
        {
            throw DataAccessFailure(new InvalidOperationException("The resource was not restored."));
        }

        return new CoachWriteExecution(
            $"Restored the resource \u201c{Clean(resource.Title, 60)}\u201d",
            Array.Empty<string>(),
            resource.Id,
            PriorStateJson: null);
    }

    private async Task<(LearningResource Resource, IReadOnlyList<string> Changes)> ValidateAsync(
        string userProfileId, CoachResourceEditArgs args, CancellationToken cancellationToken)
    {
        var resource = await _ownership.FindResourceAsync(userProfileId, args.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var title = Optional(args.Title, TitleMaxLength);
        if (title is { Length: 0 })
        {
            throw InvalidArgument("A resource needs a title.");
        }

        var description = Optional(args.Description, DescriptionMaxLength);
        var tags = Optional(args.Tags, TagsMaxLength);

        var changes = new List<string>();
        if (title is not null && !string.Equals(resource.Title ?? string.Empty, title, StringComparison.Ordinal))
        {
            changes.Add($"Title: {Display(resource.Title)} \u2192 {Display(title)}");
        }

        if (description is not null
            && !string.Equals(resource.Description ?? string.Empty, description, StringComparison.Ordinal))
        {
            changes.Add($"About: {Display(resource.Description)} \u2192 {Display(description)}");
        }

        if (tags is not null && !string.Equals(resource.Tags ?? string.Empty, tags, StringComparison.Ordinal))
        {
            changes.Add($"Tags: {Display(resource.Tags)} \u2192 {Display(tags)}");
        }

        if (changes.Count == 0)
        {
            throw InvalidArgument("That change would leave the resource exactly as it is.");
        }

        return (resource, changes);
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Length <= 80 ? value : value[..80];

    private static string? Optional(string? value, int maxLength) =>
        value is null ? null : Clean(value, maxLength);
}

// ---------------------------------------------------------------------------- removal

/// <summary>
/// Deletes a learning resource the learner owns.
/// </summary>
/// <remarks>
/// Protected and irreversible. Deleting a resource takes its vocabulary mappings with it, and
/// because vocabulary ownership runs through those mappings, an "undo" would have to reconstruct
/// the whole graph — including words that may since have been remapped elsewhere.
/// </remarks>
public sealed class CoachResourceRemovalHandler : CoachWriteHandlerBase<CoachResourceRemovalArgs>
{
    private readonly LearningResourceRepository _resources;
    private readonly CoachWriteOwnership _ownership;

    public CoachResourceRemovalHandler(LearningResourceRepository resources, CoachWriteOwnership ownership)
    {
        _resources = resources;
        _ownership = ownership;
    }

    public override string ToolName => CoachToolNames.ProposeResourceRemoval;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteHard;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.LearningResource;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachResourceRemovalArgs args, CancellationToken cancellationToken)
    {
        var resource = await _ownership.FindResourceAsync(userProfileId, args.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        return new CoachWritePreview(
            $"Delete the resource \u201c{Clean(resource.Title, 60)}\u201d",
            new[]
            {
                $"Title: {Clean(resource.Title, 120)}",
                "Its vocabulary links go with it.",
                "This cannot be undone."
            },
            resource.Id,
            Canonical(new CoachResourceRemovalArgs(resource.Id)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachResourceRemovalArgs args, CancellationToken cancellationToken)
    {
        var resource = await _ownership.FindResourceAsync(userProfileId, args.ResourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var title = Clean(resource.Title, 60);
        if (await _resources.DeleteResourceAsync(resource, userProfileId).ConfigureAwait(false) <= 0)
        {
            throw DataAccessFailure(new InvalidOperationException("The resource was not deleted."));
        }

        return new CoachWriteExecution(
            $"Deleted the resource \u201c{title}\u201d",
            Array.Empty<string>(),
            resource.Id,
            PriorStateJson: null);
    }
}
