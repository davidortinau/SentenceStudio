using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Progress;

/// <summary>
/// Reads a Learning Resource's study history for one owner (issue #219): sessions and minutes
/// by activity from <see cref="DailyPlanCompletion"/> (plan and Choose My Own sessions both
/// carry the resource), and the mastery spread of the resource's words from
/// <see cref="VocabularyProgress"/>. Read-only.
/// </summary>
/// <remarks>
/// Every read is filtered on the owner the caller passes: the resource, its words, the
/// completions and the progress rows. No owner, or a resource the owner does not hold, reads
/// nothing and returns null (logged), never another learner's rows. Singleton-safe: it opens a
/// scope per call rather than holding a context.
/// </remarks>
public sealed class ResourceStudySummaryService(
    IServiceProvider services, ILogger<ResourceStudySummaryService> logger)
{
    public async Task<ResourceStudySummary?> GetAsync(
        string? userProfileId, string? resourceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            logger.LogWarning("Resource study summary refused without an active owner.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            logger.LogWarning("Resource study summary refused without a resource.");
            return null;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var owned = await db.LearningResources.AsNoTracking()
            .AnyAsync(resource => resource.Id == resourceId
                && resource.UserProfileId == userProfileId, cancellationToken);
        if (!owned)
        {
            logger.LogWarning(
                "Resource study summary refused resource {ResourceId}: the active owner does not hold it.",
                resourceId);
            return null;
        }

        // A session is a plan or Choose My Own item that was begun (minutes logged) or
        // completed; a plan item never started is not study. Archived rows (a plan reset)
        // keep the minutes the learner really spent, so they count.
        var sessions = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == userProfileId
                && row.ResourceId == resourceId
                && (row.IsCompleted || row.MinutesSpent > 0))
            .Select(row => new
            {
                row.Id, row.ActivityType, row.MinutesSpent, row.Date,
                row.CompletedAt, row.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var activities = sessions
            .GroupBy(row => ParseActivity(row.ActivityType))
            .Select(group => new ResourceActivityStudy(
                group.Key, group.Count(), group.Sum(row => Math.Max(0, row.MinutesSpent))))
            .OrderByDescending(activity => activity.Minutes)
            .ThenByDescending(activity => activity.Sessions)
            .ThenBy(activity => activity.ActivityType.HasValue ? (int)activity.ActivityType.Value : int.MaxValue)
            .ToArray();
        var last = sessions
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.CompletedAt ?? row.UpdatedAt)
            .ThenByDescending(row => row.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        // The resource's words are the owner's words mapped to it (one owner per word).
        var wordIds = db.ResourceVocabularyMappings
            .Where(mapping => mapping.ResourceId == resourceId)
            .Select(mapping => mapping.VocabularyWordId)
            .Where(wordId => db.VocabularyWords.Any(word => word.Id == wordId
                && word.UserProfileId == userProfileId))
            .Distinct();
        var wordCount = await wordIds.CountAsync(cancellationToken);
        var progress = wordCount == 0
            ? []
            : await db.VocabularyProgresses.AsNoTracking()
                .Where(row => row.UserId == userProfileId
                    && wordIds.Contains(row.VocabularyWordId))
                .ToListAsync(cancellationToken);
        // One progress row per word and learner; should a duplicate exist, the latest wins.
        var perWord = progress
            .GroupBy(row => row.VocabularyWordId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => row.UpdatedAt).First())
            .ToArray();
        var learning = perWord.Count(row => row.Status == LearningStatus.Learning);
        var familiar = perWord.Count(row => row.Status == LearningStatus.Familiar);
        var known = perWord.Count(row => row.Status == LearningStatus.Known);
        var unknown = Math.Max(0, wordCount - learning - familiar - known);
        var average = wordCount == 0 ? 0
            : perWord.Sum(row => Math.Clamp((double)row.MasteryScore, 0, 1)) / wordCount;

        return new ResourceStudySummary(
            resourceId,
            sessions.Count,
            activities.Sum(activity => activity.Minutes),
            activities,
            last?.Date.Date,
            last is null ? null : ParseActivity(last.ActivityType),
            wordCount, unknown, learning, familiar, known, average);
    }

    private static PlanActivityType? ParseActivity(string? value) =>
        Enum.TryParse<PlanActivityType>(value, ignoreCase: false, out var type)
            && Enum.IsDefined(type)
            ? type : null;
}
