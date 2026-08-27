using SentenceStudio.Application.Practice;
using SentenceStudio.Application.Resources;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Reads the resources the learner owns, as metadata only.
/// The answer holds titles, media types, capability flags, counts, and last use.
/// The answer never holds a transcript, a translation, or diary text.
/// Titles and tags come from imported data, so the tool cleans them and treats
/// them as data. They never change what the coach is allowed to do.
/// </summary>
public sealed class ResourceCatalogTool : CoachToolBase
{
    private const int MinResults = 1;
    /// <summary>
    /// The largest page this read will return, whatever the caller asks for.
    /// </summary>
    /// <remarks>
    /// Public because the read-capability metadata table states this ceiling to the manifest, and a
    /// number transcribed into that table by hand is a number that drifts the first time this one
    /// changes. The tool is the source; the table cites it.
    /// </remarks>
    public const int MaxResults = 50;

    /// <summary>The result count used when the model sends no value, or an explicit null.</summary>
    public const int DefaultResults = 20;

    private const int MaxTitleLength = 120;
    private const int MaxTagLength = 40;
    private const int MaxTagsPerResource = 8;

    private readonly ILearningResourceQueries _resources;
    private readonly IPracticeHistoryQueries _history;
    private readonly IPlanDateContext _dates;

    public ResourceCatalogTool(
        IUserScopeProvider userScope,
        ILearningResourceQueries resources,
        IPracticeHistoryQueries history,
        IPlanDateContext dates)
        : base(userScope)
    {
        _resources = resources;
        _history = history;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.GetResourceCatalog;

    /// <summary>Returns the owned resources, most recently used first.</summary>
    public async Task<ResourceCatalogSummary> GetAsync(
        int maxResults = DefaultResults,
        CancellationToken ct = default)
    {
        var userProfileId = RequireUserProfileId();

        if (maxResults is < MinResults or > MaxResults)
        {
            throw InvalidArgument($"The result count must be from {MinResults} to {MaxResults}.");
        }

        var today = _dates.UserLocalDate;

        IReadOnlyList<LearningResourceSummary> rows;
        int totalCount;
        IReadOnlyDictionary<string, DateTime> lastUsed;
        try
        {
            totalCount = await _resources.CountResourcesAsync(userProfileId, ct);
            rows = await _resources.GetResourceSummariesAsync(userProfileId, ct);
            lastUsed = await _history.GetResourceLastUsedAsync(userProfileId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw DataAccessFailure(ex);
        }

        var entries = rows
            .Select(r =>
            {
                int? daysSince = lastUsed.TryGetValue(r.ResourceId, out var used)
                    ? Math.Max(0, today.DayNumber - _dates.ToUserLocal(used).DayNumber)
                    : null;

                return new ResourceCatalogEntry(
                    ResourceId: r.ResourceId,
                    Title: SanitizeMetadata(r.Title, MaxTitleLength),
                    MediaType: NullIfEmpty(SanitizeMetadata(r.MediaType, 40)),
                    Language: NullIfEmpty(SanitizeMetadata(r.Language, 40)),
                    VocabularyCount: r.VocabularyCount,
                    HasAudio: HasAudio(r.MediaType, r.MediaUrl),
                    HasTranscript: r.HasTranscript,
                    HasVideo: IsVideo(r.MediaType, r.MediaUrl),
                    IsSystemGenerated: r.IsSmartResource,
                    Tags: SplitTags(r.Tags),
                    DaysSinceLastUse: daysSince);
            })
            .OrderBy(e => e.DaysSinceLastUse ?? int.MaxValue)
            .ThenBy(e => e.Title, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();

        return new ResourceCatalogSummary(
            totalCount,
            entries.Count,
            entries,
            new CoachResultScope
            {
                Coverage = totalCount > entries.Count
                    ? CoachScopeCoverage.PageOfOwnedSet
                    : CoachScopeCoverage.CompleteOwnedSet,
                Order = CoachScopeOrder.LastUsedAscending,
                OrderHonored = true,
                Filters = CoachScopeFilters.OwnerScoped,
                AsOfUtc = _dates.UtcNow,
                RequestedCount = maxResults,
                ReturnedCount = entries.Count,
                MatchedCount = totalCount,
                Truncated = totalCount > entries.Count,
                DefinitionCode = CoachScopeDefinition.OwnedResourceCatalog,
                EligiblePopulationCount = totalCount,
                MinimumEvidence = CoachScopeMinimumEvidence.None,
                TieBreak = CoachScopeTieBreak.TitleOrdinal,
                // Days-since-use is counted on the learner's calendar, not on a UTC clock, so a
                // resource used last night is "yesterday" for them regardless of the server.
                ClockBasis = CoachScopeClockBasis.LearnerLocalDay,
                ReferenceMode = CoachScopeReferenceMode.CalendarDay
            });
    }

    private static bool HasAudio(string? mediaType, string? mediaUrl) =>
        IsVideo(mediaType, mediaUrl)
        || string.Equals(mediaType, "Podcast", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideo(string? mediaType, string? mediaUrl) =>
        string.Equals(mediaType, "Video", StringComparison.OrdinalIgnoreCase)
        || (mediaUrl is not null && mediaUrl.Contains("youtu", StringComparison.OrdinalIgnoreCase));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static List<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => SanitizeMetadata(t, MaxTagLength))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTagsPerResource)
                .ToList();
}
