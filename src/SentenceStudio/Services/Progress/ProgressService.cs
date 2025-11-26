using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services.Progress;

public class ProgressService : IProgressService
{
    private readonly LearningResourceRepository _resourceRepo;
    private readonly SkillProfileRepository _skillRepo;
    private readonly UserActivityRepository _activityRepo;
    private readonly VocabularyProgressService _vocabService;
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly ProgressCacheService _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILlmPlanGenerationService _llmPlanService;
    private readonly ILogger<ProgressService> _logger;

    public ProgressService(
        LearningResourceRepository resourceRepo,
        SkillProfileRepository skillRepo,
        UserActivityRepository activityRepo,
        VocabularyProgressService vocabService,
        VocabularyProgressRepository progressRepo,
        ProgressCacheService cache,
        IServiceProvider serviceProvider,
        ILlmPlanGenerationService llmPlanService,
        ILogger<ProgressService> logger)
    {
        _resourceRepo = resourceRepo;
        _skillRepo = skillRepo;
        _activityRepo = activityRepo;
        _vocabService = vocabService;
        _progressRepo = progressRepo;
        _cache = cache;
        _serviceProvider = serviceProvider;
        _llmPlanService = llmPlanService;
        _logger = logger;
    }

    public async Task<List<ResourceProgress>> GetRecentResourceProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetResourceProgress();
        if (cached != null)
            return cached;

        // PHASE 1 OPTIMIZATION: Use lightweight query and SQL aggregation instead of N+1 queries
        var resources = await _resourceRepo.GetAllResourcesLightweightAsync();
        var recent = resources
            .Take(20) // Already ordered by UpdatedAt in GetAllResourcesLightweightAsync
            .ToList();

        // Get progress aggregations for all recent resources in ONE query
        var resourceIds = recent.Select(r => r.Id).ToList();
        var aggregations = await _progressRepo.GetMultipleResourceProgressAggregationsAsync(resourceIds);

        var list = new List<ResourceProgress>();
        foreach (var r in recent)
        {
            // Get aggregated data from dictionary (O(1) lookup)
            if (aggregations.TryGetValue(r.Id, out var agg))
            {
                var minutes = Math.Clamp(agg.TotalAttempts / 3, 0, 180);
                var last = r.UpdatedAt == default ? r.CreatedAt : r.UpdatedAt;
                list.Add(new ResourceProgress(
                    r.Id,
                    r.Title ?? $"Resource #{r.Id}",
                    agg.AverageMasteryScore,
                    last.ToUniversalTime(),
                    agg.TotalAttempts,
                    agg.CorrectRate,
                    minutes));
            }
            else
            {
                // Resource has no progress data yet
                var last = r.UpdatedAt == default ? r.CreatedAt : r.UpdatedAt;
                list.Add(new ResourceProgress(
                    r.Id,
                    r.Title ?? $"Resource #{r.Id}",
                    0,
                    last.ToUniversalTime(),
                    0,
                    0,
                    0));
            }
        }

        var result = list
            .OrderByDescending(x => x.LastActivityUtc)
            .Take(max)
            .ToList();

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetResourceProgress(result);
        return result;
    }

    public async Task<List<SkillProgress>> GetRecentSkillProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default)
    {
        // PHASE 1 OPTIMIZATION: Use SQL aggregation instead of loading all vocab and progress
        var skills = await _skillRepo.ListAsync();

        // Get overall proficiency in ONE query instead of loading all vocab words
        var overallAgg = await _progressRepo.GetOverallProgressAggregationAsync();
        double prof = overallAgg?.AverageMasteryScore ?? 0;

        var list = new List<SkillProgress>();
        foreach (var s in skills)
        {
            var last = s.UpdatedAt == default ? s.CreatedAt : s.UpdatedAt;
            var delta = 0.0; // Could compute from last 7d vs prior period when detailed events are available
            list.Add(new SkillProgress(s.Id, s.Title ?? $"Skill #{s.Id}", prof, delta, last.ToUniversalTime()));
        }

        return list
            .OrderByDescending(x => x.LastActivityUtc)
            .Take(max)
            .ToList();
    }

    public async Task<SkillProgress?> GetSkillProgressAsync(int skillId, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetSkillProgress(skillId);
        if (cached != null)
            return cached;

        // PHASE 1 OPTIMIZATION: Use SQL aggregation instead of loading all vocab and progress
        var skills = await _skillRepo.ListAsync();
        var s = skills.FirstOrDefault(x => x.Id == skillId);
        if (s == null) return null;

        // Get overall proficiency in ONE query
        var overallAgg = await _progressRepo.GetOverallProgressAggregationAsync();
        double prof = overallAgg?.AverageMasteryScore ?? 0;

        var last = s.UpdatedAt == default ? s.CreatedAt : s.UpdatedAt;
        var result = new SkillProgress(s.Id, s.Title ?? $"Skill #{s.Id}", prof, 0.0, last.ToUniversalTime());

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetSkillProgress(skillId, result);
        return result;
    }

    public async Task<VocabProgressSummary> GetVocabSummaryAsync(DateTime fromUtc, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetVocabSummary();
        if (cached != null)
            return cached;

        _logger.LogDebug("🏴‍☠️ Loading VocabSummary from database");
        // PHASE 1 OPTIMIZATION: Use SQL aggregation instead of loading all progress records
        var (newCount, learning, review, known) = await _progressRepo.GetVocabSummaryCountsAsync();
        var success7d = await _progressRepo.GetSuccessRate7dAsync();

        _logger.LogDebug("🏴‍☠️ VocabSummary: New={NewCount}, Learning={Learning}, Review={Review}, Known={Known}, Success7d={Success7d}", newCount, learning, review, known, success7d);
        var result = new VocabProgressSummary(newCount, learning, review, known, success7d);

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetVocabSummary(result);
        return result;
    }

    public async Task<IReadOnlyList<PracticeHeatPoint>> GetPracticeHeatAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetPracticeHeat();
        if (cached != null)
            return cached;

        // Use UserActivity as the single source of truth for all practice activities
        var userActivities = await _activityRepo.GetByDateRangeAsync(fromUtc, toUtc);

        // Group by day
        var dailyActivities = userActivities
            .GroupBy(a => a.CreatedAt.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        // Generate all days in the range
        var days = Enumerable.Range(0, (int)(toUtc.Date - fromUtc.Date).TotalDays + 1)
            .Select(i => fromUtc.Date.AddDays(i))
            .ToList();

        var results = new List<PracticeHeatPoint>();
        foreach (var day in days)
        {
            // Get activity count for this day
            int count = dailyActivities.GetValueOrDefault(day.Date, 0);
            results.Add(new PracticeHeatPoint(day, count));
        }

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetPracticeHeat(results);
        return results;
    }

    public async Task<TodaysPlan> GenerateTodaysPlanAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;

        var existingPlan = await GetCachedPlanAsync(today, ct);
        if (existingPlan != null)
            return existingPlan;

        _logger.LogDebug("🤖 Generating new plan with LLM...");

        try
        {
            // Use LLM to generate the plan
            var llmResponse = await _llmPlanService.GeneratePlanAsync(ct);

            if (llmResponse == null || !llmResponse.Activities.Any())
            {
                _logger.LogDebug("⚠️ LLM returned no activities, falling back to basic plan");
                return await GenerateFallbackPlanAsync(today, ct);
            }

            // Get vocab due count for enrichment
            var vocabDueCount = await GetVocabDueCountAsync(today, ct);

            // Build lookup dictionaries for resource and skill names
            var resources = await _resourceRepo.GetAllResourcesLightweightAsync();
            var skills = await _skillRepo.ListAsync();

            var resourceTitles = resources.ToDictionary(r => r.Id, r => r.Title ?? "Untitled");
            var skillNames = skills.ToDictionary(s => s.Id, s => s.Title ?? "Untitled");

            // Convert LLM response to TodaysPlan
            var plan = PlanConverter.ConvertToTodaysPlan(
                llmResponse,
                today,
                resourceTitles,
                skillNames,
                vocabDueCount
            );

            // Add streak info
            var streak = await GetStreakInfoAsync(ct);

            // Build display strings
            var uniqueResourceTitles = plan.Items
                .Where(i => !string.IsNullOrEmpty(i.ResourceTitle))
                .Select(i => i.ResourceTitle!)
                .Distinct()
                .ToList();
            var skillTitle = plan.Items
                .FirstOrDefault(i => !string.IsNullOrEmpty(i.SkillName))?.SkillName;

            var enrichedPlan = new TodaysPlan(
                GeneratedForDate: today,
                Items: plan.Items,
                EstimatedTotalMinutes: plan.Items.Sum(i => i.EstimatedMinutes),
                CompletedCount: 0,
                TotalCount: plan.Items.Count,
                CompletionPercentage: 0.0,
                Streak: streak,
                ResourceTitles: uniqueResourceTitles.Any() ? string.Join(", ", uniqueResourceTitles) : null,
                SkillTitle: skillTitle,
                Rationale: plan.Rationale
            );

            // Enrich with any existing completion data from database (resume support)
            enrichedPlan = await EnrichPlanWithCompletionDataAsync(enrichedPlan, ct);

            // CRITICAL: Pre-create DailyPlanCompletion records for all plan items
            // This ensures progress can be saved even if cache is lost
            await InitializePlanCompletionRecordsAsync(enrichedPlan, ct);

            await CachePlanAsync(enrichedPlan, ct);
            return enrichedPlan;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ LLM plan generation failed");
            _logger.LogDebug("⚠️ Falling back to basic plan");
            return await GenerateFallbackPlanAsync(today, ct);
        }
    }

    private async Task<TodaysPlan> GenerateFallbackPlanAsync(DateTime today, CancellationToken ct)
    {
        _logger.LogDebug("📝 Generating fallback plan (vocab review only)");

        var planItems = new List<DailyPlanItem>();
        var vocabDueCount = await GetVocabDueCountAsync(today, ct);

        if (vocabDueCount >= 5)
        {
            planItems.Add(new DailyPlanItem(
                Id: GeneratePlanItemId(today, PlanActivityType.VocabularyReview),
                TitleKey: "plan_item_vocab_review_title",
                DescriptionKey: "plan_item_vocab_review_desc",
                ActivityType: PlanActivityType.VocabularyReview,
                EstimatedMinutes: Math.Min(vocabDueCount / 4, 15),
                Priority: 1,
                IsCompleted: false,
                CompletedAt: null,
                Route: "/vocabulary-quiz",
                RouteParameters: new() { ["Mode"] = "SRS", ["DueOnly"] = true },
                ResourceId: null,
                ResourceTitle: null,
                SkillId: null,
                SkillName: null,
                VocabDueCount: vocabDueCount,
                DifficultyLevel: null
            ));
        }

        var streak = await GetStreakInfoAsync(ct);

        var plan = new TodaysPlan(
            GeneratedForDate: today,
            Items: planItems,
            EstimatedTotalMinutes: planItems.Sum(i => i.EstimatedMinutes),
            CompletedCount: 0,
            TotalCount: planItems.Count,
            CompletionPercentage: 0.0,
            Streak: streak,
            ResourceTitles: null,
            SkillTitle: null,
            Rationale: "Generated fallback plan due to insufficient data or service unavailability. Focusing on vocabulary review to maintain learning momentum."
        );

        plan = await EnrichPlanWithCompletionDataAsync(plan, ct);
        await CachePlanAsync(plan, ct);
        return plan;
    }

    public async Task<TodaysPlan?> GetCachedPlanAsync(DateTime date, CancellationToken ct = default)
    {
        _logger.LogDebug("🔍 GetCachedPlanAsync for {Date:yyyy-MM-dd} (Kind={Kind})", date, date.Kind);

        var cachedPlan = _cache.GetTodaysPlan();
        if (cachedPlan == null)
        {
            _logger.LogDebug("⚠️ No plan in memory cache - checking database...");

            // Try to reconstruct from database
            var reconstructedPlan = await ReconstructPlanFromDatabase(date, ct);
            if (reconstructedPlan != null)
            {
                _logger.LogDebug("✅ Reconstructed plan from database with {Count} items", reconstructedPlan.Items.Count);
                _cache.SetTodaysPlan(reconstructedPlan);
                return reconstructedPlan;
            }

            _logger.LogDebug("⚠️ No plan in database either - need to generate new one");
            return null;
        }

        _logger.LogDebug("📊 Cache contains plan for {CachedDate:yyyy-MM-dd} (Kind={Kind})", cachedPlan.GeneratedForDate, cachedPlan.GeneratedForDate.Kind);

        // Validate cached plan is for the requested date
        if (cachedPlan.GeneratedForDate.Date != date.Date)
        {
            _logger.LogDebug("❌ Cache date mismatch: requested={RequestedDate:yyyy-MM-dd}, cached={CachedDate:yyyy-MM-dd}", date.Date, cachedPlan.GeneratedForDate.Date);
            _cache.InvalidateTodaysPlan();

            // Try database before giving up
            var reconstructedPlan = await ReconstructPlanFromDatabase(date, ct);
            if (reconstructedPlan != null)
            {
                _logger.LogDebug("✅ Found plan in database for correct date");
                _cache.SetTodaysPlan(reconstructedPlan);
                return reconstructedPlan;
            }

            return null;
        }

        _logger.LogDebug("✅ Cache date matches - enriching with latest completion data");

        // Enrich with latest completion data from database
        var enrichedPlan = await EnrichPlanWithCompletionDataAsync(cachedPlan, ct);

        // Update cache with enriched data
        _cache.UpdateTodaysPlan(enrichedPlan);

        return enrichedPlan;
    }

    public async Task ClearCachedPlanAsync(DateTime date, CancellationToken ct = default)
    {
        _logger.LogDebug("🗑️ ClearCachedPlanAsync for {Date:yyyy-MM-dd}", date);

        // Clear from memory cache
        _cache.InvalidateTodaysPlan();
        _logger.LogDebug("✅ Cleared memory cache");

        // CRITICAL: Also delete database records to prevent reconstruction
        // Without this, GetCachedPlanAsync will reconstruct from database and never call LLM
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var completionsToDelete = await db.DailyPlanCompletions
            .Where(c => c.Date == date.Date)
            .ToListAsync(ct);

        if (completionsToDelete.Any())
        {
            db.DailyPlanCompletions.RemoveRange(completionsToDelete);
            await db.SaveChangesAsync(ct);
            _logger.LogDebug("🗑️ Deleted {Count} DailyPlanCompletion records from database", completionsToDelete.Count);
        }

        _logger.LogDebug("✅ Cache and database cleared - next GenerateTodaysPlanAsync will create fresh plan from LLM");
    }

    public async Task MarkPlanItemCompleteAsync(string planItemId, int minutesSpent, CancellationToken ct = default)
    {
        _logger.LogDebug("📊 MarkPlanItemCompleteAsync - planItemId={PlanItemId}, minutesSpent={MinutesSpent}", planItemId, minutesSpent);

        // CRITICAL: Use UTC date to match plan generation
        var today = DateTime.UtcNow.Date;
        _logger.LogDebug("📅 Using UTC date: {Today:yyyy-MM-dd}", today);

        var plan = _cache.GetTodaysPlan();

        if (plan == null)
        {
            _logger.LogDebug("⚠️ No cached plan found for today");
            return;
        }

        _logger.LogDebug("✅ Found cached plan for {Date:yyyy-MM-dd}", plan.GeneratedForDate);

        var item = plan.Items.FirstOrDefault(i => i.Id == planItemId);
        if (item == null)
        {
            _logger.LogDebug("⚠️ Plan item '{PlanItemId}' not found in cached plan", planItemId);
            _logger.LogDebug("📊 Available plan item IDs: {Ids}", string.Join(", ", plan.Items.Select(i => i.Id)));
            return;
        }

        _logger.LogDebug("✅ Found plan item: {TitleKey}", item.TitleKey);

        // Mark in database
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Check if record already exists
        var existing = await db.DailyPlanCompletions
            .FirstOrDefaultAsync(c => c.Date == today && c.PlanItemId == planItemId, ct);

        if (existing != null)
        {
            _logger.LogDebug("💾 Updating existing completion record in database");
            existing.IsCompleted = true;
            existing.CompletedAt = DateTime.UtcNow;
            existing.MinutesSpent = minutesSpent;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _logger.LogDebug("💾 Creating new completion record in database");
            var completion = new DailyPlanCompletion
            {
                Date = today,
                PlanItemId = planItemId,
                ActivityType = item.ActivityType.ToString(),
                ResourceId = item.ResourceId,
                SkillId = item.SkillId,
                IsCompleted = true,
                CompletedAt = DateTime.UtcNow,
                MinutesSpent = minutesSpent,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await db.DailyPlanCompletions.AddAsync(completion, ct);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("✅ Database updated");

        // Update cache - create new record with updated completion status
        var updatedItem = item with
        {
            IsCompleted = true,
            CompletedAt = DateTime.UtcNow,
            MinutesSpent = minutesSpent
        };

        var itemIndex = plan.Items.IndexOf(item);
        if (itemIndex >= 0)
        {
            plan.Items[itemIndex] = updatedItem;
        }

        // Recalculate plan completion percentage based on time
        var totalMinutesSpent = plan.Items.Sum(i => i.MinutesSpent);
        var totalEstimatedMinutes = plan.Items.Sum(i => i.EstimatedMinutes);
        var completionPercentage = totalEstimatedMinutes > 0
            ? Math.Min(100, (totalMinutesSpent / (double)totalEstimatedMinutes) * 100)
            : 0;

        var updatedPlan = plan with
        {
            CompletedCount = plan.Items.Count(i => i.IsCompleted),
            CompletionPercentage = completionPercentage
        };

        _cache.UpdateTodaysPlan(updatedPlan);
        _logger.LogDebug("✅ Cache updated - {Percentage:F0}% complete ({Spent}/{Estimated} min)", completionPercentage, totalMinutesSpent, totalEstimatedMinutes);
    }

    public async Task UpdatePlanItemProgressAsync(string planItemId, int minutesSpent, CancellationToken ct = default)
    {
        _logger.LogDebug("📊 UpdatePlanItemProgressAsync - planItemId={PlanItemId}, minutesSpent={MinutesSpent}", planItemId, minutesSpent);

        // CRITICAL: Use UTC date to match plan generation
        var today = DateTime.UtcNow.Date;
        _logger.LogDebug("📅 Using UTC date: {Today:yyyy-MM-dd}", today);

        // Update database FIRST - this should always work if plan was initialized
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var existing = await db.DailyPlanCompletions
            .FirstOrDefaultAsync(c => c.Date == today && c.PlanItemId == planItemId, ct);

        if (existing != null)
        {
            existing.MinutesSpent = minutesSpent;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogDebug("💾 Updated database record to {MinutesSpent} minutes (cache-independent)", minutesSpent);
        }
        else
        {
            _logger.LogDebug("⚠️ No DailyPlanCompletion record found for planItemId='{PlanItemId}' on {Today:yyyy-MM-dd}", planItemId, today);
            _logger.LogDebug("💡 This means the plan wasn't initialized properly");
        }

        // Also update cache if it exists (optional, for UI responsiveness)
        var plan = _cache.GetTodaysPlan();
        if (plan != null)
        {
            _logger.LogDebug("✅ Cache exists - updating cache too");

            var item = plan.Items.FirstOrDefault(i => i.Id == planItemId);
            if (item != null)
            {
                var updatedItem = item with { MinutesSpent = minutesSpent };
                var itemIndex = plan.Items.IndexOf(item);
                if (itemIndex >= 0)
                {
                    plan.Items[itemIndex] = updatedItem;
                }

                // Recalculate completion percentage
                var totalMinutesSpent = plan.Items.Sum(i => i.MinutesSpent);
                var totalEstimatedMinutes = plan.Items.Sum(i => i.EstimatedMinutes);
                var completionPercentage = totalEstimatedMinutes > 0
                    ? Math.Min(100, (totalMinutesSpent / (double)totalEstimatedMinutes) * 100)
                    : 0;

                var updatedPlan = plan with { CompletionPercentage = completionPercentage };
                _cache.UpdateTodaysPlan(updatedPlan);
                _logger.LogDebug("✅ Cache updated - {Percentage:F0}% complete", completionPercentage);
            }
        }
        else
        {
            _logger.LogDebug("ℹ️ No cached plan - database updated successfully, cache skipped");
        }
    }

    private async Task<int> GetVocabDueCountAsync(DateTime date, CancellationToken ct)
    {
        return await _progressRepo.GetDueVocabCountAsync(date);
    }

    private async Task<TodaysPlan> EnrichPlanWithCompletionDataAsync(TodaysPlan plan, CancellationToken ct)
    {
        _logger.LogDebug("🔧 Enriching plan with completion data for {Date:yyyy-MM-dd}", plan.GeneratedForDate);

        // Load completion data from database
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var completions = await db.DailyPlanCompletions
            .Where(c => c.Date == plan.GeneratedForDate.Date)
            .ToListAsync(ct);

        _logger.LogDebug("📊 Found {Count} completion records", completions.Count);

        // Create dictionary for O(1) lookup
        var completionDict = completions.ToDictionary(c => c.PlanItemId);

        // Update each plan item with completion data
        var enrichedItems = plan.Items.Select(item =>
        {
            if (completionDict.TryGetValue(item.Id, out var completion))
            {
                _logger.LogDebug("  ✅ {TitleKey}: {MinutesSpent} min, completed={IsCompleted}", item.TitleKey, completion.MinutesSpent, completion.IsCompleted);
                return item with
                {
                    IsCompleted = completion.IsCompleted,
                    CompletedAt = completion.CompletedAt,
                    MinutesSpent = completion.MinutesSpent
                };
            }
            return item;
        }).ToList();

        // Recalculate plan statistics
        var totalMinutesSpent = enrichedItems.Sum(i => i.MinutesSpent);
        var totalEstimatedMinutes = enrichedItems.Sum(i => i.EstimatedMinutes);
        var completionPercentage = totalEstimatedMinutes > 0
            ? Math.Min(100, (totalMinutesSpent / (double)totalEstimatedMinutes) * 100)
            : 0;

        var enrichedPlan = plan with
        {
            Items = enrichedItems,
            CompletedCount = enrichedItems.Count(i => i.IsCompleted),
            CompletionPercentage = completionPercentage
        };

        _logger.LogDebug("📊 Plan enriched: {Percentage:F0}% complete ({Spent}/{Estimated} min)", completionPercentage, totalMinutesSpent, totalEstimatedMinutes);

        return enrichedPlan;
    }

    private async Task<List<UserActivity>> GetRecentActivityHistoryAsync(int days, CancellationToken ct)
    {
        var fromDate = DateTime.UtcNow.AddDays(-days);
        return await _activityRepo.GetByDateRangeAsync(fromDate, DateTime.UtcNow);
    }

    private async Task<LearningResource?> SelectOptimalResourceAsync(List<UserActivity> recentHistory, CancellationToken ct)
    {
        var resources = await _resourceRepo.GetAllResourcesLightweightAsync();
        if (!resources.Any()) return null;

        var recentResourceIds = recentHistory
            .Where(a => !string.IsNullOrEmpty(a.Input))
            .Select(a => int.TryParse(a.Input, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet();

        var candidates = resources
            .Where(r => !recentResourceIds.Contains(r.Id))
            .ToList();

        if (!candidates.Any())
        {
            return resources.OrderBy(r => r.UpdatedAt).First();
        }

        return candidates.First();
    }

    private async Task<SkillProfile?> SelectOptimalSkillAsync(List<UserActivity> recentHistory, CancellationToken ct)
    {
        var skills = await _skillRepo.ListAsync();
        if (!skills.Any()) return null;

        return skills.First();
    }

    private PlanActivityType DetermineInputActivity(LearningResource resource, List<UserActivity> recentHistory)
    {
        var lastReading = recentHistory
            .Where(a => a.Activity == "Reading")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        if (lastReading == null || (DateTime.UtcNow - lastReading.CreatedAt).TotalDays >= 2)
            return PlanActivityType.Reading;

        return PlanActivityType.Listening;
    }

    private PlanActivityType DetermineOutputActivity(SkillProfile skill, List<UserActivity> recentHistory)
    {
        var recentOutput = recentHistory
            .Where(a => a.Activity is "Shadowing" or "Cloze" or "Translation")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        if (recentOutput == null)
            return PlanActivityType.Shadowing;

        return recentOutput.Activity switch
        {
            "Shadowing" => PlanActivityType.Cloze,
            "Cloze" => PlanActivityType.Translation,
            "Translation" => PlanActivityType.Shadowing,
            _ => PlanActivityType.Shadowing
        };
    }

    private async Task<StreakInfo> GetStreakInfoAsync(CancellationToken ct)
    {
        var activities = await _activityRepo.GetByDateRangeAsync(
            DateTime.UtcNow.AddDays(-90),
            DateTime.UtcNow);

        if (!activities.Any())
        {
            return new StreakInfo(0, 0, null);
        }

        var sortedDates = activities
            .Select(a => a.CreatedAt.Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        var currentStreak = 0;
        var longestStreak = 0;
        var lastDate = DateTime.UtcNow.Date;

        foreach (var date in sortedDates)
        {
            if ((lastDate - date).TotalDays <= 1)
            {
                currentStreak++;
                longestStreak = Math.Max(longestStreak, currentStreak);
                lastDate = date;
            }
            else
            {
                break;
            }
        }

        return new StreakInfo(
            CurrentStreak: currentStreak,
            LongestStreak: longestStreak,
            LastPracticeDate: sortedDates.FirstOrDefault()
        );
    }

    private async Task CachePlanAsync(TodaysPlan plan, CancellationToken ct)
    {
        _cache.SetTodaysPlan(plan);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Pre-create DailyPlanCompletion records for all plan items.
    /// This ensures progress can be saved even if cache is lost during the day.
    /// </summary>
    private async Task InitializePlanCompletionRecordsAsync(TodaysPlan plan, CancellationToken ct)
    {
        _logger.LogDebug("🏭️ Initializing DailyPlanCompletion records for {Count} plan items", plan.Items.Count);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var item in plan.Items)
        {
            // Check if record already exists
            var existing = await db.DailyPlanCompletions
                .FirstOrDefaultAsync(c => c.Date == plan.GeneratedForDate.Date && c.PlanItemId == item.Id, ct);

            if (existing == null)
            {
                var completion = new DailyPlanCompletion
                {
                    Date = plan.GeneratedForDate.Date,
                    PlanItemId = item.Id,
                    ActivityType = item.ActivityType.ToString(),
                    ResourceId = item.ResourceId,
                    SkillId = item.SkillId,
                    IsCompleted = false,
                    MinutesSpent = 0,
                    EstimatedMinutes = item.EstimatedMinutes,
                    Priority = item.Priority,
                    TitleKey = item.TitleKey,
                    DescriptionKey = item.DescriptionKey,
                    Rationale = plan.Rationale ?? string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await db.DailyPlanCompletions.AddAsync(completion, ct);
                _logger.LogDebug("  ✅ Created record for {TitleKey}", item.TitleKey);
            }
            else
            {
                _logger.LogDebug("  ⏭️ Record already exists for {TitleKey} ({MinutesSpent} min)", item.TitleKey, existing.MinutesSpent);
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("💾 Initialized {Count} DailyPlanCompletion records", plan.Items.Count);
    }

    /// <summary>
    /// Reconstruct a TodaysPlan from DailyPlanCompletion records in the database.
    /// This allows the plan to survive app restarts.
    /// </summary>
    private async Task<TodaysPlan?> ReconstructPlanFromDatabase(DateTime date, CancellationToken ct)
    {
        _logger.LogDebug("🔨 Attempting to reconstruct plan from database for {Date:yyyy-MM-dd}", date);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var completions = await db.DailyPlanCompletions
            .Where(c => c.Date == date.Date)
            .OrderBy(c => c.Priority)
            .ToListAsync(ct);

        if (!completions.Any())
        {
            _logger.LogDebug("⚠️ No DailyPlanCompletion records found for {Date:yyyy-MM-dd}", date);
            return null;
        }

        _logger.LogDebug("📊 Found {Count} completion records, reconstructing plan...", completions.Count);

        // Extract rationale from first record (stored redundantly in all records for same date)
        var rationale = completions.FirstOrDefault()?.Rationale ?? string.Empty;

        // Convert DailyPlanCompletion records back to PlanItems
        var planItems = new List<DailyPlanItem>();

        foreach (var completion in completions)
        {
            // Derive route and parameters from ActivityType and IDs using PlanConverter
            var activityType = Enum.Parse<PlanActivityType>(completion.ActivityType);
            var route = PlanConverter.GetRouteForActivity(activityType);
            var routeParams = PlanConverter.BuildRouteParameters(activityType, completion.ResourceId, completion.SkillId);

            var planItem = new DailyPlanItem(
                Id: completion.PlanItemId,
                TitleKey: completion.TitleKey,
                DescriptionKey: completion.DescriptionKey,
                ActivityType: activityType,
                EstimatedMinutes: completion.EstimatedMinutes,
                Priority: completion.Priority,
                IsCompleted: completion.IsCompleted,
                CompletedAt: completion.CompletedAt,
                Route: route,
                RouteParameters: routeParams,
                ResourceId: completion.ResourceId,
                ResourceTitle: null, // Will be enriched later if needed
                SkillId: completion.SkillId,
                SkillName: null, // Will be enriched later if needed
                VocabDueCount: null,
                DifficultyLevel: null,
                MinutesSpent: completion.MinutesSpent
            );

            planItems.Add(planItem);
            _logger.LogDebug("  ✅ Reconstructed: {TitleKey} ({Spent}/{Estimated} min) -> {Route}", completion.TitleKey, completion.MinutesSpent, completion.EstimatedMinutes, route);
        }

        // Calculate overall plan statistics
        var totalMinutesSpent = planItems.Sum(i => i.MinutesSpent);
        var totalEstimatedMinutes = planItems.Sum(i => i.EstimatedMinutes);
        var completionPercentage = totalEstimatedMinutes > 0
            ? Math.Min(100, (totalMinutesSpent / (double)totalEstimatedMinutes) * 100)
            : 0;

        // Get streak info
        var streak = await GetStreakInfoAsync(ct);

        var reconstructedPlan = new TodaysPlan(
            GeneratedForDate: date,
            Items: planItems,
            EstimatedTotalMinutes: totalEstimatedMinutes,
            CompletedCount: planItems.Count(i => i.IsCompleted),
            TotalCount: planItems.Count,
            CompletionPercentage: completionPercentage,
            Streak: streak,
            ResourceTitles: null, // Will be enriched later if needed
            SkillTitle: null,
            Rationale: rationale
        );

        _logger.LogDebug("✅ Reconstructed plan: {Percentage:F0}% complete ({Spent}/{Estimated} min)", completionPercentage, totalMinutesSpent, totalEstimatedMinutes);
        return reconstructedPlan;
    }

    /// <summary>
    /// Generate deterministic plan item ID based on date and activity type.
    /// This ensures same activity on same day always has same ID, enabling progress persistence.
    /// </summary>
    private string GeneratePlanItemId(DateTime date, PlanActivityType activityType, int? resourceId = null, int? skillId = null)
    {
        // Create deterministic string: date + activity + resource + skill
        var parts = new List<string>
        {
            date.ToString("yyyy-MM-dd"),
            activityType.ToString()
        };

        if (resourceId.HasValue)
            parts.Add($"r{resourceId.Value}");
        if (skillId.HasValue)
            parts.Add($"s{skillId.Value}");

        var combined = string.Join("_", parts);

        // Use deterministic hash to create stable GUID-like ID
        // This ensures same inputs always produce same ID
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
        var guid = new Guid(hash.Take(16).ToArray());

        _logger.LogDebug("🔑 Generated plan item ID: {Guid} for {Combined}", guid, combined);
        return guid.ToString();
    }
}
