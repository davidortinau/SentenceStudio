using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.Services.PlanGeneration;

public interface ILlmPlanGenerationService
{
    Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default);
}

public class LlmPlanGenerationService : ILlmPlanGenerationService
{
    private readonly IChatClient _chatClient;
    private readonly UserProfileRepository _userProfileRepo;
    private readonly LearningResourceRepository _resourceRepo;
    private readonly SkillProfileRepository _skillRepo;
    private readonly VocabularyProgressRepository _vocabProgressRepo;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LlmPlanGenerationService> _logger;

    private static readonly string PromptTemplate;

    static LlmPlanGenerationService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "SentenceStudio.Resources.Prompts.DailyPlanGeneration.scriban";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Could not find embedded resource: {resourceName}");

        using var reader = new StreamReader(stream);
        PromptTemplate = reader.ReadToEnd();
    }

    public LlmPlanGenerationService(
        IChatClient chatClient,
        UserProfileRepository userProfileRepo,
        LearningResourceRepository resourceRepo,
        SkillProfileRepository skillRepo,
        VocabularyProgressRepository vocabProgressRepo,
        IServiceProvider serviceProvider,
        ILogger<LlmPlanGenerationService> logger)
    {
        _chatClient = chatClient;
        _userProfileRepo = userProfileRepo;
        _resourceRepo = resourceRepo;
        _skillRepo = skillRepo;
        _vocabProgressRepo = vocabProgressRepo;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("🤖 Starting LLM plan generation");

            var userProfile = await _userProfileRepo.GetAsync();
            if (userProfile == null)
            {
                _logger.LogWarning("❌ No user profile found");
                return null;
            }

            var request = await BuildPlanRequestAsync(userProfile, ct);
            var prompt = RenderPrompt(request);

            _logger.LogDebug("📝 Prompt length: {PromptLength} chars", prompt.Length);
            _logger.LogDebug("📊 Context: {VocabDue} vocab due, {RecentCount} recent activities, {ResourceCount} resources, {SkillCount} skills",
                request.VocabularyDueCount, request.RecentHistory.Count, request.AvailableResources.Count, request.AvailableSkills.Count);
            _logger.LogTrace("\n========== FULL PROMPT TO LLM ==========\n{Prompt}\n========== END PROMPT ==========\n", prompt);

            var response = await _chatClient.GetResponseAsync<DailyPlanResponse>(prompt, cancellationToken: ct);

            if (response?.Result != null)
            {
                _logger.LogDebug("\n========== LLM RESPONSE ==========");
                _logger.LogDebug("Activities Count: {Count}", response.Result.Activities.Count);
                _logger.LogDebug("Rationale: {Rationale}", response.Result.Rationale);
                _logger.LogDebug("\nActivities:");
                foreach (var activity in response.Result.Activities)
                {
                    _logger.LogDebug("  - {ActivityType}: {Minutes}min (Priority: {Priority}, ResourceId: {ResourceId}, SkillId: {SkillId})",
                        activity.ActivityType, activity.EstimatedMinutes, activity.Priority, activity.ResourceId, activity.SkillId);
                }
                _logger.LogDebug("========== END LLM RESPONSE ==========");

                _logger.LogInformation("✅ LLM generated plan with {ActivityCount} activities", response.Result.Activities.Count);
                _logger.LogInformation("💡 Rationale: {Rationale}", response.Result.Rationale);
                return response.Result;
            }

            _logger.LogWarning("⚠️ LLM returned null response");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ LLM plan generation failed");
            return null;
        }
    }

    private async Task<DailyPlanRequest> BuildPlanRequestAsync(UserProfile userProfile, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var twoWeeksAgo = today.AddDays(-14);

        var vocabDue = await _vocabProgressRepo.GetDueVocabCountAsync(today);

        // Query DailyPlanCompletion records for recent activity history
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var recentCompletions = await db.DailyPlanCompletions
            .Where(c => c.Date >= twoWeeksAgo)
            .OrderByDescending(c => c.Date)
            .ToListAsync(ct);

        var resources = await _resourceRepo.GetAllResourcesLightweightAsync();
        var skills = await _skillRepo.ListAsync();

        // Get vocabulary counts for each resource
        var vocabularyCounts = await db.ResourceVocabularyMappings
            .GroupBy(rvm => rvm.ResourceId)
            .Select(g => new { ResourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ResourceId, x => x.Count, ct);

        // Build activity history with resource/skill titles
        var recentHistory = new List<ActivitySummary>();
        foreach (var completion in recentCompletions)
        {
            var resource = completion.ResourceId.HasValue
                ? resources.FirstOrDefault(r => r.Id == completion.ResourceId.Value)
                : null;
            var skill = completion.SkillId.HasValue
                ? skills.FirstOrDefault(s => s.Id == completion.SkillId.Value)
                : null;

            recentHistory.Add(new ActivitySummary
            {
                Date = completion.Date,
                ActivityType = completion.ActivityType,
                ResourceId = completion.ResourceId,
                ResourceTitle = resource?.Title,
                SkillId = completion.SkillId,
                SkillName = skill?.Title,
                MinutesSpent = completion.MinutesSpent
            });
        }

        return new DailyPlanRequest
        {
            PreferredSessionMinutes = userProfile.PreferredSessionMinutes,
            TargetLevel = userProfile.TargetCEFRLevel ?? "Not Set",
            NativeLanguage = userProfile.NativeLanguage,
            TargetLanguage = userProfile.TargetLanguage,
            VocabularyDueCount = vocabDue,
            RecentHistory = recentHistory,
            AvailableResources = resources.Select(r => new ResourceOption
            {
                Id = r.Id,
                Title = r.Title ?? "Untitled",
                MediaType = r.MediaType ?? "Unknown",
                Language = r.Language ?? "Unknown",
                WordCount = vocabularyCounts.TryGetValue(r.Id, out var count) ? count : 0,
                HasAudio = r.MediaType == "Podcast" || r.MediaType == "Video",
                YouTubeUrl = r.MediaType == "Video" && !string.IsNullOrEmpty(r.MediaUrl) && r.MediaUrl.Contains("youtube")
                    ? r.MediaUrl
                    : null
            }).ToList(),
            AvailableSkills = skills.Select(s => new SkillOption
            {
                Id = s.Id,
                Title = s.Title ?? "Untitled",
                Description = s.Description ?? string.Empty
            }).ToList()
        };
    }

    private string RenderPrompt(DailyPlanRequest request)
    {
        var template = Template.Parse(PromptTemplate);

        var model = new
        {
            preferred_minutes = request.PreferredSessionMinutes,
            target_level = request.TargetLevel,
            native_language = request.NativeLanguage,
            target_language = request.TargetLanguage,
            vocab_due_count = request.VocabularyDueCount,
            recent_history = request.RecentHistory.Select(a => new
            {
                date = a.Date,
                activity_type = a.ActivityType,
                resource_id = a.ResourceId,
                resource_title = a.ResourceTitle,
                skill_id = a.SkillId,
                skill_name = a.SkillName,
                minutes_spent = a.MinutesSpent
            }).ToList(),
            available_resources = request.AvailableResources.Select(r => new
            {
                id = r.Id,
                title = r.Title,
                media_type = r.MediaType,
                language = r.Language,
                word_count = r.WordCount,
                has_audio = r.HasAudio,
                youtube_url = r.YouTubeUrl
            }).ToList(),
            available_skills = request.AvailableSkills.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                description = s.Description
            }).ToList()
        };

        return template.Render(model);
    }
}
