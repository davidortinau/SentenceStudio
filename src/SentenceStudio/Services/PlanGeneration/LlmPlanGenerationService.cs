using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
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
        IServiceProvider serviceProvider)
    {
        _chatClient = chatClient;
        _userProfileRepo = userProfileRepo;
        _resourceRepo = resourceRepo;
        _skillRepo = skillRepo;
        _vocabProgressRepo = vocabProgressRepo;
        _serviceProvider = serviceProvider;
    }

    public async Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default)
    {
        try
        {
            Debug.WriteLine("🤖 Starting LLM plan generation...");

            var userProfile = await _userProfileRepo.GetAsync();
            if (userProfile == null)
            {
                Debug.WriteLine("❌ No user profile found");
                return null;
            }

            var request = await BuildPlanRequestAsync(userProfile, ct);
            var prompt = RenderPrompt(request);

            Debug.WriteLine($"📝 Prompt length: {prompt.Length} chars");
            Debug.WriteLine($"📊 Context: {request.VocabularyDueCount} vocab due, {request.RecentHistory.Count} recent activities, {request.AvailableResources.Count} resources, {request.AvailableSkills.Count} skills");

            var response = await _chatClient.GetResponseAsync<DailyPlanResponse>(prompt, cancellationToken: ct);

            if (response?.Result != null)
            {
                Debug.WriteLine($"✅ LLM generated plan with {response.Result.Activities.Count} activities");
                Debug.WriteLine($"💡 Rationale: {response.Result.Rationale}");
                return response.Result;
            }

            Debug.WriteLine("⚠️ LLM returned null response");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ LLM plan generation failed: {ex.Message}");
            Debug.WriteLine($"❌ Stack trace: {ex.StackTrace}");
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
                WordCount = r.Vocabulary?.Count ?? 0,
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
