#if DEBUG
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Contracts.Plans;

namespace SentenceStudio.Native.iOS.Platform;

public sealed record NativePreviewFixture(string Surface, string Owner, string Email)
{
    public static async Task<NativePreviewFixture?> ForAuthenticatedOwnerAsync(
        IServiceProvider provider, string? owner, CancellationToken cancellationToken)
    {
        var attributes = typeof(NativePreviewFixture).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        if (!attributes.TryGetValue("SentenceStudio.Native.AuthenticatedSampleOwnerId",
                out var expectedOwner))
            return null;

        var surface = attributes.GetValueOrDefault(
            "SentenceStudio.Native.AuthenticatedSampleSurface") ?? "dashboard";
        if (surface is not ("dashboard" or "quiz" or "question" or "feedback"
                or "summary" or "complete" or "resume" or "back")
            || string.IsNullOrWhiteSpace(owner)
            || !string.Equals(owner, expectedOwner, StringComparison.Ordinal)
            || attributes.GetValueOrDefault("SentenceStudio.Native.PreviewSchema") != "true"
            || NativeApiEndpoint.FromAssembly() is not { IsLoopback: true })
            throw new InvalidOperationException(
                "An authenticated Debug sample needs the matching owner, isolated schema, and loopback API.");

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == owner, cancellationToken);
        if (string.IsNullOrWhiteSpace(profile?.Email))
            throw new InvalidOperationException(
                "An authenticated Debug sample needs an existing profile for the signed-in owner.");

        return new NativePreviewFixture(surface, owner, profile.Email);
    }

    public static NativePreviewFixture? FromAssembly()
    {
        var attributes = typeof(NativePreviewFixture).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        if (!attributes.TryGetValue("SentenceStudio.Native.PreviewSurface", out var surface))
            return null;
        if (surface is not ("dashboard" or "question" or "mc" or "mc-back"
                or "mc-resume" or "mc-details" or "feedback" or "summary" or "complete"
                or "back" or "resume")
            || !attributes.TryGetValue("SentenceStudio.Native.PreviewSchema", out var schema)
            || schema != "true"
            || !attributes.TryGetValue("SentenceStudio.Native.PreviewOwnerId", out var owner)
            || string.IsNullOrWhiteSpace(owner)
            || !attributes.TryGetValue("SentenceStudio.Native.PreviewEmail", out var email)
            || string.IsNullOrWhiteSpace(email)
            || NativeApiEndpoint.FromAssembly() is not { IsLoopback: true })
            throw new InvalidOperationException(
                "A native Debug preview needs an isolated local API, profile owner, and preview-only schema.");

        return new NativePreviewFixture(surface, owner, email);
    }

    public async Task SeedAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.UserProfiles.AnyAsync(profile => profile.Id != Owner))
            throw new InvalidOperationException(
                "The isolated Debug fixture refuses a database containing another owner.");

        if (!await db.UserProfiles.AnyAsync(profile => profile.Id == Owner))
        {
            db.UserProfiles.Add(new UserProfile
            {
                Id = Owner,
                Email = Email,
                Name = "Preview fixture",
                NativeLanguage = "English",
                TargetLanguage = "Korean",
                DisplayLanguage = "English",
                CreatedAt = DateTime.UtcNow
            });
        }

        var resourceId = $"ios-native-preview-{Owner}";
        if (!await db.LearningResources.AnyAsync(resource => resource.Id == resourceId))
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = resourceId,
                UserProfileId = Owner,
                Title = "Preview vocabulary",
                MediaType = "Vocabulary List",
                Language = "Korean",
                CreatedAt = DateTime.UtcNow
            });

            var vocabulary = new (string Target, string Meaning)[]
            {
                ("안녕하세요", "hello"), ("감사합니다", "thank you"),
                ("물", "water"), ("책", "book"), ("사람", "person"),
                ("학교", "school"), ("음식", "food"), ("집", "home"),
                ("친구", "friend"), ("시간", "time"), ("오늘", "today"),
                ("내일", "tomorrow"), ("사과", "apple"), ("나무", "tree"),
                ("도시", "city"), ("바다", "sea"), ("하늘", "sky"),
                ("아침", "morning"), ("저녁", "evening"), ("길", "street")
            };
            var multipleChoiceFixture = Surface.StartsWith("mc", StringComparison.Ordinal);
            for (var index = 0; index < vocabulary.Length; index++)
            {
                var wordId = $"ios-native-preview-word-{Owner}-{index}";
                db.VocabularyWords.Add(new VocabularyWord
                {
                    Id = wordId,
                    TargetLanguageTerm = vocabulary[index].Target,
                    NativeLanguageTerm = vocabulary[index].Meaning,
                    Language = "Korean"
                });
                db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                {
                    Id = $"ios-native-preview-map-{Owner}-{index}",
                    ResourceId = resourceId,
                    VocabularyWordId = wordId
                });
                db.VocabularyProgresses.Add(new VocabularyProgress
                {
                    Id = $"ios-native-preview-progress-{Owner}-{index}",
                    UserId = Owner,
                    VocabularyWordId = wordId,
                    MasteryScore = multipleChoiceFixture ? 0f : 0.45f,
                    CurrentStreak = multipleChoiceFixture ? 0f : 3f,
                    TotalAttempts = multipleChoiceFixture ? 0 : 3,
                    QuizRecognitionDemonstrations = multipleChoiceFixture ? 0 : 3,
                    NextReviewDate = DateTime.UtcNow.AddDays(-3)
                });
            }
            await db.SaveChangesAsync();
        }

        provider.GetRequiredService<IPreferencesService>()
            .Set(UserProfileRepository.ActiveProfileIdKey, Owner);
        provider.GetRequiredService<DeviceUserScopeProvider>().SetActiveUser(Owner);
        var plans = new PlanService(
            db,
            provider.GetRequiredService<IUserScopeProvider>(),
            scope.ServiceProvider.GetRequiredService<IPlanDateContext>(),
            provider.GetRequiredService<IDeterministicPlanGenerator>(),
            scope.ServiceProvider.GetRequiredService<IPlanCopyProvider>(),
            provider.GetRequiredService<ILogger<PlanService>>());
        if (await plans.GetTodayAsync() is null)
        {
            var generated = await plans.GenerateTodayAsync(
                new GenerateTodaysPlanRequest { Strategy = "deterministic" });
            if (!generated.Items.Any(item =>
                    item.ActivityType == nameof(PlanActivityType.VocabularyReview)))
                throw new InvalidOperationException(
                    "The real plan service did not produce an assigned vocabulary activity.");
        }
    }
}
#endif
