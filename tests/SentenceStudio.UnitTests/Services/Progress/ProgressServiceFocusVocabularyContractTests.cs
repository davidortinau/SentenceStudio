using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.DailyPlanGeneration;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.Services.Progress;

public sealed class ProgressServiceFocusVocabularyContractTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeLlmPlanGenerationService _llmPlanService;

    private const string UserA = "focus-progress-user-a";

    public ProgressServiceFocusVocabularyContractTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(UserA);
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());

        _llmPlanService = new FakeLlmPlanGenerationService();

        services.AddSingleton(preferences.Object);
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton(_llmPlanService);
        services.AddSingleton<ILlmPlanGenerationService>(_llmPlanService);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));

        services.AddScoped<UserProfileRepository>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();
        services.AddScoped<UserActivityRepository>();
        services.AddScoped<VocabularyProgressRepository>();
        services.AddScoped<VocabularyLearningContextRepository>();
        services.AddScoped<VocabularyProgressService>();
        services.AddSingleton<ProgressCacheService>();
        services.AddScoped<ProgressService>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        var db = bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.Add(new UserProfile
        {
            Id = UserA,
            Name = "Focus Progress User",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = 30,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task FocusVocabulary_ProgressServiceGenerateTodaysPlanPersistsFocusFacts_SqliteRoundTrip()
    {
        var focusIds = new List<string>
        {
            "focus-progress-word-01",
            "focus-progress-word-02",
            "focus-progress-word-03",
            "focus-progress-word-04",
            "focus-progress-word-05",
        };
        _llmPlanService.Response = CreateFocusResponse(focusIds);

        var generated = await NewService().GenerateTodaysPlanAsync();
        AssertTodaysPlanCarriesFocus(generated, focusIds);

        _provider.GetRequiredService<ProgressCacheService>().InvalidateTodaysPlan(DateTime.UtcNow.Date);
        var reloaded = await NewService().GetCachedPlanAsync(DateTime.UtcNow.Date);

        reloaded.Should().NotBeNull("legacy ProgressService must reconstruct focus facts from DailyPlanCompletion rows");
        AssertTodaysPlanCarriesFocus(reloaded!, focusIds);
    }

    private ProgressService NewService()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ProgressService>();
    }

    private static DailyPlanResponse CreateFocusResponse(List<string> focusIds)
    {
        var activities = new List<PlanActivity>
        {
            new()
            {
                ActivityType = "VocabularyReview",
                ResourceId = null,
                SkillId = null,
                EstimatedMinutes = 8,
                Priority = 1,
                VocabWordCount = focusIds.Count,
            },
            new()
            {
                ActivityType = "Cloze",
                ResourceId = null,
                SkillId = "focus-skill",
                EstimatedMinutes = 10,
                Priority = 2,
            },
        };

        foreach (var activity in activities)
        {
            FocusVocabularyContractTestHelpers.SetRequiredFocusVocabularyIds(
                activity,
                focusIds,
                "legacy ProgressService must persist focus IDs from PlanActivity through DailyPlanCompletion");
        }

        return new DailyPlanResponse
        {
            Activities = activities,
            FocusVocabularyIds = focusIds,
            Rationale = "Focus vocabulary progress service contract test",
            Narrative = new PlanNarrative(
                Resources: new List<PlanResourceSummary>(),
                VocabInsight: new VocabInsight(
                    TotalDue: focusIds.Count,
                    ReviewCount: focusIds.Count,
                    NewCount: 0,
                    AverageMastery: 0.25f,
                    StrugglingCategories: new List<TagInsight>(),
                    SampleStrugglingWords: new List<string>(),
                    PatternInsight: "Focus vocabulary progress service contract test",
                    PreviewWords: focusIds.Select(id => new PlanPreviewWord(id, $"target-{id}", $"native-{id}")).ToList()),
                Story: "Focus vocabulary progress service contract test story.",
                FocusAreas: new List<string> { "Focus vocabulary" }),
        };
    }

    private static void AssertTodaysPlanCarriesFocus(TodaysPlan plan, List<string> focusIds)
    {
        FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                plan,
                "TodaysPlan must expose focus vocabulary for the legacy dashboard path")
            .Should().Equal(focusIds);

        FocusVocabularyContractTestHelpers.GetPreviewWordIds(plan.Narrative)
            .Should().Equal(focusIds, "legacy preview words must be derived from focus IDs");

        var alignedItems = plan.Items
            .Where(item => FocusVocabularyContractTestHelpers.IsVocabularyAlignedActivity(item.ActivityType.ToString()))
            .ToList();
        alignedItems.Should().NotBeEmpty();
        foreach (var item in alignedItems)
        {
            FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                    item,
                    $"{item.ActivityType} DailyPlanItem must carry focus IDs after ProgressService reconstruction")
                .Should().Equal(focusIds);
        }
    }

    [Fact]
    public async Task FocusVocabulary_ProgressServicePersistsNarrativeAndRationaleFacts_SqliteRoundTrip()
    {
        var focusIds = new List<string>
        {
            "facts-progress-word-01",
            "facts-progress-word-02",
            "facts-progress-word-03",
            "facts-progress-word-04",
            "facts-progress-word-05",
        };
        _llmPlanService.Response = CreateFocusResponse(focusIds);

        var generated = await NewService().GenerateTodaysPlanAsync();
        generated.Should().NotBeNull();

        using var verifyScope = _provider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var planRow = await db.DailyPlans
            .FirstOrDefaultAsync(p => p.UserProfileId == UserA && p.Date == DateTime.UtcNow.Date);

        planRow.Should().NotBeNull(
            "ProgressService.InitializePlanCompletionRecordsAsync must persist a DailyPlan row");

        planRow!.FocusVocabularyFacts.Should().NotBeNullOrWhiteSpace(
            "FocusVocabularyFacts must be persisted on the DailyPlan row");

        planRow.NarrativeFacts.Should().NotBeNullOrWhiteSpace(
            "NarrativeFacts must be persisted on the DailyPlan row so the dashboard insight panel and Preview button can render after a reload or CoreSync round-trip");

        planRow.RationaleFacts.Should().NotBeNullOrWhiteSpace(
            "RationaleFacts must be persisted on the DailyPlan row so the rationale text survives a reload or CoreSync round-trip");

        planRow.NarrativeFacts.Should().Contain("\"story\"")
            .And.Contain("\"vocabInsight\"")
            .And.Contain("\"previewWords\"")
            .And.Contain("facts-progress-word-01",
                "the serialized NarrativeFacts must include the PreviewWords derived from the focus vocabulary set, in camelCase (matches PlanService wire format for CoreSync interoperability)");

        planRow.RationaleFacts.Should().Contain("\"resourceSelectionReason\"")
            .And.Contain("Focus vocabulary progress service contract test");
    }

    [Fact]
    public async Task NarrativeFacts_DeserializeViaSharedSerializer_CrossSystemCompatibility_SqliteRoundTrip()
    {
        // This test guards Critical Issue 1 from the /review pass:
        // mobile ProgressService used to serialize PascalCase, webapp PlanService
        // deserializes camelCase + case-sensitive → silently returns NULL narrative.
        // Fix: hoisted PlanFactsSerializer used by BOTH services. This test verifies
        // that whatever ProgressService writes is decipherable by the shared
        // deserializer that PlanService also uses.
        var focusIds = new List<string>
        {
            "interop-word-01",
            "interop-word-02",
            "interop-word-03",
        };
        _llmPlanService.Response = CreateFocusResponse(focusIds);

        var generated = await NewService().GenerateTodaysPlanAsync();
        generated.Should().NotBeNull();

        using var verifyScope = _provider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var planRow = await db.DailyPlans
            .FirstOrDefaultAsync(p => p.UserProfileId == UserA && p.Date == DateTime.UtcNow.Date);

        planRow.Should().NotBeNull();
        planRow!.NarrativeFacts.Should().NotBeNullOrWhiteSpace();

        // Deserialize through the SAME path PlanService uses.
        var narrativeDto = SentenceStudio.Services.Plans.PlanFactsSerializer
            .DeserializeNarrativeFacts(planRow.NarrativeFacts);

        narrativeDto.Should().NotBeNull(
            "PlanFactsSerializer.DeserializeNarrativeFacts must return a non-null DTO — otherwise the cross-system CoreSync round-trip silently drops the narrative");
        narrativeDto!.Story.Should().NotBeNullOrEmpty(
            "Story must survive serialize→deserialize (was NULL before the casing fix)");
        narrativeDto.VocabInsight.Should().NotBeNull(
            "VocabInsight must survive serialize→deserialize (was NULL before the casing fix)");
        narrativeDto.VocabInsight!.PreviewWords.Should().NotBeNullOrEmpty(
            "PreviewWords must survive serialize→deserialize (count was -1/null before the casing fix)");
        narrativeDto.VocabInsight.PreviewWords!.Should().HaveCount(focusIds.Count);
        narrativeDto.VocabInsight.PreviewWords!.Select(w => w.WordId).Should().Equal(focusIds);

        // Also verify Rationale interoperability.
        planRow.RationaleFacts.Should().NotBeNullOrWhiteSpace();
        var rationale = SentenceStudio.Services.Plans.PlanFactsSerializer
            .DeserializeRationaleFacts(planRow.RationaleFacts);
        rationale.Should().NotBeNull(
            "RationaleFacts must round-trip through the shared deserializer");
        rationale!.ResourceSelectionReason.Should().Contain("Focus vocabulary progress service contract test");

        // And focus vocabulary IDs.
        planRow.FocusVocabularyFacts.Should().NotBeNullOrWhiteSpace();
        var focus = SentenceStudio.Services.Plans.PlanFactsSerializer
            .DeserializeFocusVocabularyFacts(planRow.FocusVocabularyFacts);
        focus.Should().Equal(focusIds);
    }

    [Fact]
    public async Task NarrativeFacts_FallbackPlanDoesNotClobberPreviousFacts_SqliteRoundTrip()
    {
        // This test guards Issue 3 from the /review pass:
        // CreateFallbackPlan constructs a TodaysPlan with null Narrative
        // (exception recovery path). Without the null-coalesce guard, the
        // update branch of InitializePlanCompletionRecordsAsync would write
        // NULL over the previously-persisted facts, destroying user data.
        //
        // Setup requires forcing GenerateTodaysPlanAsync to actually reach the
        // LLM call (not short-circuit via the cache/DB reconstruction path):
        //   - Invalidate the in-memory cache (clears the cache short-circuit at L213)
        //   - Delete DailyPlanCompletion rows (forces ReconstructPlanFromDatabase
        //     to return null at L1294, so GetCachedPlanAsync returns null)
        //   - Keep the DailyPlan row so the UPDATE branch of
        //     InitializePlanCompletionRecordsAsync is the one being tested
        var focusIds = new List<string>
        {
            "preserve-word-01",
            "preserve-word-02",
            "preserve-word-03",
        };

        // 1. First generation succeeds → row gets full facts persisted.
        _llmPlanService.Response = CreateFocusResponse(focusIds);
        await NewService().GenerateTodaysPlanAsync();

        string? originalNarrativeFacts;
        string? originalRationaleFacts;
        string? originalFocusFacts;
        using (var scope1 = _provider.CreateScope())
        {
            var db1 = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row1 = await db1.DailyPlans
                .FirstOrDefaultAsync(p => p.UserProfileId == UserA && p.Date == DateTime.UtcNow.Date);
            row1.Should().NotBeNull();
            originalNarrativeFacts = row1!.NarrativeFacts;
            originalRationaleFacts = row1.RationaleFacts;
            originalFocusFacts = row1.FocusVocabularyFacts;
            originalNarrativeFacts.Should().NotBeNullOrWhiteSpace(
                "first generation must persist NarrativeFacts");
            originalRationaleFacts.Should().NotBeNullOrWhiteSpace(
                "first generation must persist RationaleFacts");
            originalFocusFacts.Should().NotBeNullOrWhiteSpace(
                "first generation must persist FocusVocabularyFacts");
        }

        // 2. Delete DailyPlanCompletion rows for today (but KEEP DailyPlan row).
        //    This forces ReconstructPlanFromDatabase to return null on the next
        //    call, which in turn forces GenerateTodaysPlanAsync to invoke the LLM.
        using (var purgeScope = _provider.CreateScope())
        {
            var purgeDb = purgeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var today = DateTime.UtcNow.Date;
            var rows = await purgeDb.DailyPlanCompletions
                .Where(c => c.UserProfileId == UserA && c.Date == today)
                .ToListAsync();
            purgeDb.DailyPlanCompletions.RemoveRange(rows);
            await purgeDb.SaveChangesAsync();
            rows.Should().NotBeEmpty("first generation must have created completion rows");

            // Sanity: confirm DailyPlan row STILL exists (no cascade delete).
            var planStillThere = await purgeDb.DailyPlans
                .FirstOrDefaultAsync(p => p.UserProfileId == UserA && p.Date == today);
            planStillThere.Should().NotBeNull(
                "DailyPlan row must survive DailyPlanCompletion deletion (no FK cascade) — " +
                "otherwise the update branch we're testing is unreachable");
            planStillThere!.NarrativeFacts.Should().Be(originalNarrativeFacts,
                "DailyPlan.NarrativeFacts must still be intact after deleting completion rows");
        }

        // 3. Force LLM failure → second generation falls back to GenerateFallbackPlanAsync,
        //    which constructs TodaysPlan with null Narrative + null Rationale + null FocusVocabularyIds.
        _llmPlanService.ThrowOnNextCall = true;
        _provider.GetRequiredService<ProgressCacheService>().InvalidateTodaysPlan(DateTime.UtcNow.Date);
        await NewService().GenerateTodaysPlanAsync();

        // 3a. Verify the throw was actually consumed (proves the LLM call happened
        //     and the fallback path was entered — without this, the test could
        //     silently pass via the cache reconstruction short-circuit).
        _llmPlanService.ThrowOnNextCall.Should().BeFalse(
            "ThrowOnNextCall should be reset to false after the LLM throws — " +
            "if it's still true, the LLM was never called and the fallback path was never exercised");

        // 4. Verify the row STILL has the original facts — they must not be clobbered.
        using var verifyScope = _provider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var planRow = await db.DailyPlans
            .FirstOrDefaultAsync(p => p.UserProfileId == UserA && p.Date == DateTime.UtcNow.Date);

        planRow.Should().NotBeNull();
        planRow!.NarrativeFacts.Should().Be(originalNarrativeFacts,
            "fallback path must NOT clobber previously-persisted NarrativeFacts with NULL");
        planRow.RationaleFacts.Should().Be(originalRationaleFacts,
            "fallback path must NOT clobber previously-persisted RationaleFacts with NULL");
        planRow.FocusVocabularyFacts.Should().Be(originalFocusFacts,
            "fallback path must NOT clobber previously-persisted FocusVocabularyFacts with NULL");
    }

    [Fact]
    public async Task FocusVocabulary_ReconstructionFallsBackToPreviewWords_WhenDailyPlanRowMissing_SqliteRoundTrip()
    {
        // GUARD: User-reported bug — "Preview plan vocabulary" shows 20 words but
        // the Vocabulary Quiz shows totally different words.
        //
        // Repro scenario (matches squad-jayne case on iOS sim 2026-06-09):
        //   • Mobile DB has DailyPlanCompletion rows (synced down) but NO DailyPlan
        //     row (out-of-order CoreSync, or generated before AddFocusVocabularyFacts).
        //   • NarrativeJson.PreviewWords IS present (20 words) — that's what powers
        //     the dashboard insight and Preview button.
        //   • Without this fallback, ReconstructPlanFromDatabase reads NULL focus
        //     facts → returns empty FocusVocabularyIds → activity URL omits them →
        //     VocabQuiz falls back to legacy selection → DIFFERENT words.
        //
        // Fix: when planFocusVocabularyIds is empty AND narrative has PreviewWords,
        // hydrate from narrative so the reconstructed plan items carry the same
        // word set the preview shows.
        var focusIds = new List<string>
        {
            "preview-fallback-word-01",
            "preview-fallback-word-02",
            "preview-fallback-word-03",
            "preview-fallback-word-04",
            "preview-fallback-word-05",
        };

        // 1) Generate normally so completion rows + narrative get written.
        _llmPlanService.Response = CreateFocusResponse(focusIds);
        await NewService().GenerateTodaysPlanAsync();

        // 2) Simulate the bug-state on disk: DailyPlan row missing entirely
        //    (just like squad-jayne's local SQLite on the iOS sim).
        using (var purgeScope = _provider.CreateScope())
        {
            var purgeDb = purgeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var today = DateTime.UtcNow.Date;
            var rows = await purgeDb.DailyPlans
                .Where(p => p.UserProfileId == UserA && p.Date == today)
                .ToListAsync();
            purgeDb.DailyPlans.RemoveRange(rows);
            await purgeDb.SaveChangesAsync();
            rows.Should().NotBeEmpty(
                "first generation must have created a DailyPlan row — otherwise this test " +
                "is not exercising the deletion path");

            // Confirm DailyPlanCompletion rows are still present (the bug requires them).
            var completions = await purgeDb.DailyPlanCompletions
                .Where(c => c.UserProfileId == UserA && c.Date == today)
                .ToListAsync();
            completions.Should().NotBeEmpty(
                "DailyPlanCompletion rows must remain for ReconstructPlanFromDatabase " +
                "to find anything to reconstruct from");
            completions.First().NarrativeJson.Should().NotBeNullOrWhiteSpace(
                "NarrativeJson must be present on completion rows — that is the data " +
                "source the fallback hydration reads from");
        }

        // 3) Force reconstruction path via cache invalidation + fresh service call.
        _provider.GetRequiredService<ProgressCacheService>().InvalidateTodaysPlan(DateTime.UtcNow.Date);
        var reconstructed = await NewService().GetCachedPlanAsync(DateTime.UtcNow.Date);

        reconstructed.Should().NotBeNull(
            "ReconstructPlanFromDatabase must produce a plan from DailyPlanCompletion rows " +
            "even when the DailyPlan row is missing");

        FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                reconstructed!,
                "reconstruction must hydrate TodaysPlan.FocusVocabularyIds from narrative " +
                "PreviewWords when DailyPlan.FocusVocabularyFacts is null — otherwise " +
                "the Vocabulary Quiz launched from the dashboard will receive empty " +
                "FocusVocabularyIds and pick different words than the Preview shows")
            .Should().BeEquivalentTo(focusIds);

        var vocabReview = reconstructed!.Items
            .FirstOrDefault(item => FocusVocabularyContractTestHelpers.IsVocabularyAlignedActivity(
                item.ActivityType.ToString()));
        vocabReview.Should().NotBeNull(
            "reconstructed plan must include a VocabularyReview item for this assertion to be meaningful");
        FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                vocabReview!,
                "VocabularyReview PlanItem must carry the same focus IDs as the " +
                "dashboard preview — this is the assertion that directly maps to the " +
                "user-reported bug ('the words in the Quiz don't match the words in the Preview')")
            .Should().BeEquivalentTo(focusIds);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private sealed class FakeLlmPlanGenerationService : ILlmPlanGenerationService
    {
        public DailyPlanResponse? Response { get; set; }

        public bool ThrowOnNextCall { get; set; }

        public Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default)
        {
            if (ThrowOnNextCall)
            {
                ThrowOnNextCall = false;
                throw new InvalidOperationException("Simulated LLM failure for fallback path test");
            }
            return Task.FromResult(Response);
        }
    }
}
