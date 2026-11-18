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
    /// <summary>Focus codes of topic selections start with this; the rest names the topic.</summary>
    public const string TopicCodePrefix = "topic:";

    private readonly IVocabularyFocusResolver _resolver;
    private readonly ICoachLanguageResolver _languages;
    private readonly ILogger<CoachVocabularyFocusService> _logger;
    private readonly ILearnerTopicQueries? _topics;

    public CoachVocabularyFocusService(
        IVocabularyFocusResolver resolver,
        ICoachLanguageResolver languages,
        ILogger<CoachVocabularyFocusService> logger,
        ILearnerTopicQueries? topics = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _topics = topics;
    }

    /// <summary>
    /// Resolves a focus for the scoped learner and freezes the answer.
    /// </summary>
    /// <remarks>
    /// Call only after intent validation: an unvalidated description must never reach a query.
    /// </remarks>
    public Task<CoachFocusOutcome> ResolveAsync(
        string? description,
        string planVersion,
        CancellationToken cancellationToken) =>
        ResolveAsync(description, topics: null, planVersion, cancellationToken);

    /// <summary>
    /// Resolves a focus: a kind of word from the controlled registry ("active verbs"), or else a
    /// topic ("food and restaurants") from the learner's own tags and resource titles.
    /// </summary>
    /// <param name="topics">Tag and resource names the model chose from get_resource_catalog; only
    /// names the learner has are used.</param>
    public async Task<CoachFocusOutcome> ResolveAsync(
        string? description,
        IReadOnlyList<string>? topics,
        string planVersion,
        CancellationToken cancellationToken)
    {
        if (!CoachVocabularyFocusAliases.TryMap(description, out var alias))
        {
            if (_topics is not null)
            {
                var topic = await ResolveTopicAsync(description, topics, planVersion, cancellationToken)
                    .ConfigureAwait(false);
                if (topic is not null)
                    return topic;
            }

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

    /// <summary>
    /// A topic focus: the named tags and resources the learner has, then the words that carry those
    /// tags or belong to those resources. Null when no name is the learner's, so the caller asks.
    /// </summary>
    private async Task<CoachFocusOutcome?> ResolveTopicAsync(
        string? description,
        IReadOnlyList<string>? names,
        string planVersion,
        CancellationToken cancellationToken)
    {
        // Every resource of the learner's, not only the most-used page, so a title copied from the
        // catalog is always found.
        var learner = await _topics!.GetTopicsAsync(LearnerTopicQueries.MaxForMatching, cancellationToken).ConfigureAwait(false);
        var match = MatchTopics(description, names, learner);
        if (match.Tags.Count == 0 && match.Resources.Count == 0)
        {
            _logger.LogInformation(
                "[Coach] A topic focus named none of the learner's {TagCount} tags or {ResourceCount} resources; asking instead.",
                learner.Tags.Count, learner.Resources.Count);
            return null;
        }

        var result = await _resolver.ResolveAsync(
            new VocabularyFocusRequest
            {
                CategoryTags = match.Tags,
                ResourceIds = match.Resources.Select(r => r.ResourceId).ToList(),
                RequestedCount = VocabularyFocusRequest.DefaultCount
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logger.LogInformation(
                "[Coach] Topic focus did not resolve: {Outcome}. Matched {MatchedCount} owned word(s). Nothing was written.",
                result.Outcome, result.MatchedCount);
            return new CoachFocusOutcome { Failure = Map(result.Outcome), MatchedCount = result.MatchedCount };
        }

        var label = TopicLabel(match);
        var selection = new CoachFocusSelection
        {
            // The code holds no learner text: it is kept in the permanent revision audit, where only
            // the session row may hold the label.
            FocusCode = TopicCodePrefix + TopicHash(match),
            DisplayLabel = label,
            VocabularyWordIds = result.SelectedVocabularyWordIds,
            ResolvedForPlanVersion = planVersion,
            EligibleCount = result.MatchedCount,
            Words = await WordsAsync(result, cancellationToken).ConfigureAwait(false)
        };

        _logger.LogInformation(
            "[Coach] Topic focus resolved from {TagCount} tag(s) and {ResourceCount} resource(s): {Selected} of {Matched} word(s).",
            match.Tags.Count, match.Resources.Count, selection.VocabularyWordIds.Count, result.MatchedCount);

        return new CoachFocusOutcome { Selection = selection, Projection = Project(selection), MatchedCount = result.MatchedCount };
    }

    /// <summary>The learner's tags and resources a topic names.</summary>
    public sealed record TopicMatch(IReadOnlyList<string> Tags, IReadOnlyList<LearnerTopicResource> Resources);

    /// <summary>
    /// Keeps only names the learner has: a tag compared whole, a resource by its exact title. With no
    /// usable name, words of the learner's description are matched to whole tags and to whole words
    /// of resource titles, so "food" finds the "food" tag without the model's help.
    /// </summary>
    public static TopicMatch MatchTopics(string? description, IReadOnlyList<string>? names, LearnerTopics learner)
    {
        var tags = new List<string>();
        var resources = new List<LearnerTopicResource>();
        foreach (var name in names ?? [])
        {
            var trimmed = name.Trim();
            var tag = learner.Tags.FirstOrDefault(t => string.Equals(t.Tag, trimmed, StringComparison.OrdinalIgnoreCase));
            if (tag is not null && !tags.Contains(tag.Tag))
                tags.Add(tag.Tag);
            foreach (var resource in learner.Resources.Where(r => string.Equals(r.Title.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
                if (!resources.Contains(resource))
                    resources.Add(resource);
        }

        if (tags.Count == 0 && resources.Count == 0 && !string.IsNullOrWhiteSpace(description))
        {
            var words = Words(description).Where(w => w.Length >= 3 && !FillerWords.Contains(w))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            tags.AddRange(learner.Tags.Where(t => words.Contains(t.Tag)).Select(t => t.Tag));
            resources.AddRange(learner.Resources.Where(r => Words(r.Title).Any(words.Contains)));
        }

        return new TopicMatch(
            tags.Take(VocabularyFocusRequest.MaxTopicFilters).ToList(),
            resources.Take(VocabularyFocusRequest.MaxTopicFilters).ToList());
    }

    // Words a request is made of rather than about: "I want to study food and restaurant content".
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "for", "with", "about", "want", "study", "learn", "practice", "practise", "today",
        "words", "word", "vocabulary", "content", "some", "more", "things", "stuff", "focus", "plan",
        "please", "today's", "todays", "에", "관련", "단어", "공부", "오늘"
    };

    /// <summary>A short, stable code for the tags and resources a topic matched.</summary>
    private static string TopicHash(TopicMatch match)
    {
        var key = string.Join("|", match.Tags.OrderBy(t => t, StringComparer.Ordinal))
            + "#" + string.Join("|", match.Resources.Select(r => r.ResourceId).OrderBy(r => r, StringComparer.Ordinal));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static IEnumerable<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0);

    /// <summary>
    /// "words about food and At the restaurant", at most three names. It reads in the shared copy:
    /// "I found 10 matching words about food and At the restaurant for this plan."
    /// </summary>
    private static string TopicLabel(TopicMatch match)
    {
        // A title that only repeats a tag ("food vocabulary" next to "food") adds nothing to the label.
        var tags = match.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titles = match.Resources.Select(r => r.Title.Trim()).Where(t => !Words(t).Any(tags.Contains));
        var names = match.Tags.Concat(titles)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        if (names.Count == 0)
            names = match.Resources.Select(r => r.Title.Trim()).Take(1).ToList();
        var joined = names.Count <= 1
            ? string.Join("", names)
            : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        var label = "words about " + joined;
        return label.Length > 80 ? label[..80].TrimEnd() : label;
    }

    private async Task<IReadOnlyList<CoachVocabularyFocusWordDto>> WordsAsync(
        VocabularyFocusResult result, CancellationToken cancellationToken)
    {
        var languages = await _languages.ResolveAsync(cancellationToken).ConfigureAwait(false);
        return result.Items
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
    }

    /// <summary>Projects a stored selection for display, without resolving again.</summary>
    public static CoachVocabularyFocusDto? Project(CoachFocusSelection? selection)
    {
        if (selection is null)
        {
            return null;
        }

        if (selection.FocusCode.StartsWith(TopicCodePrefix, StringComparison.Ordinal))
        {
            return new CoachVocabularyFocusDto
            {
                FocusCode = selection.FocusCode,
                // Read back from the audit, the label is gone (it holds no learner text).
                DisplayLabel = selection.DisplayLabel ?? "words on your topic",
                EligibleCount = selection.EligibleCount,
                SelectedCount = selection.VocabularyWordIds.Count,
                Words = selection.Words ?? Array.Empty<CoachVocabularyFocusWordDto>()
            };
        }

        if (!CoachVocabularyFocusAliases.TryFromCode(selection.FocusCode, out var alias))
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
