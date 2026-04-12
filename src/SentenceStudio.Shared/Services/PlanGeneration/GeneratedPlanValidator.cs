using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.PlanGeneration;

/// <summary>
/// Validates a generated PlanSkeleton against pedagogical and structural invariants.
/// Returns a list of critical violations and warnings.
/// </summary>
public class GeneratedPlanValidator
{
    public PlanValidationResult Validate(
        PlanSkeleton plan,
        List<DailyPlanCompletion> recentCompletions,
        Dictionary<string, LearningResource> resources)
    {
        var result = new PlanValidationResult();

        if (plan == null)
        {
            result.CriticalViolations.Add("Plan is null");
            return result;
        }

        ValidateVocabularyReview(plan, result);
        ValidateResourceSelection(plan, recentCompletions, resources, result);
        ValidateActivityCompatibility(plan, resources, result);
        ValidateSessionBudget(plan, result);
        ValidateActivityVariety(plan, recentCompletions, result);
        ValidateResourceRecency(plan, recentCompletions, result);

        return result;
    }

    private void ValidateVocabularyReview(PlanSkeleton plan, PlanValidationResult result)
    {
        var review = plan.VocabularyReview;
        if (review == null) return;

        // Invariant 1: No known words in review
        var knownWords = review.DueWords.Where(w => w.IsKnown).ToList();
        if (knownWords.Any())
        {
            result.CriticalViolations.Add(
                $"Known words in review: {knownWords.Count} words with IsKnown=true " +
                $"(MasteryScore >= 0.85 AND ProductionInStreak >= 2) should not be in DueWords");
        }

        // Invariant 2: Promoted words (MasteryScore >= 0.50) should be flagged for production mode
        var promotedWords = review.DueWords
            .Where(w => w.MasteryScore >= 0.50f && !w.IsKnown)
            .ToList();
        if (promotedWords.Any())
        {
            // Check if the plan indicates production mode for these words
            // Currently, there's no per-word mode tracking in VocabularyReviewBlock
            var hasProductionModeInfo = false; // The block doesn't track per-word modes
            if (!hasProductionModeInfo && promotedWords.Any())
            {
                result.CriticalViolations.Add(
                    $"Promoted words without production mode: {promotedWords.Count} words with " +
                    $"MasteryScore >= 0.50 should use Text mode, but VocabularyReviewBlock " +
                    $"has no per-word mode tracking");
            }
        }

        // Invariant 3: Contextual review overlap >= 40%
        if (review.IsContextual && !string.IsNullOrEmpty(review.ResourceId))
        {
            var wordsWithResourceContext = review.DueWords
                .Count(w => w.LearningContexts
                    .Any(lc => lc.LearningResourceId == review.ResourceId));
            var overlapPercent = review.DueWords.Count > 0
                ? (double)wordsWithResourceContext / review.DueWords.Count
                : 0;

            if (overlapPercent < 0.40)
            {
                result.CriticalViolations.Add(
                    $"Low contextual overlap: {overlapPercent:P0} of due words overlap with " +
                    $"resource {review.ResourceId} (need >= 40%)");
            }
        }

        // Invariant 4: WordCount matches actual selected words
        if (review.WordCount != review.DueWords.Count)
        {
            result.CriticalViolations.Add(
                $"WordCount mismatch: WordCount={review.WordCount} but " +
                $"DueWords.Count={review.DueWords.Count}");
        }
    }

    private void ValidateResourceSelection(
        PlanSkeleton plan,
        List<DailyPlanCompletion> recentCompletions,
        Dictionary<string, LearningResource> resources,
        PlanValidationResult result)
    {
        if (plan.PrimaryResource == null) return;

        // Invariant 5: ResourceId must reference a valid resource
        foreach (var activity in plan.Activities.Where(a => !string.IsNullOrEmpty(a.ResourceId)))
        {
            if (!resources.ContainsKey(activity.ResourceId!))
            {
                result.CriticalViolations.Add(
                    $"Invalid ResourceId: Activity '{activity.ActivityType}' references " +
                    $"resource '{activity.ResourceId}' which doesn't exist");
            }
        }

        // Invariant 6: No primary resource used yesterday
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        var yesterdayResources = recentCompletions
            .Where(c => c.Date.Date == yesterday && !string.IsNullOrEmpty(c.ResourceId))
            .Select(c => c.ResourceId!)
            .ToHashSet();

        if (yesterdayResources.Contains(plan.PrimaryResource.Id))
        {
            result.CriticalViolations.Add(
                $"Yesterday's resource reused: Resource '{plan.PrimaryResource.Title}' " +
                $"(ID: {plan.PrimaryResource.Id}) was used yesterday");
        }
    }

    private void ValidateActivityCompatibility(
        PlanSkeleton plan,
        Dictionary<string, LearningResource> resources,
        PlanValidationResult result)
    {
        foreach (var activity in plan.Activities)
        {
            if (string.IsNullOrEmpty(activity.ResourceId)) continue;
            if (!resources.TryGetValue(activity.ResourceId, out var resource)) continue;

            // Invariant 7: Activity types must be compatible with resource capabilities
            switch (activity.ActivityType)
            {
                case "Reading":
                    if (string.IsNullOrWhiteSpace(resource.Transcript))
                    {
                        result.CriticalViolations.Add(
                            $"Incompatible activity: Reading requires transcript but " +
                            $"resource '{resource.Title}' has none");
                    }
                    break;

                case "VideoWatching":
                    if (resource.MediaType != "Video" || string.IsNullOrEmpty(resource.MediaUrl))
                    {
                        result.CriticalViolations.Add(
                            $"Incompatible activity: VideoWatching requires YouTube URL but " +
                            $"resource '{resource.Title}' has none");
                    }
                    break;

                case "Listening":
                    if (resource.MediaType != "Video" && resource.MediaType != "Podcast")
                    {
                        result.CriticalViolations.Add(
                            $"Incompatible activity: Listening requires audio but " +
                            $"resource '{resource.Title}' (type: {resource.MediaType}) has none");
                    }
                    break;
            }
        }
    }

    private void ValidateSessionBudget(PlanSkeleton plan, PlanValidationResult result)
    {
        // Invariant 8: Total minutes should not exceed session budget
        // (We compare against TotalMinutes which is set by the builder)
        var actualTotal = plan.Activities.Sum(a => a.EstimatedMinutes);
        if (actualTotal != plan.TotalMinutes)
        {
            result.Warnings.Add(
                $"TotalMinutes inconsistency: plan.TotalMinutes={plan.TotalMinutes} but " +
                $"sum of activities={actualTotal}");
        }
    }

    private void ValidateActivityVariety(
        PlanSkeleton plan,
        List<DailyPlanCompletion> recentCompletions,
        PlanValidationResult result)
    {
        // Warning 9: Activity types should not repeat from yesterday
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        var yesterdayTypes = recentCompletions
            .Where(c => c.Date.Date == yesterday)
            .Select(c => c.ActivityType)
            .ToHashSet();

        var repeatedTypes = plan.Activities
            .Where(a => yesterdayTypes.Contains(a.ActivityType))
            .Select(a => a.ActivityType)
            .Distinct()
            .ToList();

        if (repeatedTypes.Any())
        {
            result.Warnings.Add(
                $"Repeated activity types from yesterday: {string.Join(", ", repeatedTypes)}");
        }
    }

    private void ValidateResourceRecency(
        PlanSkeleton plan,
        List<DailyPlanCompletion> recentCompletions,
        PlanValidationResult result)
    {
        if (plan.PrimaryResource == null) return;

        // Warning 10: Same resource should not appear more than twice in recent 5-day window
        var fiveDaysAgo = DateTime.UtcNow.Date.AddDays(-5);
        var recentResourceUsage = recentCompletions
            .Where(c => c.Date.Date >= fiveDaysAgo && c.ResourceId == plan.PrimaryResource.Id)
            .Select(c => c.Date.Date)
            .Distinct()
            .Count();

        if (recentResourceUsage >= 2)
        {
            result.Warnings.Add(
                $"Resource overuse: '{plan.PrimaryResource.Title}' used {recentResourceUsage} " +
                $"times in last 5 days (max recommended: 2)");
        }
    }
}

public class PlanValidationResult
{
    public bool IsValid => !CriticalViolations.Any();
    public List<string> CriticalViolations { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
