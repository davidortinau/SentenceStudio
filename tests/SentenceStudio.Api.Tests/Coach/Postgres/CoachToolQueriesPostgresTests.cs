using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Application;
using SentenceStudio.Application.Learners;
using SentenceStudio.Application.Practice;
using SentenceStudio.Application.Resources;
using SentenceStudio.Application.Skills;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The typed read contracts, against the provider they actually ship on.
/// </summary>
/// <remarks>
/// <para>
/// The coach tool suites run these queries on SQLite, which proves the ordering, the counting, and
/// the tenant predicates. What it cannot prove is that the expressions translate on Npgsql —
/// <c>EF.Functions.Like</c>, the correlated <c>COUNT</c> inside the resource projection, and the
/// <c>GROUP BY … MAX</c> that becomes a dictionary are all places where a provider difference
/// turns a passing test into a production exception on the first request.
/// </para>
/// <para>
/// A translation failure is not a subtle wrong answer either: it throws, and the tool converts it
/// into a data-access failure the learner sees as the coach being unable to read their account. So
/// this family exists to catch it here rather than there. It skips cleanly when no scratch server
/// is configured.
/// </para>
/// </remarks>
public sealed class CoachToolQueriesPostgresTests : IAsyncLifetime
{
    /// <summary>A file system the resource repository can construct against and never reads.</summary>
    private sealed class StubToolQueryFileSystem : IFileSystemService
    {
        public string AppDataDirectory { get; } =
            Path.Combine(AppContext.BaseDirectory, "coach-tool-query-tests");

        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new NotSupportedException("These tests do not read packaged files.");
    }

    private const string Owner = "pg-queries-owner";
    private const string Stranger = "pg-queries-stranger";

    private static readonly DateTime Created = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PlanDate = new(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("toolqueries", withApplicationSchema: true);

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(_harness.ConnectionString));

        services.AddSingleton<IFileSystemService, StubToolQueryFileSystem>();

        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddApplicationQueries();

        _services = services.BuildServiceProvider();

        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _services?.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private ILearnerProfileQueries Profiles => _services.GetRequiredService<ILearnerProfileQueries>();
    private ISkillProfileQueries Skills => _services.GetRequiredService<ISkillProfileQueries>();
    private ILearningResourceQueries Resources => _services.GetRequiredService<ILearningResourceQueries>();
    private IVocabularyQueries Vocabulary => _services.GetRequiredService<IVocabularyQueries>();
    private IPracticeHistoryQueries History => _services.GetRequiredService<IPracticeHistoryQueries>();

    [PostgresFact]
    public async Task Learner_profile_facts_translate_and_scope()
    {
        var facts = await Profiles.GetProfileFactsAsync(Owner);

        facts.Should().NotBeNull();
        facts!.TargetLanguage.Should().Be("Korean");
        facts.NativeLanguage.Should().Be("English");
        facts.PreferredSessionMinutes.Should().Be(20);

        (await Profiles.GetProfileFactsAsync("nobody")).Should().BeNull();
    }

    [PostgresFact]
    public async Task Skill_queries_translate_order_and_exclude_the_archive()
    {
        (await Skills.CountActiveSkillsAsync(Owner)).Should().Be(2);

        var recent = await Skills.GetRecentActiveSkillsAsync(Owner, 10);
        recent.Select(s => s.Title).Should().Equal("Newest skill", "Oldest skill");

        (await Skills.GetRecentActiveSkillsAsync(Owner, 1)).Should().ContainSingle();

        (await Skills.GetActiveSkillDetailAsync(Owner, "pg-skill-archived")).Should().BeNull();
        (await Skills.GetActiveSkillDetailAsync(Stranger, "pg-skill-new")).Should().BeNull();
        (await Skills.GetActiveSkillDetailAsync(Owner, "pg-skill-new"))!.Title.Should().Be("Newest skill");
    }

    [PostgresFact]
    public async Task Resource_queries_translate_the_correlated_vocabulary_count()
    {
        (await Resources.CountResourcesAsync(Owner)).Should().Be(2);

        var all = await Resources.GetResourceSummariesAsync(Owner);
        all.Should().HaveCount(2);
        all.Single(r => r.ResourceId == "pg-resource-a").VocabularyCount.Should().Be(1);
        all.Single(r => r.ResourceId == "pg-resource-b").VocabularyCount.Should().Be(0);

        var recent = await Resources.GetRecentResourceSummariesAsync(Owner, 1);
        recent.Should().ContainSingle().Which.ResourceId.Should().Be("pg-resource-b");

        var detail = await Resources.GetResourceSummaryAsync(Owner, "pg-resource-a");
        detail!.HasTranscript.Should().BeTrue();
        detail.IsSmartResource.Should().BeFalse();

        (await Resources.GetResourceSummaryAsync(Stranger, "pg-resource-a")).Should().BeNull();
    }

    [PostgresFact]
    public async Task Vocabulary_search_translates_the_like_filter_and_holds_the_due_embargo()
    {
        (await Vocabulary.CountTrackedWordsAsync(Owner)).Should().Be(2);
        (await Vocabulary.GetProgressFactsAsync(Owner)).Should().HaveCount(2);
        (await Vocabulary.GetDueWordTagsAsync(Owner, Now)).Should().Equal("food");

        var unfiltered = await Vocabulary.SearchUndueWordsAsync(Owner, null, 10, Now);
        unfiltered.TotalCount.Should().Be(1, "the due word is embargoed from the count as well");
        unfiltered.MatchedCount.Should().Be(
            2, "the count of what matched before the embargo is what lets a caller say one is due");
        unfiltered.Words.Should().ContainSingle().Which.WordId.Should().Be("pg-word-undue");

        var matching = await Vocabulary.SearchUndueWordsAsync(Owner, "safe", 10, Now);
        matching.Words.Should().ContainSingle();
        matching.MatchedCount.Should().Be(1, "the query narrows the matched count too, not only the page");

        var notMatching = await Vocabulary.SearchUndueWordsAsync(Owner, "no-such-term", 10, Now);
        notMatching.MatchedCount.Should().Be(0);
        notMatching.TotalCount.Should().Be(0);
        notMatching.Words.Should().BeEmpty();

        // The sanctioned route to a due word is naming it, and that route still works.
        (await Vocabulary.GetTrackedWordAsync(Owner, "pg-word-due"))!.TargetLanguageTerm.Should().Be("만기");
        (await Vocabulary.GetTrackedWordAsync(Stranger, "pg-word-due")).Should().BeNull();
    }

    [PostgresFact]
    public async Task Practice_history_queries_translate_the_grouped_last_use()
    {
        var window = await History.GetCompletionsInRangeAsync(Owner, PlanDate, PlanDate.AddDays(1));
        window.Should().ContainSingle().Which.ActivityType.Should().Be("Reading");

        (await History.CountActivityAttemptsAsync(Owner, PlanDate, PlanDate.AddDays(1))).Should().Be(1);

        var lastUsed = await History.GetResourceLastUsedAsync(Owner);
        lastUsed.Should().ContainKey("pg-resource-a");
        lastUsed["pg-resource-a"].Should().Be(PlanDate);

        (await History.GetResourceLastUsedAsync(Owner, "pg-resource-a")).Should().Be(PlanDate);
        (await History.GetResourceLastUsedAsync(Owner, "pg-resource-b")).Should().BeNull();

        (await History.GetPlanForDateAsync(Owner, PlanDate))!.Strategy.Should().Be("deterministic");
        (await History.GetPlanForDateAsync(Owner, PlanDate.AddDays(1))).Should().BeNull();
        (await History.GetPlanItemsForDateAsync(Owner, PlanDate)).Should().ContainSingle();

        (await History.GetPlanForDateAsync(Stranger, PlanDate)).Should().BeNull();
        (await History.GetCompletionsInRangeAsync(Stranger, PlanDate, PlanDate.AddDays(1))).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Every_query_fails_closed_on_the_real_provider()
    {
        (await Profiles.GetProfileFactsAsync(string.Empty)).Should().BeNull();
        (await Skills.CountActiveSkillsAsync(string.Empty)).Should().Be(0);
        (await Skills.GetRecentActiveSkillsAsync(string.Empty, 10)).Should().BeEmpty();
        (await Resources.CountResourcesAsync(string.Empty)).Should().Be(0);
        (await Resources.GetResourceSummariesAsync(string.Empty)).Should().BeEmpty();
        (await Vocabulary.CountTrackedWordsAsync(string.Empty)).Should().Be(0);
        (await Vocabulary.SearchUndueWordsAsync(string.Empty, null, 10, Now)).Words.Should().BeEmpty();
        (await History.GetResourceLastUsedAsync(string.Empty)).Should().BeEmpty();
        (await History.GetPlanItemsForDateAsync(string.Empty, PlanDate)).Should().BeEmpty();
    }

    private async Task SeedAsync()
    {
        await using var db = _harness.NewApplicationContext();

        db.UserProfiles.AddRange(
            NewProfile(Owner),
            NewProfile(Stranger));

        db.SkillProfiles.AddRange(
            new SkillProfile
            {
                Id = "pg-skill-old", Title = "Oldest skill", Language = "Korean",
                UserProfileId = Owner, CreatedAt = Created, UpdatedAt = Created
            },
            new SkillProfile
            {
                Id = "pg-skill-new", Title = "Newest skill", Language = "Korean",
                UserProfileId = Owner, CreatedAt = Created, UpdatedAt = Created.AddDays(10)
            },
            new SkillProfile
            {
                Id = "pg-skill-archived", Title = "Archived skill", Language = "Korean",
                UserProfileId = Owner, IsArchived = true, CreatedAt = Created, UpdatedAt = Created
            },
            new SkillProfile
            {
                Id = "pg-skill-stranger", Title = "Not yours", Language = "Korean",
                UserProfileId = Stranger, CreatedAt = Created, UpdatedAt = Created
            });

        db.LearningResources.AddRange(
            new LearningResource
            {
                Id = "pg-resource-a", Title = "Travel phrases", MediaType = "Podcast",
                Transcript = "Content that must stay in the database.", Tags = "travel,food",
                Language = "Korean", UserProfileId = Owner, CreatedAt = Created, UpdatedAt = Created
            },
            new LearningResource
            {
                Id = "pg-resource-b", Title = "Grammar drills", MediaType = "Article",
                Language = "Korean", UserProfileId = Owner, CreatedAt = Created, UpdatedAt = Created.AddDays(5)
            },
            new LearningResource
            {
                Id = "pg-resource-stranger", Title = "Not yours", MediaType = "Article",
                Language = "Korean", UserProfileId = Stranger, CreatedAt = Created, UpdatedAt = Created
            });

        db.VocabularyWords.AddRange(
            new VocabularyWord
            {
                Id = "pg-word-undue", TargetLanguageTerm = "안전", NativeLanguageTerm = "safe",
                Tags = "daily", Language = "Korean", CreatedAt = Created, UpdatedAt = Created
            },
            new VocabularyWord
            {
                Id = "pg-word-due", TargetLanguageTerm = "만기", NativeLanguageTerm = "due",
                Tags = "food", Language = "Korean", CreatedAt = Created, UpdatedAt = Created
            });

        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = "pg-map-1", ResourceId = "pg-resource-a", VocabularyWordId = "pg-word-undue"
        });

        db.VocabularyProgresses.AddRange(
            new VocabularyProgress
            {
                Id = "pg-progress-undue", UserId = Owner, VocabularyWordId = "pg-word-undue",
                MasteryScore = 0.4f, TotalAttempts = 5, CorrectAttempts = 4,
                NextReviewDate = Now.AddDays(3), LastPracticedAt = Now.AddDays(-2)
            },
            new VocabularyProgress
            {
                Id = "pg-progress-due", UserId = Owner, VocabularyWordId = "pg-word-due",
                MasteryScore = 0.9f, TotalAttempts = 9, CorrectAttempts = 8,
                NextReviewDate = Now.AddDays(-1), LastPracticedAt = Now.AddDays(-5)
            });

        db.DailyPlans.Add(new DailyPlan
        {
            Id = "pg-plan-1", UserProfileId = Owner, Date = PlanDate, GeneratedAtUtc = PlanDate,
            Strategy = "deterministic", CreatedAt = PlanDate, UpdatedAt = PlanDate
        });

        db.DailyPlanCompletions.Add(new DailyPlanCompletion
        {
            Id = "pg-completion-1", UserProfileId = Owner, PlanItemId = "pg-item-1",
            ActivityType = "Reading", MinutesSpent = 10, EstimatedMinutes = 12, IsCompleted = true,
            ResourceId = "pg-resource-a", Date = PlanDate, CreatedAt = PlanDate, UpdatedAt = PlanDate
        });

        db.UserActivities.Add(new UserActivity
        {
            Id = "pg-activity-1", UserProfileId = Owner, Activity = "VocabularyQuiz",
            CreatedAt = PlanDate.AddHours(9), UpdatedAt = PlanDate.AddHours(9)
        });

        await db.SaveChangesAsync();
    }

    private static UserProfile NewProfile(string id) => new()
    {
        Id = id,
        Name = "Learner",
        Email = $"{id}@example.com",
        NativeLanguage = "English",
        TargetLanguage = "Korean",
        PreferredSessionMinutes = 20,
        CreatedAt = Created
    };
}
