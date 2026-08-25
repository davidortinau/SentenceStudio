using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>Why a focus request did not produce a selection.</summary>
public enum CoachFocusFailure
{
    /// <summary>The learner's words are not in the controlled registry.</summary>
    Unrecognized = 0,

    /// <summary>The registry mapped it, but the resolver refused the request itself.</summary>
    InvalidFocus,

    /// <summary>Too little of the learner's vocabulary is classified to answer honestly.</summary>
    MetadataUnavailable,

    /// <summary>Nothing the learner owns matches.</summary>
    NoMatches,

    /// <summary>Some matched, but fewer than a usable set.</summary>
    InsufficientMatches
}

/// <summary>The outcome of turning a learner description into a frozen selection.</summary>
public sealed record CoachFocusOutcome
{
    public CoachFocusSelection? Selection { get; init; }

    public CoachVocabularyFocusDto? Projection { get; init; }

    public CoachFocusFailure? Failure { get; init; }

    /// <summary>Owned words matching the filter, for the deterministic explanation.</summary>
    public int MatchedCount { get; init; }

    public bool IsSuccess => Selection is not null && Projection is not null;
}

/// <summary>
/// Turns a learner's description of a vocabulary focus into a frozen, owned selection.
/// </summary>
/// <remarks>
/// Two steps, both application-owned. The controlled registry decides what the words mean, and the
/// tenant-scoped resolver decides which of the learner's own vocabulary satisfies it. The model
/// contributes the description and nothing else, and never learns the answer.
/// </remarks>
public sealed class CoachVocabularyFocusService
{
    private readonly IVocabularyFocusResolver _resolver;
    private readonly ICoachLanguageResolver _languages;
    private readonly ILogger<CoachVocabularyFocusService> _logger;

    public CoachVocabularyFocusService(
        IVocabularyFocusResolver resolver,
        ICoachLanguageResolver languages,
        ILogger<CoachVocabularyFocusService> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves a focus for the scoped learner and freezes the answer.
    /// </summary>
    /// <remarks>
    /// Call only after intent validation: an unvalidated description must never reach a query.
    /// </remarks>
    public async Task<CoachFocusOutcome> ResolveAsync(
        string? description,
        string planVersion,
        CancellationToken cancellationToken)
    {
        if (!CoachVocabularyFocusAliases.TryMap(description, out var alias))
        {
            // Counts only: the learner's wording never reaches a log.
            _logger.LogInformation(
                "[Coach] A vocabulary focus description did not match the controlled registry; " +
                "asking one clarifying question and writing nothing.");

            return new CoachFocusOutcome { Failure = CoachFocusFailure.Unrecognized };
        }

        var result = await _resolver.ResolveAsync(
            new VocabularyFocusRequest
            {
                PartOfSpeech = alias.PartOfSpeech,
                DisplayDescription = null,
                RequestedCount = VocabularyFocusRequest.DefaultCount
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logger.LogInformation(
                "[Coach] Vocabulary focus {FocusCode} did not resolve: {Outcome}. " +
                "Matched {MatchedCount} of {ClassifiedCount} classified owned word(s). Nothing was written.",
                alias.FocusCode, result.Outcome, result.MatchedCount, result.ClassifiedCandidateCount);

            return new CoachFocusOutcome
            {
                Failure = Map(result.Outcome),
                MatchedCount = result.MatchedCount
            };
        }

        var languages = await _languages.ResolveAsync(cancellationToken).ConfigureAwait(false);

        // Trimmed at the boundary, and a gloss that is only whitespace becomes no gloss at all: a
        // client should never have to decide whether an empty string means "no translation".
        var words = result.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.TargetLanguageTerm))
            .Select(i =>
            {
                var gloss = i.NativeLanguageTerm?.Trim();
                gloss = string.IsNullOrEmpty(gloss) ? null : gloss;

                return new CoachVocabularyFocusWordDto
                {
                    TargetText = i.TargetLanguageTerm!.Trim(),
                    TargetLanguageTag = languages.TargetLanguageTag,
                    DisplayText = gloss,
                    DisplayLanguageTag = gloss is null ? null : languages.DisplayLanguageTag
                };
            })
            .ToList();

        var selection = new CoachFocusSelection
        {
            FocusCode = alias.FocusCode,
            VocabularyWordIds = result.SelectedVocabularyWordIds,
            ResolvedForPlanVersion = planVersion,
            EligibleCount = result.MatchedCount,
            Words = words
        };

        return new CoachFocusOutcome
        {
            Selection = selection,
            Projection = Project(selection, alias),
            MatchedCount = result.MatchedCount
        };
    }

    /// <summary>Projects a stored selection for display, without resolving again.</summary>
    public static CoachVocabularyFocusDto? Project(CoachFocusSelection? selection)
    {
        if (selection is null || !CoachVocabularyFocusAliases.TryFromCode(selection.FocusCode, out var alias))
        {
            return null;
        }

        return Project(selection, alias);
    }

    private static CoachVocabularyFocusDto Project(
        CoachFocusSelection selection, CoachVocabularyFocusAlias alias) => new()
    {
        FocusCode = alias.FocusCode,
        DisplayLabel = alias.DisplayLabel,
        EligibleCount = selection.EligibleCount,
        SelectedCount = selection.VocabularyWordIds.Count,
        Words = selection.Words ?? Array.Empty<CoachVocabularyFocusWordDto>()
    };

    private static CoachFocusFailure Map(VocabularyFocusOutcome outcome) => outcome switch
    {
        VocabularyFocusOutcome.InvalidFocus => CoachFocusFailure.InvalidFocus,
        VocabularyFocusOutcome.MetadataUnavailable => CoachFocusFailure.MetadataUnavailable,
        VocabularyFocusOutcome.NoMatches => CoachFocusFailure.NoMatches,
        VocabularyFocusOutcome.InsufficientMatches => CoachFocusFailure.InsufficientMatches,
        _ => CoachFocusFailure.InvalidFocus
    };
}
