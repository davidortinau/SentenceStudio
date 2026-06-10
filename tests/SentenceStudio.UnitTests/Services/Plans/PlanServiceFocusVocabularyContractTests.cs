using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.Services.Plans;

public sealed class PlanServiceFocusVocabularyContractTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeScope _scope;
    private readonly FakeDateContext _date;
    private readonly FakeDeterministicGenerator _generator;

    private const string UserA = "focus-user-a";

    public PlanServiceFocusVocabularyContractTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));

        _scope = new FakeScope(UserA);
        _date = new FakeDateContext(new DateOnly(2026, 6, 8), TimeZoneInfo.Utc);
        _generator = new FakeDeterministicGenerator();

        services.AddSingleton<IUserScopeProvider>(_scope);
        services.AddSingleton<IPlanDateContext>(_date);
        services.AddSingleton<IDeterministicPlanGenerator>(_generator);
        services.AddSingleton<IPlanCopyProvider, EnglishPlanCopyProvider>();
        services.AddScoped<IPlanService, PlanService>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        var db = bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task FocusVocabulary_PlanServiceGenerateTodayPersistsFocusFacts_SqliteRoundTrip()
    {
        var focusIds = CreateFocusIds();
        _generator.SetSkeleton(CreateFocusSkeleton(focusIds));

        var generated = await NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());
        AssertPlanDtoCarriesFocus(generated, focusIds);

        var reloaded = await NewService().GetTodayAsync();
        reloaded.Should().NotBeNull("the focus vocabulary facts must survive a database reload");
        AssertPlanDtoCarriesFocus(reloaded!, focusIds);
    }

    [Fact]
    public async Task FocusVocabulary_PlanServicePreviewWordsComeFromFocusIds()
    {
        var focusIds = CreateFocusIds();
        _generator.SetSkeleton(CreateFocusSkeleton(focusIds));

        var generated = await NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());

        var planFocusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            generated,
            "TodaysPlanDto must expose the canonical focus vocabulary set");
        var previewIds = FocusVocabularyContractTestHelpers.GetPreviewWordIds(generated.Narrative);

        previewIds.Should().Equal(planFocusIds,
            "PlanService must serialize preview words as a projection of focus IDs, not as a separate selection");
    }

    [Fact]
    public async Task FocusVocabulary_BuildDtoFallsBackToPreviewWords_WhenDailyPlanFocusFactsAreNull()
    {
        // GUARD: User-reported bug — the dashboard "Preview plan vocabulary"
        // shows 20 words but the Vocabulary Quiz shows totally different words.
        //
        // Server-side bug condition:
        //   • DailyPlan row exists with FocusVocabularyFacts = NULL (legacy row
        //     written before the AddFocusVocabularyFacts migration, OR LLM
        //     response that did not propagate FocusVocabularyIds).
        //   • NarrativeFacts contains PreviewWords (powers the dashboard insight
        //     and the Preview button).
        //   • Without fallback: PlanService.BuildDto returns empty
        //     FocusVocabularyIds → activity URL omits them → VocabQuiz / Cloze /
        //     VideoWatching fall back to legacy selection → different words.
        //
        // Fix: when DailyPlan.FocusVocabularyFacts is null/empty AND narrative
        // has PreviewWords, hydrate from the narrative so the API DTO and every
        // vocabulary-aligned PlanItem carry the same set the preview shows.
        var previewWordIds = new List<string>
        {
            "server-fallback-word-01",
            "server-fallback-word-02",
            "server-fallback-word-03",
            "server-fallback-word-04",
            "server-fallback-word-05",
        };

        var today = _date.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var narrativeFactsJson = PlanFactsSerializer.SerializeNarrativeFacts(new NarrativeFactsDto
        {
            VocabInsight = new NarrativeVocabInsightFactsDto
            {
                TotalDue = previewWordIds.Count,
                ReviewCount = previewWordIds.Count,
                NewCount = 0,
                AverageMastery = 0.25f,
                StrugglingCategories = new List<NarrativeTagInsightFactsDto>(),
                SampleStrugglingWords = new List<string>(),
                PatternInsight = "Server-side fallback regression test",
                PreviewWords = previewWordIds
                    .Select(id => new NarrativePreviewWordFactsDto { WordId = id, TargetTerm = $"target-{id}", NativeTerm = $"native-{id}" })
                    .ToList(),
            },
            Story = "Server-side fallback regression test story.",
            FocusAreas = new List<string> { "Server-side fallback" },
        });

        using (var seed = _provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.DailyPlans.Add(new DailyPlan
            {
                Id = Guid.NewGuid().ToString(),
                UserProfileId = UserA,
                Date = today,
                GeneratedAtUtc = today,
                Strategy = "deterministic",
                FocusVocabularyFacts = null, // ← the exact bug condition
                NarrativeFacts = narrativeFactsJson,
                RationaleFacts = null,
            });

            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = Guid.NewGuid().ToString(),
                UserProfileId = UserA,
                Date = today,
                PlanItemId = "vocab-review-fallback",
                ActivityType = "VocabularyReview",
                Priority = 1,
                EstimatedMinutes = 8,
                IsCompleted = false,
            });
            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = Guid.NewGuid().ToString(),
                UserProfileId = UserA,
                Date = today,
                PlanItemId = "writing-fallback",
                ActivityType = "Writing",
                Priority = 2,
                EstimatedMinutes = 12,
                IsCompleted = false,
            });

            await db.SaveChangesAsync();
        }

        var plan = await NewService().GetTodayAsync();

        plan.Should().NotBeNull(
            "GetTodayAsync must return the seeded plan even when FocusVocabularyFacts is null");

        FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                plan!,
                "BuildDto must hydrate TodaysPlanDto.FocusVocabularyIds from narrative " +
                "PreviewWords when DailyPlan.FocusVocabularyFacts is null — otherwise " +
                "the dashboard preview and activities see different word sets")
            .Should().BeEquivalentTo(previewWordIds);

        var vocabReview = plan!.Items
            .FirstOrDefault(item => FocusVocabularyContractTestHelpers.IsVocabularyAlignedActivity(item.ActivityType));
        vocabReview.Should().NotBeNull(
            "the seeded VocabularyReview completion must produce a PlanItemDto for this assertion to be meaningful");
        FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                vocabReview!,
                "vocabulary-aligned PlanItemDto must carry the hydrated focus IDs so " +
                "the activity launched from the dashboard receives the same words the " +
                "user previewed")
            .Should().BeEquivalentTo(previewWordIds);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgresql")]
    public void FocusVocabulary_PersistenceModelIncludesFocusFacts_ForSqliteAndPostgreSql(string providerName)
    {
        using var context = CreateModelOnlyContext(providerName);

        var dailyPlanEntity = context.Model.FindEntityType(typeof(DailyPlan));
        var completionEntity = context.Model.FindEntityType(typeof(DailyPlanCompletion));

        dailyPlanEntity.Should().NotBeNull();
        completionEntity.Should().NotBeNull();
        FocusVocabularyContractTestHelpers.FindFocusVocabularyEfProperty(dailyPlanEntity)
            .Should().NotBeNull("DailyPlan is the canonical Phase 1 focus vocabulary storage row for {0}", providerName);
        FocusVocabularyContractTestHelpers.FindFocusVocabularyEfProperty(completionEntity)
            .Should().BeNull("Captain's directive makes DailyPlan the synced canonical focus store; DailyPlanCompletion remains personal progress only on {0}", providerName);
    }

    private IPlanService NewService()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPlanService>();
    }

    private static ApplicationDbContext CreateModelOnlyContext(string providerName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        if (providerName == "sqlite")
        {
            options.UseSqlite("Data Source=:memory:");
        }
        else
        {
            options.UseNpgsql("Host=localhost;Database=sentencestudio_model_only;Username=postgres;Password=postgres");
        }

        return new ApplicationDbContext(options.Options);
    }

    private static List<string> CreateFocusIds() => new()
    {
        "focus-service-word-01",
        "focus-service-word-02",
        "focus-service-word-03",
        "focus-service-word-04",
        "focus-service-word-05",
    };

    private static PlanSkeleton CreateFocusSkeleton(List<string> focusIds)
    {
        var activities = new List<PlannedActivity>
        {
            new()
            {
                ActivityType = "VocabularyReview",
                ResourceId = null,
                SkillId = null,
                EstimatedMinutes = 8,
                Priority = 1,
                Rationale = "Focus vocabulary review",
            },
            new()
            {
                ActivityType = "Writing",
                ResourceId = "focus-resource",
                SkillId = "focus-skill",
                EstimatedMinutes = 12,
                Priority = 2,
                Rationale = "Focus vocabulary production",
            },
        };

        foreach (var activity in activities)
        {
            FocusVocabularyContractTestHelpers.SetRequiredFocusVocabularyIds(
                activity,
                focusIds,
                "PlanService must persist per-item focus IDs from PlannedActivity");
        }

        var skeleton = new PlanSkeleton
        {
            Activities = activities,
            TotalMinutes = activities.Sum(activity => activity.EstimatedMinutes),
            ResourceSelectionReason = "Focus vocabulary contract test",
            Narrative = new PlanNarrative(
                Resources: new List<PlanResourceSummary>(),
                VocabInsight: new VocabInsight(
                    TotalDue: focusIds.Count,
                    ReviewCount: focusIds.Count,
                    NewCount: 0,
                    AverageMastery: 0.25f,
                    StrugglingCategories: new List<TagInsight>(),
                    SampleStrugglingWords: new List<string>(),
                    PatternInsight: "Focus vocabulary contract test",
                    PreviewWords: focusIds.Select(id => new PlanPreviewWord(id, $"target-{id}", $"native-{id}")).ToList()),
                Story: "Focus vocabulary contract test story.",
                FocusAreas: new List<string> { "Focus vocabulary" }),
        };

        FocusVocabularyContractTestHelpers.SetRequiredFocusVocabularyIds(
            skeleton,
            focusIds,
            "PlanService must persist the plan-level focus vocabulary set");
        return skeleton;
    }

    private static void AssertPlanDtoCarriesFocus(TodaysPlanDto plan, List<string> focusIds)
    {
        var planFocusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            plan,
            "TodaysPlanDto is the API/mobile contract for the canonical focus set");
        planFocusIds.Should().Equal(focusIds);

        FocusVocabularyContractTestHelpers.GetPreviewWordIds(plan.Narrative)
            .Should().Equal(focusIds, "preview words must round-trip from the focus set");

        var alignedItems = plan.Items
            .Where(item => FocusVocabularyContractTestHelpers.IsVocabularyAlignedActivity(item.ActivityType))
            .ToList();
        alignedItems.Should().NotBeEmpty();
        foreach (var item in alignedItems)
        {
            FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                    item,
                    $"{item.ActivityType} PlanItemDto must carry focus IDs after persistence")
                .Should().Equal(focusIds);
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private sealed class FakeScope : IUserScopeProvider
    {
        public FakeScope(string userProfileId) => UserProfileId = userProfileId;
        public string UserProfileId { get; private set; }
        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = UserProfileId;
            return !string.IsNullOrWhiteSpace(userProfileId);
        }
    }

    private sealed class FakeDateContext : IPlanDateContext
    {
        public FakeDateContext(DateOnly localDate, TimeZoneInfo timeZone)
        {
            UserLocalDate = localDate;
            TimeZone = timeZone;
            UtcNow = localDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        }

        public DateOnly UserLocalDate { get; }
        public DateTime UtcNow { get; }
        public TimeZoneInfo TimeZone { get; }
        public DateOnly ToUserLocal(DateTime utc) => DateOnly.FromDateTime(utc);
        public DateTime ToUtcMidnight(DateOnly userLocal) => userLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    }

    private sealed class FakeDeterministicGenerator : IDeterministicPlanGenerator
    {
        private PlanSkeleton? _skeleton;

        public void SetSkeleton(PlanSkeleton skeleton) => _skeleton = skeleton;

        public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
        {
            return Task.FromResult(_skeleton);
        }
    }
}
