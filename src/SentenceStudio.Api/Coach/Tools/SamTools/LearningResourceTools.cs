using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Practice;
using SentenceStudio.Application.Resources;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>Lists the learner's learning resources with metadata. No transcript or diary text.</summary>
public sealed class LearningResourceListTool : CoachToolBase
{
    /// <summary>The largest page this read will return, whatever the caller asks for.</summary>
    /// <remarks>Public so the read-capability metadata cites it rather than restating it.</remarks>
    public const int MaxResults = 30;

    private readonly ILearningResourceQueries _resources;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<LearningResourceListTool> _logger;

    public LearningResourceListTool(
        IUserScopeProvider userScope,
        ILearningResourceQueries resources,
        IPlanDateContext dates,
        ILogger<LearningResourceListTool> logger)
        : base(userScope)
    {
        _resources = resources;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetLearningResourceList;

    public async Task<LearningResourceListResult> GetAsync(int maxResults = 20, CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();
        maxResults = Math.Clamp(maxResults, 1, MaxResults);

        try
        {
            var total = await _resources.CountResourcesAsync(userId, ct);
            var rows = await _resources.GetRecentResourceSummariesAsync(userId, maxResults, ct);

            var entries = rows.Select(r => new LearningResourceListEntry(
                ResourceId: r.ResourceId,
                Title: SanitizeMetadata(r.Title, 120),
                MediaType: r.MediaType is null ? null : SanitizeMetadata(r.MediaType, 40),
                Language: r.Language is null ? null : SanitizeMetadata(r.Language, 40),
                VocabularyCount: r.VocabularyCount,
                HasTranscript: r.HasTranscript,
                Tags: SplitTags(r.Tags)
            )).ToList();

            return new LearningResourceListResult(
                total,
                entries.Count,
                entries,
                new CoachResultScope
                {
                    Coverage = total > entries.Count
                        ? CoachScopeCoverage.PageOfOwnedSet
                        : CoachScopeCoverage.CompleteOwnedSet,
                    Order = CoachScopeOrder.UpdatedDescending,
                    OrderHonored = true,
                    Filters = CoachScopeFilters.OwnerScoped,
                    AsOfUtc = _dates.UtcNow,
                    RequestedCount = maxResults,
                    ReturnedCount = entries.Count,
                    MatchedCount = total,
                    Truncated = total > entries.Count,
                    DefinitionCode = CoachScopeDefinition.OwnedResourceList,
                    EligiblePopulationCount = total,
                    MinimumEvidence = CoachScopeMinimumEvidence.None,
                    TieBreak = CoachScopeTieBreak.None,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }

    private static List<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => SanitizeMetadata(t, 40))
                .Where(t => t.Length > 0)
                .Take(8)
                .ToList();
}

/// <summary>Reads metadata for one learning resource the learner owns. No transcript.</summary>
public sealed class LearningResourceDetailTool : CoachToolBase
{
    private readonly ILearningResourceQueries _resources;
    private readonly IPracticeHistoryQueries _history;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<LearningResourceDetailTool> _logger;

    public LearningResourceDetailTool(
        IUserScopeProvider userScope,
        ILearningResourceQueries resources,
        IPracticeHistoryQueries history,
        IPlanDateContext dates,
        ILogger<LearningResourceDetailTool> logger)
        : base(userScope)
    {
        _resources = resources;
        _history = history;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetLearningResourceDetail;

    public async Task<LearningResourceDetailResult> GetAsync(string resourceId, CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();
        if (string.IsNullOrWhiteSpace(resourceId))
            throw InvalidArgument("The resource identifier is required.");

        try
        {
            var row = await _resources.GetResourceSummaryAsync(userId, resourceId, ct);

            if (row is null)
                throw InvalidArgument("The resource does not exist or does not belong to this learner.");

            var lastUsed = await _history.GetResourceLastUsedAsync(userId, resourceId, ct);

            int? daysSince = lastUsed is { } lu
                ? Math.Max(0, _dates.UserLocalDate.DayNumber - _dates.ToUserLocal(lu).DayNumber)
                : null;

            return new LearningResourceDetailResult(
                ResourceId: row.ResourceId,
                Title: SanitizeMetadata(row.Title, 120),
                MediaType: row.MediaType is null ? null : SanitizeMetadata(row.MediaType, 40),
                Language: row.Language is null ? null : SanitizeMetadata(row.Language, 40),
                VocabularyCount: row.VocabularyCount,
                HasAudio: IsAudio(row.MediaType, row.MediaUrl),
                HasTranscript: row.HasTranscript,
                HasVideo: IsVideo(row.MediaType, row.MediaUrl),
                IsSystemGenerated: row.IsSmartResource,
                Tags: SplitTags(row.Tags),
                DaysSinceLastUse: daysSince,
                Scope: new CoachResultScope
                {
                    Coverage = CoachScopeCoverage.SingleItem,
                    Order = CoachScopeOrder.NotApplicable,
                    OrderHonored = true,
                    Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.SingleIdentifier,
                    AsOfUtc = _dates.UtcNow,
                    ReturnedCount = 1,
                    DefinitionCode = CoachScopeDefinition.OwnedResourceDetail,
                    MinimumEvidence = CoachScopeMinimumEvidence.None,
                    TieBreak = CoachScopeTieBreak.NotApplicable,
                    ClockBasis = CoachScopeClockBasis.LearnerLocalDay,
                    ReferenceMode = CoachScopeReferenceMode.CalendarDay
                });
        }
        catch (CoachToolException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }

    private static bool IsVideo(string? mediaType, string? url) =>
        string.Equals(mediaType, "Video", StringComparison.OrdinalIgnoreCase)
        || (url is not null && url.Contains("youtu", StringComparison.OrdinalIgnoreCase));

    private static bool IsAudio(string? mediaType, string? url) =>
        IsVideo(mediaType, url)
        || string.Equals(mediaType, "Podcast", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => SanitizeMetadata(t, 40))
                .Where(t => t.Length > 0)
                .Take(8)
                .ToList();
}
