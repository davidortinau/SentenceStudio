using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.Services.PlanGeneration;

/// <summary>
/// Builds daily study plans using deterministic algorithms and pedagogical rules.
/// Handles resource selection, vocabulary review decisions, and activity sequencing
/// without LLM assistance for speed, reliability, and pedagogical soundness.
/// </summary>
public class DeterministicPlanBuilder
{
    private readonly UserProfileRepository _userProfileRepo;
    private readonly LearningResourceRepository _resourceRepo;
    private readonly SkillProfileRepository _skillRepo;
    private readonly VocabularyProgressRepository _vocabProgressRepo;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DeterministicPlanBuilder> _logger;

    public DeterministicPlanBuilder(
        UserProfileRepository userProfileRepo,
        LearningResourceRepository resourceRepo,
        SkillProfileRepository skillRepo,
        VocabularyProgressRepository vocabProgressRepo,
        IServiceProvider serviceProvider,
        ILogger<DeterministicPlanBuilder> logger)
    {
        _userProfileRepo = userProfileRepo;
        _resourceRepo = resourceRepo;
        _skillRepo = skillRepo;
        _vocabProgressRepo = vocabProgressRepo;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Builds a complete study plan deterministically using pedagogical rules.
    /// </summary>
    public async Task<PlanSkeleton> BuildPlanAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("🎯 Starting deterministic plan generation");

        var userProfile = await _userProfileRepo.GetAsync();
        if (userProfile == null)
        {
            _logger.LogWarning("❌ No user profile found");
            return null;
        }

        var sessionMinutes = userProfile.PreferredSessionMinutes;
        var today = DateTime.UtcNow.Date;

        // Step 1: Determine vocabulary review needs (SRS algorithm)
        var vocabReview = await DetermineVocabularyReviewAsync(today, ct);

        if (vocabReview != null)
        {
            _logger.LogInformation("📚 Vocab review needed: {WordCount} words from resource {ResourceId} (~{Minutes}min)",
                vocabReview.WordCount, vocabReview.ResourceId, vocabReview.EstimatedMinutes);
        }

        // Step 2: Select primary resource (avoid recent, prefer vocab-rich)
        var primaryResource = await SelectPrimaryResourceAsync(today, vocabReview?.ResourceId, ct);

        if (primaryResource == null)
        {
            _logger.LogWarning("⚠️ No suitable resource found");
            // Fallback: vocab review only if available
            if (vocabReview != null)
            {
                return new PlanSkeleton
                {
                    Activities = new List<PlannedActivity>
                    {
                        new PlannedActivity
                        {
                            ActivityType = "VocabularyReview",
                            ResourceId = vocabReview.ResourceId,
                            SkillId = vocabReview.SkillId,
                            EstimatedMinutes = Math.Min(vocabReview.EstimatedMinutes, sessionMinutes),
                            Priority = 1
                        }
                    },
                    PrimaryResource = null,
                    VocabularyReview = vocabReview,
                    TotalMinutes = Math.Min(vocabReview.EstimatedMinutes, sessionMinutes)
                };
            }
            return null;
        }

        _logger.LogInformation("📖 Selected resource: {ResourceTitle} (ID: {ResourceId}, not used for {DaysSince} days)",
            primaryResource.Title, primaryResource.Id, primaryResource.DaysSinceLastUse);

        // Step 3: Determine skill level (use primary skill or default)
        var skill = await DetermineSkillAsync(primaryResource, ct);

        // Step 4: Build activity sequence (cognitive load progression)
        var activities = await BuildActivitySequenceAsync(
            primaryResource,
            skill,
            vocabReview,
            sessionMinutes,
            today,
            ct);

        var totalMinutes = activities.Sum(a => a.EstimatedMinutes);

        _logger.LogInformation("✅ Generated plan: {ActivityCount} activities, {TotalMinutes}min total",
            activities.Count, totalMinutes);

        return new PlanSkeleton
        {
            Activities = activities,
            PrimaryResource = primaryResource,
            PrimarySkill = skill,
            VocabularyReview = vocabReview,
            TotalMinutes = totalMinutes,
            ResourceSelectionReason = primaryResource.SelectionReason
        };
    }

    /// <summary>
    /// Determines if vocabulary review is needed and calculates scope.
    /// Uses SRS algorithm - purely deterministic.
    /// </summary>
    private async Task<VocabularyReviewBlock?> DetermineVocabularyReviewAsync(
        DateTime today,
        CancellationToken ct)
    {
        var dueWords = await _vocabProgressRepo.GetDueVocabularyAsync(today);

        if (dueWords.Count < 5)
        {
            _logger.LogDebug("⏭️ Skipping vocab review - only {Count} words due (need 5+)", dueWords.Count);
            return null;
        }

        // Cap at manageable amount (research shows 15-20 words optimal per session)
        var reviewCount = Math.Min(20, dueWords.Count);

        // Group by resource to find best vocabulary-resource match
        var wordsByResource = dueWords
            .SelectMany(w => w.LearningContexts
                .Where(lc => lc.LearningResourceId.HasValue)
                .Select(lc => new { Word = w, ResourceId = lc.LearningResourceId!.Value }))
            .GroupBy(x => x.ResourceId)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        int? resourceId = null;
        int? skillId = null;

        if (wordsByResource != null && wordsByResource.Count() >= 5)
        {
            // Found a resource with significant vocab due - use it for contextual learning!
            resourceId = wordsByResource.Key;

            // Get the skill associated with this resource (from recent activity or resource metadata)
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var recentSkill = await db.DailyPlanCompletions
                .Where(c => c.ResourceId == resourceId && c.SkillId.HasValue)
                .OrderByDescending(c => c.Date)
                .Select(c => c.SkillId)
                .FirstOrDefaultAsync(ct);

            skillId = recentSkill;

            _logger.LogInformation("🎯 Contextual vocab review: {Count}/{Total} words from resource {ResourceId}",
                wordsByResource.Count(), dueWords.Count, resourceId);
        }
        else
        {
            // No strong resource match - review from general pool
            _logger.LogInformation("📚 General vocab review: {ReviewCount}/{Total} words",
                reviewCount, dueWords.Count);
        }

        // Estimate time: ~3-4 words per minute with MC→text entry progression
        var estimatedMinutes = (int)Math.Ceiling(reviewCount / 3.5);

        return new VocabularyReviewBlock
        {
            WordCount = reviewCount,
            TotalDue = dueWords.Count,
            ResourceId = resourceId,
            SkillId = skillId,
            EstimatedMinutes = estimatedMinutes,
            IsContextual = resourceId.HasValue
        };
    }

    /// <summary>
    /// Selects primary resource using pedagogical rules:
    /// 1. NEVER use resource from yesterday
    /// 2. Strong preference for resources not used in 5 days
    /// 3. Prefer resources with vocabulary due (contextual learning)
    /// 4. Round-robin through all resources before repeating
    /// </summary>
    private async Task<SelectedResource?> SelectPrimaryResourceAsync(
        DateTime today,
        int? vocabResourceId,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Get recent activity to analyze resource usage patterns
        var recentActivity = await db.DailyPlanCompletions
            .Where(c => c.Date >= today.AddDays(-14) && c.ResourceId.HasValue)
            .OrderByDescending(c => c.Date)
            .ToListAsync(ct);

        var yesterday = today.AddDays(-1);
        var fiveDaysAgo = today.AddDays(-5);

        // Build resource usage map
        var resourceLastUsed = recentActivity
            .GroupBy(a => a.ResourceId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Max(a => a.Date)
            );

        // Get all available resources with vocabulary counts
        var resources = await _resourceRepo.GetAllResourcesLightweightAsync();

        var vocabularyCounts = await db.ResourceVocabularyMappings
            .GroupBy(rvm => rvm.ResourceId)
            .Select(g => new { ResourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ResourceId, x => x.Count, ct);

        // Filter and score resources
        var candidates = resources
            .Where(r => r.Id > 0 && !string.IsNullOrEmpty(r.Title))
            .Where(r => r.MediaType != "Other") // Skip test/invalid resources
            .Select(r => new ResourceCandidate
            {
                Resource = r,
                LastUsed = resourceLastUsed.TryGetValue(r.Id, out var date) ? date : (DateTime?)null,
                DaysSinceLastUse = resourceLastUsed.TryGetValue(r.Id, out var d)
                    ? (today - d).Days
                    : 999, // Never used = high score
                VocabCount = vocabularyCounts.TryGetValue(r.Id, out var count) ? count : 0,
                IsVocabResource = r.Id == vocabResourceId,
                Score = 0.0
            })
            .ToList();

        if (!candidates.Any())
        {
            _logger.LogWarning("❌ No valid resources available");
            return null;
        }

        // Apply scoring rules (pedagogically sound)
        foreach (var candidate in candidates)
        {
            double score = 0;

            // RULE 1: NEVER use yesterday's resource (hard constraint)
            if (candidate.LastUsed == yesterday)
            {
                score = -1000;
                continue;
            }

            // RULE 2: Strong bonus for resources not used in 5+ days
            if (candidate.DaysSinceLastUse >= 5)
                score += 100;
            else if (candidate.DaysSinceLastUse >= 2)
                score += 50;
            else
                score += candidate.DaysSinceLastUse * 10; // Linear scale for 0-1 days

            // RULE 3: Bonus for contextual learning (matches vocab due)
            if (candidate.IsVocabResource)
                score += 75;

            // RULE 4: Prefer resources with more vocabulary
            score += Math.Log(candidate.VocabCount + 1) * 5;

            // RULE 5: Prefer resources with audio (enables more activity types)
            if (candidate.Resource.MediaType == "Video" || candidate.Resource.MediaType == "Podcast")
                score += 20;

            candidate.Score = score;
        }

        // Select best candidate
        var selected = candidates
            .Where(c => c.Score > -500) // Filter out disqualified (yesterday's resource)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => Guid.NewGuid()) // Random tiebreaker
            .FirstOrDefault();

        if (selected == null)
        {
            _logger.LogWarning("⚠️ All resources disqualified (likely used yesterday)");
            return null;
        }

        var reason = selected.DaysSinceLastUse >= 5
            ? $"Fresh resource (not used for {selected.DaysSinceLastUse} days)"
            : selected.IsVocabResource
                ? $"Matches vocabulary due for review ({selected.VocabCount} words)"
                : selected.DaysSinceLastUse >= 2
                    ? $"Good variety (used {selected.DaysSinceLastUse} days ago)"
                    : "Best available option";

        return new SelectedResource
        {
            Id = selected.Resource.Id,
            Title = selected.Resource.Title,
            MediaType = selected.Resource.MediaType,
            Language = selected.Resource.Language,
            VocabularyCount = selected.VocabCount,
            DaysSinceLastUse = selected.DaysSinceLastUse,
            SelectionReason = reason,
            HasAudio = selected.Resource.MediaType == "Video" || selected.Resource.MediaType == "Podcast",
            HasTranscript = !string.IsNullOrWhiteSpace(selected.Resource.Transcript),
            YouTubeUrl = selected.Resource.MediaType == "Video" && !string.IsNullOrEmpty(selected.Resource.MediaUrl)
                ? selected.Resource.MediaUrl
                : null
        };
    }

    /// <summary>
    /// Determines appropriate skill level for the session.
    /// Uses recent activity patterns or resource metadata.
    /// </summary>
    private async Task<SkillInfo?> DetermineSkillAsync(
        SelectedResource resource,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Try to find skill from recent activity with this resource
        var recentSkill = await db.DailyPlanCompletions
            .Where(c => c.ResourceId == resource.Id && c.SkillId.HasValue)
            .OrderByDescending(c => c.Date)
            .Select(c => c.SkillId!.Value)
            .FirstOrDefaultAsync(ct);

        if (recentSkill > 0)
        {
            var skill = await _skillRepo.GetAsync(recentSkill);
            if (skill != null)
            {
                return new SkillInfo
                {
                    Id = skill.Id,
                    Title = skill.Title,
                    Description = skill.Description
                };
            }
        }

        // Fallback: Use most recently practiced skill overall
        var fallbackSkill = await db.DailyPlanCompletions
            .Where(c => c.SkillId.HasValue)
            .OrderByDescending(c => c.Date)
            .Select(c => c.SkillId!.Value)
            .FirstOrDefaultAsync(ct);

        if (fallbackSkill > 0)
        {
            var skill = await _skillRepo.GetAsync(fallbackSkill);
            if (skill != null)
            {
                return new SkillInfo
                {
                    Id = skill.Id,
                    Title = skill.Title,
                    Description = skill.Description
                };
            }
        }

        // Last resort: Get any available skill
        var skills = await _skillRepo.ListAsync();
        var anySkill = skills.FirstOrDefault();

        if (anySkill != null)
        {
            return new SkillInfo
            {
                Id = anySkill.Id,
                Title = anySkill.Title,
                Description = anySkill.Description
            };
        }

        _logger.LogWarning("⚠️ No skills available");
        return null;
    }

    /// <summary>
    /// Builds activity sequence using cognitive load theory and SLA principles:
    /// 1. Vocab review first (consolidation)
    /// 2. Input before output (Krashen's Input Hypothesis)
    /// 3. Progressive cognitive load (easy → hard)
    /// 4. Variety in activity types
    /// </summary>
    private async Task<List<PlannedActivity>> BuildActivitySequenceAsync(
        SelectedResource resource,
        SkillInfo skill,
        VocabularyReviewBlock? vocabReview,
        int sessionMinutes,
        DateTime today,
        CancellationToken ct)
    {
        var activities = new List<PlannedActivity>();
        var remainingMinutes = sessionMinutes;
        var priority = 1;

        // Get recent activity types to ensure variety
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var recentActivityTypes = await db.DailyPlanCompletions
            .Where(c => c.Date >= today.AddDays(-3))
            .Select(c => c.ActivityType)
            .ToListAsync(ct);

        var yesterdayActivityList = await db.DailyPlanCompletions
            .Where(c => c.Date == today.AddDays(-1))
            .Select(c => c.ActivityType)
            .ToListAsync(ct);
        var yesterdayActivities = yesterdayActivityList.ToHashSet();

        // STEP 1: Vocab review (if needed)
        if (vocabReview != null && remainingMinutes >= 5)
        {
            var vocabMinutes = Math.Min(vocabReview.EstimatedMinutes, remainingMinutes);
            activities.Add(new PlannedActivity
            {
                ActivityType = "VocabularyReview",
                ResourceId = vocabReview.ResourceId ?? resource.Id,
                SkillId = vocabReview.SkillId ?? skill?.Id,
                EstimatedMinutes = vocabMinutes,
                Priority = priority++,
                Rationale = vocabReview.IsContextual
                    ? $"Review {vocabReview.WordCount} words from this resource (contextual learning)"
                    : $"Review {vocabReview.WordCount} of {vocabReview.TotalDue} words due today"
            });
            remainingMinutes -= vocabMinutes;
        }

        // STEP 2: Input activity (lower cognitive load, comprehensible input)
        if (remainingMinutes >= 8)
        {
            var inputActivity = SelectInputActivity(resource, yesterdayActivities, recentActivityTypes);
            
            // Only add input activity if resource supports at least one (has transcript, audio, or video)
            if (!string.IsNullOrEmpty(inputActivity))
            {
                var inputMinutes = Math.Min(10, remainingMinutes);

                activities.Add(new PlannedActivity
                {
                    ActivityType = inputActivity,
                    ResourceId = resource.Id,
                    SkillId = skill?.Id,
                    EstimatedMinutes = inputMinutes,
                    Priority = priority++,
                    Rationale = GetActivityRationale(inputActivity, "input")
                });
                remainingMinutes -= inputMinutes;
            }
            else
            {
                _logger.LogWarning("⚠️ Resource {ResourceId} has no compatible input activities (no transcript/audio/video)", resource.Id);
            }
        }

        // STEP 3: Output activity (higher cognitive load, production practice)
        if (remainingMinutes >= 8)
        {
            var outputActivity = SelectOutputActivity(resource, yesterdayActivities, recentActivityTypes);
            var outputMinutes = Math.Min(10, remainingMinutes);

            activities.Add(new PlannedActivity
            {
                ActivityType = outputActivity,
                ResourceId = resource.Id,
                SkillId = skill?.Id,
                EstimatedMinutes = outputMinutes,
                Priority = priority++,
                Rationale = GetActivityRationale(outputActivity, "output")
            });
            remainingMinutes -= outputMinutes;
        }

        // STEP 4: Light closer (if time remains)
        if (remainingMinutes >= 5 && skill != null)
        {
            activities.Add(new PlannedActivity
            {
                ActivityType = "VocabularyGame",
                ResourceId = null, // Vocabulary games use skill context
                SkillId = skill.Id,
                EstimatedMinutes = Math.Min(8, remainingMinutes),
                Priority = priority++,
                Rationale = "Light game activity to reinforce vocabulary in a low-pressure way"
            });
        }

        return activities;
    }

    private string SelectInputActivity(
        SelectedResource resource,
        HashSet<string> yesterdayActivities,
        List<string> recentActivities)
    {
        var inputActivities = new List<string>();

        // Add appropriate input activities based on resource capabilities
        // VideoWatching REQUIRES a YouTube URL
        if (resource.HasAudio && !string.IsNullOrEmpty(resource.YouTubeUrl))
        {
            inputActivities.Add("VideoWatching");
        }
        
        // Listening requires audio (video or podcast)
        if (resource.HasAudio)
        {
            inputActivities.Add("Listening");
        }

        // Reading REQUIRES a transcript - do NOT add if resource has no transcript!
        if (resource.HasTranscript)
        {
            inputActivities.Add("Reading");
        }

        // If no input activities are compatible with this resource, return null
        if (!inputActivities.Any())
        {
            return null;
        }

        // Filter out yesterday's activities
        var fresh = inputActivities.Where(a => !yesterdayActivities.Contains(a)).ToList();
        if (fresh.Any())
            inputActivities = fresh;

        // Prefer least recently used
        var selected = inputActivities
            .OrderBy(a => recentActivities.Count(r => r == a))
            .ThenBy(a => Guid.NewGuid())
            .First();

        return selected;
    }

    private string SelectOutputActivity(
        SelectedResource resource,
        HashSet<string> yesterdayActivities,
        List<string> recentActivities)
    {
        var outputActivities = new List<string> { "Translation", "Cloze", "Writing" };

        // Add shadowing if resource has audio (highly effective for pronunciation)
        if (resource.HasAudio)
            outputActivities.Add("Shadowing");

        // Filter out yesterday's activities
        var fresh = outputActivities.Where(a => !yesterdayActivities.Contains(a)).ToList();
        if (fresh.Any())
            outputActivities = fresh;

        // Prefer least recently used
        var selected = outputActivities
            .OrderBy(a => recentActivities.Count(r => r == a))
            .ThenBy(a => Guid.NewGuid())
            .First();

        return selected;
    }

    private string GetActivityRationale(string activityType, string category)
    {
        return activityType switch
        {
            "Reading" => "Build reading comprehension and vocabulary recognition",
            "Listening" => "Practice listening comprehension with transcript support",
            "VideoWatching" => "Authentic content with visual context for deeper understanding",
            "Shadowing" => "Highly effective for pronunciation and speaking fluency",
            "Translation" => "Deep comprehension practice with active recall",
            "Cloze" => "Grammar and vocabulary recall in context",
            "Writing" => "Creative sentence construction for active vocabulary use",
            _ => $"Practice {category} skills"
        };
    }
}

/// <summary>
/// Represents a vocabulary review block with SRS-scheduled words
/// </summary>
public class VocabularyReviewBlock
{
    public int WordCount { get; set; }
    public int TotalDue { get; set; }
    public int? ResourceId { get; set; }
    public int? SkillId { get; set; }
    public int EstimatedMinutes { get; set; }
    public bool IsContextual { get; set; }
}

/// <summary>
/// Represents a selected primary resource with selection metadata
/// </summary>
public class SelectedResource
{
    public int Id { get; set; }
    public string Title { get; set; }
    public string MediaType { get; set; }
    public string Language { get; set; }
    public int VocabularyCount { get; set; }
    public int DaysSinceLastUse { get; set; }
    public string SelectionReason { get; set; }
    public bool HasAudio { get; set; }
    public bool HasTranscript { get; set; }
    public string? YouTubeUrl { get; set; }
}

/// <summary>
/// Represents skill information for the session
/// </summary>
public class SkillInfo
{
    public int Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
}

/// <summary>
/// Represents the complete deterministic plan structure
/// </summary>
public class PlanSkeleton
{
    public List<PlannedActivity> Activities { get; set; } = new();
    public SelectedResource? PrimaryResource { get; set; }
    public SkillInfo? PrimarySkill { get; set; }
    public VocabularyReviewBlock? VocabularyReview { get; set; }
    public int TotalMinutes { get; set; }
    public string ResourceSelectionReason { get; set; }
}

/// <summary>
/// Represents a single planned activity with metadata
/// </summary>
public class PlannedActivity
{
    public string ActivityType { get; set; }
    public int? ResourceId { get; set; }
    public int? SkillId { get; set; }
    public int EstimatedMinutes { get; set; }
    public int Priority { get; set; }
    public string Rationale { get; set; }
}

/// <summary>
/// Internal class for resource candidate scoring
/// </summary>
internal class ResourceCandidate
{
    public LearningResource Resource { get; set; }
    public DateTime? LastUsed { get; set; }
    public int DaysSinceLastUse { get; set; }
    public int VocabCount { get; set; }
    public bool IsVocabResource { get; set; }
    public double Score { get; set; }
}
