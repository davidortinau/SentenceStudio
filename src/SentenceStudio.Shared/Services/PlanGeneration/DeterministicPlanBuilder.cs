using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models.DailyPlanGeneration;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Services.PlanGeneration;

/// <summary>
/// Builds daily study plans using deterministic algorithms and pedagogical rules.
/// Handles resource selection, vocabulary review decisions, and activity sequencing
/// without LLM assistance for speed, reliability, and pedagogical soundness.
/// </summary>
public class DeterministicPlanBuilder
{
    private const int MinimumDueVocabularyWords = 5;
    private const int MaxDueVocabularyWords = 20;
    private const int BootstrapVocabularyTarget = 15;
    private const int ResourceFreshnessLookbackDays = 30;

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
    /// <param name="userProfileId">
    /// Explicit user scope. When supplied (always on the multi-user HTTP API),
    /// the builder resolves <see cref="UserProfile"/> by id and threads the
    /// scope through every downstream query so users never see each other's
    /// resources, skills, vocab progress, or completion history. When
    /// <c>null</c>/empty (single-user MAUI mobile) the builder falls back to
    /// the legacy <c>UserProfileRepository.GetAsync()</c> path that reads
    /// the active profile id from <c>IPreferencesService</c>.
    /// </param>
    public async Task<PlanSkeleton?> BuildPlanAsync(string? userProfileId = null, CancellationToken ct = default)
    {
        _logger.LogInformation("🎯 Starting deterministic plan generation (userProfileId='{UserProfileId}')",
            userProfileId);

        UserProfile? userProfile = !string.IsNullOrEmpty(userProfileId)
            ? await _userProfileRepo.GetByIdAsync(userProfileId!, ct)
            : await _userProfileRepo.GetAsync();
        if (userProfile == null)
        {
            _logger.LogWarning("❌ No user profile found (userProfileId='{UserProfileId}')", userProfileId);
            return null;
        }

        // GetByIdAsync skips the side effects that GetAsync() runs on the
        // legacy path (smart-resource seeding). Mirror that here so the
        // API-host plan generation has the same Daily Review / New Words /
        // Phrases smart resources available as a freshly-onboarded mobile
        // user. EnsureSmartResourcesAsync is per-profile idempotent and
        // already swallows failures with a warning.
        if (!string.IsNullOrEmpty(userProfileId))
        {
            await _userProfileRepo.EnsureSmartResourcesAsync(userProfile);
        }

        // Use the resolved profile id as the SOLE authority for user scoping
        // downstream. This keeps both code paths (explicit userProfileId on
        // the API, IPreferences fallback on mobile) on the same internal
        // contract: every db/repo call below is filtered to this user.
        var scopedUserId = userProfile.Id;

        var sessionMinutes = userProfile.PreferredSessionMinutes;
        // Resolve the per-request/per-call IPlanDateContext through the service
        // provider so this Singleton builder doesn't capture a stale date
        // context across DST shifts or device-local timezone changes. The user
        // local date is the SOLE authority for "today" — see
        // PlanDateContextBannedSymbolsTests for the build-time guard.
        //
        // Kind=Utc preserves the prior behavior of the legacy code path that
        // used the system clock's UTC date directly. The tiebreaker hash
        // (today.GetHashCode) and the EF SQLite TEXT storage format stay
        // byte-identical to what's already on disk. Don't switch to
        // Unspecified without a data + tiebreaker audit.
        var dateContext = _serviceProvider.GetRequiredService<IPlanDateContext>();
        var userLocalDate = dateContext.UserLocalDate;
        var today = userLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Step 1: Determine vocabulary review needs (SRS algorithm)
        var vocabReview = await DetermineVocabularyReviewAsync(today, scopedUserId, ct);

        if (vocabReview != null)
        {
            _logger.LogInformation("📚 Vocab review needed: {WordCount} words from resource {ResourceId} (~{Minutes}min)",
                vocabReview.WordCount, vocabReview.ResourceId, vocabReview.EstimatedMinutes);
        }

        // Step 2: Select primary resource (avoid recent, prefer vocab-rich)
        var primaryResource = await SelectPrimaryResourceAsync(today, vocabReview?.ResourceId, scopedUserId, ct);

        if (primaryResource == null)
        {
            _logger.LogWarning("⚠️ No suitable resource found");
            // Fallback: vocab review only if available
            if (vocabReview != null)
            {
                var fallbackActivities = new List<PlannedActivity>
                {
                    new PlannedActivity
                    {
                        ActivityType = "VocabularyReview",
                        ResourceId = null,  // VocabularyReview is vocabulary-driven, NOT resource-scoped
                        SkillId = vocabReview.SkillId,
                        EstimatedMinutes = Math.Min(vocabReview.EstimatedMinutes, sessionMinutes),
                        Priority = 1,
                        Rationale = GetVocabularyReviewRationale(vocabReview),
                        FocusVocabularyIds = vocabReview.FocusVocabularyIds
                    }
                };

                // Build narrative for vocab-only plan
                var fallbackNarrative = BuildNarrative(null, vocabReview, fallbackActivities);

                return new PlanSkeleton
                {
                    Activities = fallbackActivities,
                    PrimaryResource = null,
                    VocabularyReview = vocabReview,
                    TotalMinutes = Math.Min(vocabReview.EstimatedMinutes, sessionMinutes),
                    Narrative = fallbackNarrative,
                    FocusVocabularyIds = vocabReview.FocusVocabularyIds
                };
            }
            return null;
        }

        var lastUseDescription = primaryResource.DaysSinceLastUse.HasValue
            ? $"not used for {primaryResource.DaysSinceLastUse.Value} days"
            : "never used before";
        _logger.LogInformation("📖 Selected resource: {ResourceTitle} (ID: {ResourceId}, {LastUseDescription})",
            primaryResource.Title, primaryResource.Id, lastUseDescription);

        // Step 3: Determine skill level (use primary skill or default)
        var skill = await DetermineSkillAsync(primaryResource, scopedUserId, ct);

        // Step 4: Build activity sequence (cognitive load progression)
        var activities = await BuildActivitySequenceAsync(
            primaryResource,
            skill,
            vocabReview,
            sessionMinutes,
            today,
            scopedUserId,
            ct);

        var totalMinutes = activities.Sum(a => a.EstimatedMinutes);

        _logger.LogInformation("✅ Generated plan: {ActivityCount} activities, {TotalMinutes}min total",
            activities.Count, totalMinutes);

        // Build narrative for the plan
        var narrative = BuildNarrative(primaryResource, vocabReview, activities);

        return new PlanSkeleton
        {
            Activities = activities,
            PrimaryResource = primaryResource,
            PrimarySkill = skill,
            VocabularyReview = vocabReview,
            TotalMinutes = totalMinutes,
            ResourceSelectionReason = primaryResource.SelectionReason,
            Narrative = narrative,
            FocusVocabularyIds = vocabReview?.FocusVocabularyIds ?? new List<string>()
        };
    }

    /// <summary>
    /// Determines if vocabulary review is needed and calculates scope.
    /// Uses SRS algorithm - purely deterministic.
    /// </summary>
    private async Task<VocabularyReviewBlock?> DetermineVocabularyReviewAsync(
        DateTime today,
        string userProfileId,
        CancellationToken ct)
    {
        var dueWords = await _vocabProgressRepo.GetDueVocabularyAsync(today, userProfileId);
        var useBootstrap = false;
        var unseenWords = new List<VocabularyProgress>();

        if (dueWords.Count < MinimumDueVocabularyWords)
        {
            var unseenLimit = Math.Max(0, BootstrapVocabularyTarget - dueWords.Count);
            unseenWords = await _vocabProgressRepo.GetUnseenMappedVocabularyAsync(userProfileId, unseenLimit, ct);

            if (unseenWords.Count == 0)
            {
                _logger.LogDebug("⏭️ Skipping vocab review - only {Count} words due (need {Minimum}+), and no unseen mapped words available",
                    dueWords.Count, MinimumDueVocabularyWords);
                return null;
            }

            useBootstrap = true;
            _logger.LogInformation("📚 Bootstrapping vocab review: {DueCount} due + {UnseenCount} unseen mapped words",
                dueWords.Count, unseenWords.Count);
        }

        // Cap at manageable amount (research shows 15-20 words optimal per session).
        // Bootstrap sessions are capped lower so first-time learners see enough
        // vocabulary to start learning without being overwhelmed.
        var reviewCount = useBootstrap
            ? Math.Min(BootstrapVocabularyTarget, dueWords.Count + unseenWords.Count)
            : Math.Min(MaxDueVocabularyWords, dueWords.Count);

        var selectedDueWords = dueWords
            .OrderBy(w => w.NextReviewDate)
            .ThenBy(w => w.VocabularyWordId, StringComparer.Ordinal)
            .Take(Math.Min(dueWords.Count, reviewCount))
            .ToList();

        var remainingBootstrapSlots = Math.Max(0, reviewCount - selectedDueWords.Count);
        var selectedUnseenWords = unseenWords
            .OrderBy(w => w.VocabularyWordId, StringComparer.Ordinal)
            .Take(remainingBootstrapSlots)
            .ToList();

        var selectedWords = selectedDueWords
            .Concat(selectedUnseenWords)
            .ToList();

        // Group by resource to find best vocabulary-resource match
        var wordsByResource = selectedWords
            .SelectMany(w => w.LearningContexts
                .Where(lc => !string.IsNullOrEmpty(lc.LearningResourceId))
                .Select(lc => new { Word = w, ResourceId = lc.LearningResourceId! }))
            .GroupBy(x => x.ResourceId)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        string? resourceId = null;
        string? skillId = null;

        if (wordsByResource != null && wordsByResource.Count() >= MinimumDueVocabularyWords)
        {
            // Found a resource with significant vocab due - use it for contextual learning!
            resourceId = wordsByResource.Key;

            // Get the skill associated with this resource (from recent activity or resource metadata)
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var recentSkill = await db.DailyPlanCompletions
                .Where(c => c.UserProfileId == userProfileId
                    && c.ResourceId == resourceId
                    && !string.IsNullOrEmpty(c.SkillId))
                .OrderByDescending(c => c.Date)
                .Select(c => c.SkillId)
                .FirstOrDefaultAsync(ct);

            skillId = recentSkill;

            _logger.LogInformation("🎯 Contextual vocab review: {Count}/{Total} words from resource {ResourceId}",
                wordsByResource.Count(), selectedWords.Count, resourceId);
        }
        else
        {
            // No strong resource match - review from general pool
            _logger.LogInformation("📚 General vocab review: {ReviewCount}/{Total} words",
                reviewCount, selectedWords.Count);
        }

        // Estimate time: ~3-4 words per minute with MC→text entry progression.
        // Bootstrap plans must reserve a meaningful activity slot even for a tiny first batch.
        var estimatedMinutes = (int)Math.Ceiling(reviewCount / 3.5);
        if (useBootstrap)
            estimatedMinutes = Math.Max(5, estimatedMinutes);

        var focusVocabularyIds = selectedWords
            .Select(w => w.VocabularyWordId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new VocabularyReviewBlock
        {
            WordCount = selectedWords.Count,
            TotalDue = useBootstrap ? selectedWords.Count : dueWords.Count,
            ResourceId = resourceId,
            SkillId = skillId,
            EstimatedMinutes = estimatedMinutes,
            IsContextual = !string.IsNullOrEmpty(resourceId),
            DueWords = selectedWords,
            FocusVocabularyIds = focusVocabularyIds,
            UnseenWordCount = selectedUnseenWords.Count,
            IsBootstrap = useBootstrap
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
        string? vocabResourceId,
        string userProfileId,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Get all activity for accurate last-used display, then keep the
        // 30-day window only for freshness/rotation scoring.
        var resourceActivity = await db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userProfileId
                && !string.IsNullOrEmpty(c.ResourceId))
            .OrderByDescending(c => c.Date)
            .ToListAsync(ct);
        var recentActivity = resourceActivity
            .Where(c => c.Date >= today.AddDays(-ResourceFreshnessLookbackDays))
            .ToList();

        var yesterday = today.AddDays(-1);

        // Build resource usage map
        var resourceLastUsed = resourceActivity
            .GroupBy(a => a.ResourceId!)
            .ToDictionary(
                g => g.Key,
                g => g.Max(a => a.Date.Date)
            );
        var resourceRecentUsageDays = recentActivity
            .Where(a => a.Date >= today.AddDays(-7))
            .GroupBy(a => a.ResourceId!)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.Date.Date).Distinct().Count());

        // Get all available resources with vocabulary counts (user-scoped)
        var resources = await _resourceRepo.GetAllResourcesLightweightAsync(userProfileId: userProfileId);

        var vocabularyCounts = await db.ResourceVocabularyMappings
            .GroupBy(rvm => rvm.ResourceId)
            .Select(g => new { ResourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ResourceId, x => x.Count, ct);

        // Filter and score resources
        var candidates = resources
            .Where(r => !string.IsNullOrEmpty(r.Id) && !string.IsNullOrEmpty(r.Title))
            .Where(r => r.MediaType != "Other") // Skip test/invalid resources
            .Where(r => !r.IsSmartResource) // Exclude smart resources (intent-driven, not auto-selected)
            .Select(r =>
            {
                var lastUsed = resourceLastUsed.TryGetValue(r.Id, out var date) ? date : (DateTime?)null;
                return new ResourceCandidate
                {
                    Resource = r,
                    LastUsed = lastUsed,
                    DaysSinceLastUse = lastUsed.HasValue ? Math.Max(0, (today - lastUsed.Value.Date).Days) : null,
                    VocabCount = vocabularyCounts.TryGetValue(r.Id, out var count) ? count : 0,
                    IsVocabResource = r.Id == vocabResourceId,
                    Score = 0.0
                };
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

            // RULE 2: Strong bonus for resources not used in 5+ days.
            // Truly never-used resources get the same freshness bonus without
            // pretending they were used 999 days ago.
            if (!candidate.DaysSinceLastUse.HasValue)
                score += 100;
            else if (candidate.DaysSinceLastUse.Value >= 5)
                score += 100;
            else if (candidate.DaysSinceLastUse.Value >= 2)
                score += 50;
            else
                score += candidate.DaysSinceLastUse.Value * 10; // Linear scale for 0-1 days

            // RULE 3: Bonus for contextual learning (matches vocab due)
            if (candidate.IsVocabResource)
                score += 75;

            // RULE 4: Prefer resources with more vocabulary
            score += Math.Log(candidate.VocabCount + 1) * 5;

            // RULE 5: Prefer resources with audio (enables more activity types)
            if (candidate.Resource.MediaType == "Video" || candidate.Resource.MediaType == "Podcast")
                score += 20;

            // RULE 6: Penalize frequent reuse across the last week so we rotate
            // through resources more aggressively (even when several are eligible).
            var recentUsageDays = resourceRecentUsageDays.TryGetValue(candidate.Resource.Id, out var usageDays)
                ? usageDays
                : 0;
            score -= recentUsageDays * 18;
            if (recentUsageDays >= 3)
                score -= 25;

            candidate.Score = score;
        }

        // Select best candidate
        var selected = candidates
            .Where(c => c.Score > -500) // Filter out disqualified (yesterday's resource)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => DeterministicHash.Combine(c.Resource.Id, today)) // Deterministic tiebreaker (process-stable)
            .FirstOrDefault();

        if (selected == null)
        {
            _logger.LogWarning("⚠️ All resources disqualified (likely used yesterday)");
            return null;
        }

        var reason = !selected.DaysSinceLastUse.HasValue
            ? "New resource for today's plan"
            : selected.DaysSinceLastUse.Value > ResourceFreshnessLookbackDays
                ? "Resource not used recently"
                : selected.DaysSinceLastUse.Value >= 1
                    ? $"Last used {selected.DaysSinceLastUse.Value} days ago"
                    : selected.IsVocabResource
                        ? $"Matches vocabulary due for review ({selected.VocabCount} words)"
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
        string userProfileId,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Try to find skill from recent activity with this resource
        var recentSkill = await db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userProfileId
                && c.ResourceId == resource.Id
                && !string.IsNullOrEmpty(c.SkillId))
            .OrderByDescending(c => c.Date)
            .Select(c => c.SkillId!)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(recentSkill))
        {
            var skill = await _skillRepo.GetAsync(recentSkill, userProfileId);
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

        // Fallback: Use most recently practiced skill overall (still user-scoped)
        var fallbackSkill = await db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userProfileId
                && !string.IsNullOrEmpty(c.SkillId))
            .OrderByDescending(c => c.Date)
            .Select(c => c.SkillId!)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(fallbackSkill))
        {
            var skill = await _skillRepo.GetAsync(fallbackSkill, userProfileId);
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

        // Last resort: Get any available skill (user-scoped)
        var skills = await _skillRepo.ListAsync(userProfileId);
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
        string userProfileId,
        CancellationToken ct)
    {
        var activities = new List<PlannedActivity>();
        var remainingMinutes = sessionMinutes;
        var priority = 1;
        var focusVocabularyIds = vocabReview?.FocusVocabularyIds ?? new List<string>();

        // Get recent activity types to ensure variety
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Exclude today's completions so that completing an activity doesn't change what gets selected on regeneration
        var recentActivityTypes = await db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userProfileId
                && c.Date >= today.AddDays(-3) && c.Date < today)
            .Select(c => c.ActivityType)
            .ToListAsync(ct);

        var yesterdayActivityList = await db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userProfileId
                && c.Date == today.AddDays(-1))
            .Select(c => c.ActivityType)
            .ToListAsync(ct);
        var yesterdayActivities = yesterdayActivityList.ToHashSet();

        // STEP 1: Vocab review (if needed)
        if (vocabReview != null && remainingMinutes > 0)
        {
            var vocabMinutes = Math.Min(vocabReview.EstimatedMinutes, remainingMinutes);
            activities.Add(new PlannedActivity
            {
                ActivityType = "VocabularyReview",
                ResourceId = null,  // VocabularyReview is vocabulary-driven, NOT resource-scoped
                SkillId = vocabReview.SkillId ?? skill?.Id,
                EstimatedMinutes = vocabMinutes,
                Priority = priority++,
                Rationale = GetVocabularyReviewRationale(vocabReview),
                FocusVocabularyIds = focusVocabularyIds
            });
            remainingMinutes -= vocabMinutes;
        }

        // STEP 2: Input activity (lower cognitive load, comprehensible input)
        if (remainingMinutes >= 8)
        {
            var inputActivity = SelectInputActivity(resource, yesterdayActivities, recentActivityTypes, today);
            
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
                    Rationale = GetActivityRationale(inputActivity, "input"),
                    FocusVocabularyIds = GetFocusVocabularyIdsForActivity(inputActivity, focusVocabularyIds)
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
            var outputActivity = SelectOutputActivity(resource, yesterdayActivities, recentActivityTypes, today);
            var outputMinutes = Math.Min(10, remainingMinutes);

            activities.Add(new PlannedActivity
            {
                ActivityType = outputActivity,
                ResourceId = outputActivity == "Cloze" ? null : resource.Id,  // Cloze is vocabulary-driven when from plan
                SkillId = skill?.Id,
                EstimatedMinutes = outputMinutes,
                Priority = priority++,
                Rationale = GetActivityRationale(outputActivity, "output"),
                FocusVocabularyIds = GetFocusVocabularyIdsForActivity(outputActivity, focusVocabularyIds)
            });
            remainingMinutes -= outputMinutes;
        }

        // STEP 4: Light closer (if time remains)
        if (remainingMinutes >= 5)
        {
            var closerActivity = await SelectCloserActivityAsync(skill, userProfileId, today, ct);
            
            if (closerActivity != null)
            {
                activities.Add(new PlannedActivity
                {
                    ActivityType = closerActivity,
                    ResourceId = null,  // Layer 1: Both NumberDrill and VocabularyGame are vocabulary-driven
                    SkillId = closerActivity == "VocabularyGame" ? skill?.Id : null,
                    EstimatedMinutes = Math.Min(8, remainingMinutes),
                    Priority = priority++,
                    Rationale = closerActivity == "NumberDrill"
                        ? "Number drill to build automaticity with Korean number systems"
                        : "Light game activity to reinforce vocabulary in a low-pressure way",
                    FocusVocabularyIds = GetFocusVocabularyIdsForActivity(closerActivity, focusVocabularyIds)
                });
            }
        }

        return activities;
    }

    private static string GetVocabularyReviewRationale(VocabularyReviewBlock vocabReview)
    {
        if (!vocabReview.IsBootstrap)
            return $"Review {vocabReview.WordCount} of {vocabReview.TotalDue} words due today";

        var dueCount = vocabReview.WordCount - vocabReview.UnseenWordCount;
        if (dueCount <= 0)
            return $"Learn {vocabReview.UnseenWordCount} new vocabulary words";

        return $"Review {dueCount} due words and learn {vocabReview.UnseenWordCount} new vocabulary words";
    }

    private static List<string> GetFocusVocabularyIdsForActivity(string activityType, IReadOnlyList<string> focusVocabularyIds)
    {
        return activityType is "VocabularyReview" or "VocabularyGame" or "Cloze" or "Writing" or "Translation" or "Reading"
            ? focusVocabularyIds.ToList()
            : new List<string>();
    }

    /// <summary>
    /// Selects the closer activity for STEP 4. Returns "NumberDrill" when numbers are due,
    /// "VocabularyGame" when a skill is available, or null if no closer is appropriate.
    /// </summary>
    private async Task<string?> SelectCloserActivityAsync(
        SkillInfo? skill,
        string userProfileId,
        DateTime today,
        CancellationToken ct)
    {
        // Check if NumberDrill is due (any NumberMasteryProgress row with DueDate <= tomorrow)
        // Resolve scoped ApplicationDbContext from a service scope (this class is registered Singleton).
        var tomorrow = today.AddDays(1);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var numbersDue = await db.NumberMasteryProgresses
            .AnyAsync(p =>
                p.UserProfileId == userProfileId
                && p.DueDate <= tomorrow, ct);
        
        if (numbersDue)
        {
            _logger.LogInformation("Numbers are due — selecting NumberDrill for STEP 4 closer: userProfileId={UserProfileId}",
                userProfileId);
            return "NumberDrill";
        }
        
        // Fallback: VocabularyGame if skill exists
        if (skill != null)
        {
            _logger.LogInformation("No numbers due — selecting VocabularyGame for STEP 4 closer: userProfileId={UserProfileId}",
                userProfileId);
            return "VocabularyGame";
        }
        
        _logger.LogInformation("No skill and no numbers due — skipping STEP 4 closer: userProfileId={UserProfileId}",
            userProfileId);
        return null;
    }

    private PlanNarrative BuildNarrative(
        SelectedResource? primaryResource,
        VocabularyReviewBlock? vocabReview,
        List<PlannedActivity> activities)
    {
        var resources = new List<PlanResourceSummary>();
        VocabInsight? vocabInsight = null;
        var focusAreas = new List<string>();
        var storyParts = new List<string>();
        var totalActivities = activities.Count;
        var totalMinutes = activities.Sum(a => a.EstimatedMinutes);
        var resourceLinkedActivities = activities.Where(a => !string.IsNullOrWhiteSpace(a.ResourceId)).ToList();
        var resourceLinkedCount = resourceLinkedActivities.Count;
        var resourceLinkedMinutes = resourceLinkedActivities.Sum(a => a.EstimatedMinutes);

        // 1. Resource summary
        if (primaryResource != null && resourceLinkedCount > 0)
        {
            var resourceCoverageReason = resourceLinkedCount == totalActivities
                ? $"{primaryResource.SelectionReason}. Used across the full plan."
                : $"{primaryResource.SelectionReason}. Used for {resourceLinkedCount} of {totalActivities} activities ({resourceLinkedMinutes} of {totalMinutes} minutes).";

            resources.Add(new PlanResourceSummary(
                primaryResource.Id,
                primaryResource.Title,
                primaryResource.MediaType,
                resourceCoverageReason));
        }

        if (primaryResource != null)
        {
            if (resourceLinkedCount == 0)
            {
                storyParts.Add("Today's plan is focused on vocabulary and skill drills; no specific learning resource is required");
            }
            else if (resourceLinkedCount == totalActivities)
            {
                storyParts.Add($"Today's plan is built around \"{primaryResource.Title}\" across all {totalActivities} activities");
            }
            else
            {
                storyParts.Add($"Today's plan mixes vocabulary-focused work with contextual practice from \"{primaryResource.Title}\" ({resourceLinkedCount} of {totalActivities} activities)");
            }
        }

        // 2. Vocab insight analysis
        if (vocabReview != null && vocabReview.DueWords.Any())
        {
            var dueWords = vocabReview.DueWords;
            var focusSet = dueWords;
            var newWords = focusSet.Where(w => w.TotalAttempts == 0).ToList();
            var reviewWords = focusSet.Where(w => w.TotalAttempts > 0).ToList();
            var avgMastery = reviewWords.Any() ? reviewWords.Average(w => w.MasteryScore) : 0f;

            // Analyze categories from Tags — separate untested from genuinely struggling
            var allTagInsights = dueWords
                .Where(w => w.VocabularyWord?.Tags != null)
                .SelectMany(w => w.VocabularyWord.Tags
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(tag => new { Tag = tag, Progress = w }))
                .GroupBy(x => x.Tag)
                .Where(g => g.Count() >= 2) // Only show categories with 2+ words
                .Select(g => new TagInsight(
                    g.Key,
                    g.Count(),
                    g.Average(x => x.Progress.Accuracy),
                    g.Sum(x => x.Progress.TotalAttempts)))
                .ToList();

            // Struggling = attempted but low accuracy; Untested = never attempted
            var strugglingTags = allTagInsights
                .Where(t => t.TotalAttempts > 0 && t.AverageAccuracy < 0.6f)
                .OrderBy(t => t.AverageAccuracy)
                .Take(3)
                .ToList();

            var untestedTags = allTagInsights
                .Where(t => t.TotalAttempts == 0)
                .OrderByDescending(t => t.WordCount)
                .Take(3)
                .ToList();

            // Combine for VocabInsight: struggling first, then untested
            var tagGroups = strugglingTags.Concat(untestedTags).Take(3).ToList();

            // Pick sample focus words FROM the highlighted categories, not globally
            var highlightedTagNames = new HashSet<string>(
                tagGroups.Select(t => t.Tag),
                StringComparer.OrdinalIgnoreCase);

            List<string> focusWords;
            if (strugglingTags.Any())
            {
                // Words from struggling categories that the user has actually attempted
                focusWords = dueWords
                    .Where(w => w.TotalAttempts > 0 && w.Accuracy < 0.6f
                        && w.VocabularyWord?.Tags != null
                        && w.VocabularyWord.Tags
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Any(tag => highlightedTagNames.Contains(tag)))
                    .OrderBy(w => w.Accuracy)
                    .Take(5)
                    .Select(w => w.VocabularyWord?.TargetLanguageTerm ?? "?")
                    .ToList();
            }
            else if (untestedTags.Any())
            {
                // Words from the top untested category — they're new, not struggling
                var topUntestedTag = untestedTags.First().Tag;
                focusWords = dueWords
                    .Where(w => w.TotalAttempts == 0
                        && w.VocabularyWord?.Tags != null
                        && w.VocabularyWord.Tags
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Any(tag => string.Equals(tag, topUntestedTag, StringComparison.OrdinalIgnoreCase)))
                    .Take(5)
                    .Select(w => w.VocabularyWord?.TargetLanguageTerm ?? "?")
                    .ToList();
            }
            else
            {
                focusWords = new List<string>();
            }

            var previewWords = focusSet
                .Where(w => w.VocabularyWord is not null
                    && !string.IsNullOrWhiteSpace(w.VocabularyWord.TargetLanguageTerm)
                    && !string.IsNullOrWhiteSpace(w.VocabularyWord.NativeLanguageTerm))
                .GroupBy(w => w.VocabularyWordId)
                .Select(g => g.First())
                .Select(w => new PlanPreviewWord(
                    w.VocabularyWordId,
                    w.VocabularyWord!.TargetLanguageTerm.Trim(),
                    w.VocabularyWord.NativeLanguageTerm!.Trim()))
                .ToList();

            // Build pattern insight with correct framing: untested vs struggling
            string? patternInsight = null;
            if (strugglingTags.Any())
            {
                var worstTag = strugglingTags.First();
                patternInsight = $"You're finding {worstTag.Tag.ToLower()} vocabulary challenging — {worstTag.WordCount} words need more practice (avg {worstTag.AverageAccuracy:P0} accuracy)";
            }
            else if (untestedTags.Any())
            {
                var topNewTag = untestedTags.First();
                patternInsight = $"Today's plan includes {topNewTag.Tag.ToLower()} vocabulary that's new to you — {topNewTag.WordCount} words you haven't practiced yet";
            }

            vocabInsight = new VocabInsight(
                vocabReview.TotalDue,
                reviewWords.Count,
                newWords.Count,
                avgMastery,
                tagGroups,
                focusWords,
                patternInsight,
                previewWords);

            // Build the story for vocab
            if (newWords.Count > 0 && reviewWords.Count > 0)
            {
                storyParts.Add($"Your vocabulary work today is a mix: {reviewWords.Count} words you've seen before and {newWords.Count} new ones");
                focusAreas.Add($"Review {reviewWords.Count} familiar words + learn {newWords.Count} new");
            }
            else if (newWords.Count > 0)
            {
                storyParts.Add($"All {newWords.Count} vocabulary words today are brand new — take your time with them");
                focusAreas.Add($"Learn {newWords.Count} new vocabulary words");
            }
            else
            {
                storyParts.Add($"You're reviewing {reviewWords.Count} words you've studied before");
                if (avgMastery < 0.5f)
                {
                    storyParts.Add("Many of these are still in early stages — focus on recognition first");
                    focusAreas.Add("Strengthen recognition of vocabulary still being learned");
                }
                else
                {
                    storyParts.Add("Most of these are coming along well — push for recall and production");
                    focusAreas.Add("Practice active recall and production of familiar words");
                }
            }

            if (patternInsight != null)
                focusAreas.Add(patternInsight);
        }

        // 3. Activity focus
        var inputActivities = activities.Where(a => a.ActivityType is "Reading" or "Listening" or "VideoWatching").ToList();
        var outputActivities = activities.Where(a => a.ActivityType is "Translation" or "Cloze" or "Writing" or "Shadowing").ToList();
        
        if (inputActivities.Any() && outputActivities.Any())
        {
            var inputLabel = inputActivities.First().ActivityType.ToLower();
            var outputLabel = outputActivities.First().ActivityType.ToLower();
            storyParts.Add($"You'll start with {inputLabel} for comprehension, then move to {outputLabel} to practice producing the language");
        }

        var story = string.Join(". ", storyParts) + ".";

        return new PlanNarrative(resources, vocabInsight, story, focusAreas);
    }

    private string SelectInputActivity(
        SelectedResource resource,
        HashSet<string> yesterdayActivities,
        List<string> recentActivities,
        DateTime today)
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

        // Prefer least recently used, with deterministic tiebreaker so the same day always produces the same order
        var selected = inputActivities
            .OrderBy(a => recentActivities.Count(r => r == a))
            .ThenBy(a => DeterministicHash.Combine(a, today))
            .First();

        return selected;
    }

    private string SelectOutputActivity(
        SelectedResource resource,
        HashSet<string> yesterdayActivities,
        List<string> recentActivities,
        DateTime today)
    {
        var outputActivities = new List<string> { "Translation", "Cloze", "Writing" };

        // Add shadowing if resource has audio (highly effective for pronunciation)
        if (resource.HasAudio)
            outputActivities.Add("Shadowing");

        // Filter out yesterday's activities
        var fresh = outputActivities.Where(a => !yesterdayActivities.Contains(a)).ToList();
        if (fresh.Any())
            outputActivities = fresh;

        // Prefer least recently used, with deterministic tiebreaker so the same day always produces the same order
        var selected = outputActivities
            .OrderBy(a => recentActivities.Count(r => r == a))
            .ThenBy(a => DeterministicHash.Combine(a, today))
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
    public string? ResourceId { get; set; }
    public string? SkillId { get; set; }
    public int EstimatedMinutes { get; set; }
    public bool IsContextual { get; set; }
    public bool IsBootstrap { get; set; }
    public int UnseenWordCount { get; set; }
    public List<VocabularyProgress> DueWords { get; set; } = new(); // The actual selected words for analysis, including bootstrap words.
    public List<string> FocusVocabularyIds { get; set; } = new();
}

/// <summary>
/// Represents a selected primary resource with selection metadata
/// </summary>
public class SelectedResource
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; }
    public string MediaType { get; set; }
    public string Language { get; set; }
    public int VocabularyCount { get; set; }
    public int? DaysSinceLastUse { get; set; }
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
    public string Id { get; set; } = string.Empty;
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
    public List<string> FocusVocabularyIds { get; set; } = new();
    public int TotalMinutes { get; set; }
    public string ResourceSelectionReason { get; set; }
    public PlanNarrative? Narrative { get; set; }
}

/// <summary>
/// Represents a single planned activity with metadata
/// </summary>
public class PlannedActivity
{
    public string ActivityType { get; set; }
    public string? ResourceId { get; set; }
    public string? SkillId { get; set; }
    public int EstimatedMinutes { get; set; }
    public int Priority { get; set; }
    public string Rationale { get; set; }
    public List<string> FocusVocabularyIds { get; set; } = new();
}

/// <summary>
/// Internal class for resource candidate scoring
/// </summary>
internal class ResourceCandidate
{
    public LearningResource Resource { get; set; }
    public DateTime? LastUsed { get; set; }
    public int? DaysSinceLastUse { get; set; }
    public int VocabCount { get; set; }
    public bool IsVocabResource { get; set; }
    public double Score { get; set; }
}
