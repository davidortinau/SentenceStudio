using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Services.Plans;

/// <summary>One tag on the learner's words, with how many of their words carry it.</summary>
public sealed record LearnerTopicTag(string Tag, int WordCount);

/// <summary>One of the learner's own resources that has words, with how many.</summary>
public sealed record LearnerTopicResource(string ResourceId, string Title, int WordCount);

/// <summary>What the learner's vocabulary is about: their word tags and resource titles.</summary>
public sealed record LearnerTopics(
    IReadOnlyList<LearnerTopicTag> Tags,
    IReadOnlyList<LearnerTopicResource> Resources)
{
    public static readonly LearnerTopics Empty = new([], []);
}

/// <summary>
/// Reads the topics a learner can focus on ("food and restaurants"): the tags on their own words
/// and the titles of their own resources. Owner-scoped through <see cref="IUserScopeProvider"/>;
/// no scope means no topics.
/// </summary>
public interface ILearnerTopicQueries
{
    /// <summary>The learner's topics, at most <paramref name="maxEach"/> tags and resources each, most used first.</summary>
    Task<LearnerTopics> GetTopicsAsync(int maxEach, CancellationToken ct = default);

    /// <summary>The learner's topics, at most <see cref="LearnerTopicQueries.MaxEach"/> each.</summary>
    Task<LearnerTopics> GetTopicsAsync(CancellationToken ct = default) => GetTopicsAsync(LearnerTopicQueries.MaxEach, ct);
}

/// <inheritdoc />
public sealed class LearnerTopicQueries : ILearnerTopicQueries
{
    /// <summary>At most this many tags and this many resources, the most-used first.</summary>
    public const int MaxEach = 40;

    /// <summary>When matching a topic's names, enough to cover every resource a learner has.</summary>
    public const int MaxForMatching = 1000;

    // Tags the app writes on generated rows, not topics a learner chose.
    private static readonly HashSet<string> SystemTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "system-generated", "dynamic", "review", "phrases", "sentences", "srs", "new", "imported"
    };

    private readonly ApplicationDbContext _db;
    private readonly IUserScopeProvider _scope;
    private readonly ILogger<LearnerTopicQueries> _logger;

    public LearnerTopicQueries(ApplicationDbContext db, IUserScopeProvider scope, ILogger<LearnerTopicQueries> logger)
    {
        _db = db;
        _scope = scope;
        _logger = logger;
    }

    public async Task<LearnerTopics> GetTopicsAsync(int maxEach, CancellationToken ct = default)
    {
        maxEach = Math.Clamp(maxEach, 1, MaxForMatching);
        if (!_scope.TryGetUserProfileId(out var userId) || string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Learner topics requested with no active user scope; returning none.");
            return LearnerTopics.Empty;
        }

        var wordTags = await _db.VocabularyWords
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(w => w.Tags != null && w.Tags != "")
            .Select(w => w.Tags!)
            .ToListAsync(ct);

        var tags = wordTags
            .SelectMany(t => t.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.ToLowerInvariant())
            .Where(t => !SystemTags.Contains(t))
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => new LearnerTopicTag(g.Key, g.Count()))
            .OrderByDescending(t => t.WordCount)
            .ThenBy(t => t.Tag, StringComparer.Ordinal)
            .Take(maxEach)
            .ToList();

        var resources = await _db.LearningResources
            .AsNoTracking()
            .Where(r => r.UserProfileId == userId && !r.IsSmartResource && r.Title != null && r.Title != "")
            .Select(r => new
            {
                r.Id,
                Title = r.Title!,
                Words = _db.ResourceVocabularyMappings.Count(m => m.ResourceId == r.Id)
            })
            .Where(r => r.Words > 0)
            .OrderByDescending(r => r.Words)
            .ThenBy(r => r.Id)
            .Take(maxEach)
            .ToListAsync(ct);

        return new LearnerTopics(
            tags,
            resources.Select(r => new LearnerTopicResource(r.Id, r.Title, r.Words)).ToList());
    }
}
