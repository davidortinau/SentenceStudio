#if DEBUG
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.Native.Android;

internal sealed class DebugPreviewPlanGenerator : IDeterministicPlanGenerator
{
    public IReadOnlyList<string>? FocusWordIds { get; set; }

    public Task<PlanSkeleton?> GenerateAsync(
        string? userProfileId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userProfileId != DebugPreviewFixture.OwnerId || FocusWordIds is null)
            throw new UnauthorizedAccessException("Preview plan needs its isolated owner and seeded words.");

        return Task.FromResult<PlanSkeleton?>(new PlanSkeleton
        {
            Activities =
            [
                new PlannedActivity
                {
                    ActivityType = nameof(PlanActivityType.VocabularyReview),
                    FocusVocabularyIds = FocusWordIds.ToList(),
                    EstimatedMinutes = 10,
                    Priority = 1,
                    Rationale = "Isolated Debug preview fixture",
                },
            ],
            FocusVocabularyIds = FocusWordIds.ToList(),
            TotalMinutes = 10,
            ResourceSelectionReason = "Isolated Debug preview fixture",
        });
    }
}

internal static class DebugPreviewFixture
{
    internal const string OwnerId = "native-android-debug-preview";
    private const string ResourceId = "native-preview-vocabulary";
    private static readonly (string Target, string Native)[] Words =
    [
        ("안녕하세요", "hello"),
        ("감사합니다", "thank you"),
        ("사과", "apple"),
        ("물", "water"),
    ];

    public static async Task PrepareAsync(
        AndroidServiceHost host, IServiceScope scope, CancellationToken cancellationToken)
    {
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<ApplicationDbContext>();
        var wordIds = Enumerable.Range(0, Words.Length)
            .Select(index => $"native-preview-word-{index}")
            .ToList();
        var seeded = await database.UserProfiles.AsNoTracking()
            .AnyAsync(profile => profile.Id == OwnerId, cancellationToken);
        if (!seeded)
        {
            database.UserProfiles.Add(new UserProfile
            {
                Id = OwnerId,
                Name = "Isolated Android Debug Preview",
                Email = "native-preview@example.invalid",
                NativeLanguage = "English",
                TargetLanguage = "Korean",
                CreatedAt = DateTime.UtcNow,
            });
            database.LearningResources.Add(new LearningResource
            {
                Id = ResourceId,
                UserProfileId = OwnerId,
                Language = "Korean",
                MediaType = "Vocabulary List",
                Title = "Debug preview vocabulary",
                CreatedAt = DateTime.UtcNow,
            });
            for (var index = 0; index < Words.Length; index++)
            {
                var wordId = wordIds[index];
                database.VocabularyWords.Add(new VocabularyWord
                {
                    Id = wordId,
                    TargetLanguageTerm = Words[index].Target,
                    NativeLanguageTerm = Words[index].Native,
                    Language = "Korean",
                });
                database.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                {
                    Id = $"native-preview-mapping-{index}",
                    ResourceId = ResourceId,
                    VocabularyWordId = wordId,
                });
                database.VocabularyProgresses.Add(new VocabularyProgress
                {
                    Id = $"native-preview-progress-{index}",
                    UserId = OwnerId,
                    VocabularyWordId = wordId,
                    MasteryScore = 0.9f,
                    ProductionInStreak = 3,
                    TotalAttempts = 8,
                    CurrentStreak = 6,
                });
            }
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (await database.VocabularyWords.AsNoTracking()
            .CountAsync(word => word.Id.StartsWith("native-preview-word-"), cancellationToken)
            != Words.Length)
        {
            throw new InvalidOperationException(
                "Existing preview data is incomplete; it was not replaced or reset.");
        }

        services.GetRequiredService<DebugPreviewPlanGenerator>().FocusWordIds = wordIds;
        services.GetRequiredService<IPreferencesService>().Set(
            UserProfileRepository.ActiveProfileIdKey, OwnerId);
        services.GetRequiredService<DeviceUserScopeProvider>().SetActiveUser(OwnerId);
        var preferences = services.GetRequiredService<VocabularyQuizPreferences>();
        preferences.DisplayDirection = "TargetToNative";
        preferences.UseTextPrompt = true;
        preferences.UseAudioPrompt = false;
        preferences.UsePhotoPrompt = false;

        using var planScope = host.CreateScope();
        var plan = planScope.ServiceProvider.GetRequiredService<IPlanService>();
        if (await plan.GetTodayAsync(cancellationToken) is null)
            await plan.GenerateTodayAsync(
                new GenerateTodaysPlanRequest { Strategy = "deterministic" },
                cancellationToken);
    }
}
#endif
