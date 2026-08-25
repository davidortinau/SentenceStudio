using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Application;
using SentenceStudio.Application.Learners;
using SentenceStudio.Application.Practice;
using SentenceStudio.Application.Resources;
using SentenceStudio.Application.Skills;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// The empty-owner behaviour of every typed read contract.
/// </summary>
/// <remarks>
/// <para>
/// These queries exist to be called by hosts where there is no ambient learner — the API serves
/// everyone, so "who is asking" arrives per request and can be missing. The dangerous version of
/// missing is not an exception; it is a query that quietly drops its <c>WHERE</c> clause and
/// returns the whole table, because that reads as success everywhere upstream.
/// </para>
/// <para>
/// So every method here is asserted twice: it returns the empty answer for its type, and it runs
/// no statement at all. The second assertion is the one that matters — a method that filtered on
/// an empty string would also return nothing today, and would start returning rows the moment
/// someone stored an empty owner.
/// </para>
/// </remarks>
public sealed class ApplicationQueryFailClosedTests : IDisposable
{
    private const string Owner = "queries-owner";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly SelectCommandCounter _queries = new();

    public ApplicationQueryFailClosedTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(_connection)
                .AddInterceptors(_queries)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());

        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddApplicationQueries();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        Seed(db);
        _queries.Reset();
    }

    private ILearnerProfileQueries Profiles => _provider.GetRequiredService<ILearnerProfileQueries>();
    private ISkillProfileQueries Skills => _provider.GetRequiredService<ISkillProfileQueries>();
    private ILearningResourceQueries Resources => _provider.GetRequiredService<ILearningResourceQueries>();
    private IVocabularyQueries Vocabulary => _provider.GetRequiredService<IVocabularyQueries>();
    private IPracticeHistoryQueries History => _provider.GetRequiredService<IPracticeHistoryQueries>();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Learner_profile_queries_fail_closed_without_an_owner(string owner)
    {
        (await Profiles.GetProfileFactsAsync(owner)).Should().BeNull();
        _queries.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Skill_queries_fail_closed_without_an_owner(string owner)
    {
        (await Skills.CountActiveSkillsAsync(owner)).Should().Be(0);
        (await Skills.GetRecentActiveSkillsAsync(owner, 20)).Should().BeEmpty();
        (await Skills.GetActiveSkillDetailAsync(owner, "any-skill")).Should().BeNull();
        _queries.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resource_queries_fail_closed_without_an_owner(string owner)
    {
        (await Resources.CountResourcesAsync(owner)).Should().Be(0);
        (await Resources.GetResourceSummariesAsync(owner)).Should().BeEmpty();
        (await Resources.GetRecentResourceSummariesAsync(owner, 20)).Should().BeEmpty();
        (await Resources.GetResourceSummaryAsync(owner, "any-resource")).Should().BeNull();
        _queries.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Vocabulary_queries_fail_closed_without_an_owner(string owner)
    {
        var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

        (await Vocabulary.CountTrackedWordsAsync(owner)).Should().Be(0);
        (await Vocabulary.GetProgressFactsAsync(owner)).Should().BeEmpty();
        (await Vocabulary.GetDueWordTagsAsync(owner, now)).Should().BeEmpty();

        var page = await Vocabulary.SearchUndueWordsAsync(owner, null, 10, now);
        page.TotalCount.Should().Be(0);
        page.Words.Should().BeEmpty();

        (await Vocabulary.GetTrackedWordAsync(owner, "any-word")).Should().BeNull();
        _queries.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Practice_history_queries_fail_closed_without_an_owner(string owner)
    {
        var from = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

        (await History.GetCompletionsInRangeAsync(owner, from, to)).Should().BeEmpty();
        (await History.CountActivityAttemptsAsync(owner, from, to)).Should().Be(0);
        (await History.GetResourceLastUsedAsync(owner)).Should().BeEmpty();
        (await History.GetResourceLastUsedAsync(owner, "any-resource")).Should().BeNull();
        (await History.GetPlanForDateAsync(owner, from)).Should().BeNull();
        (await History.GetPlanItemsForDateAsync(owner, from)).Should().BeEmpty();
        _queries.Count.Should().Be(0);
    }

    /// <summary>
    /// The mirror image: with a real owner the same calls do read, and do return the seeded rows.
    /// Without this, a contract that returned empty for everyone would satisfy every test above.
    /// </summary>
    [Fact]
    public async Task Every_query_returns_the_owners_rows_when_the_owner_is_present()
    {
        var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        var planDate = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);

        (await Profiles.GetProfileFactsAsync(Owner))!.TargetLanguage.Should().Be("Korean");

        (await Skills.CountActiveSkillsAsync(Owner)).Should().Be(1);
        (await Skills.GetRecentActiveSkillsAsync(Owner, 20)).Should().ContainSingle();
        (await Skills.GetActiveSkillDetailAsync(Owner, "skill-1")).Should().NotBeNull();

        (await Resources.CountResourcesAsync(Owner)).Should().Be(1);
        (await Resources.GetResourceSummariesAsync(Owner)).Should().ContainSingle();
        (await Resources.GetRecentResourceSummariesAsync(Owner, 20)).Should().ContainSingle();
        (await Resources.GetResourceSummaryAsync(Owner, "resource-1"))!.HasTranscript.Should().BeTrue();

        (await Vocabulary.CountTrackedWordsAsync(Owner)).Should().Be(1);
        (await Vocabulary.GetProgressFactsAsync(Owner)).Should().ContainSingle();
        (await Vocabulary.GetDueWordTagsAsync(Owner, now)).Should().ContainSingle();
        (await Vocabulary.GetTrackedWordAsync(Owner, "word-1"))!.TargetLanguageTerm.Should().Be("사과");

        (await History.GetCompletionsInRangeAsync(Owner, planDate, planDate.AddDays(1)))
            .Should().ContainSingle();
        (await History.CountActivityAttemptsAsync(Owner, planDate, planDate.AddDays(1))).Should().Be(1);
        (await History.GetResourceLastUsedAsync(Owner)).Should().ContainKey("resource-1");
        (await History.GetResourceLastUsedAsync(Owner, "resource-1")).Should().NotBeNull();
        (await History.GetPlanForDateAsync(Owner, planDate))!.Strategy.Should().Be("deterministic");
        (await History.GetPlanItemsForDateAsync(Owner, planDate)).Should().ContainSingle();
    }

    private static void Seed(ApplicationDbContext db)
    {
        var created = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var planDate = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);

        db.UserProfiles.Add(new UserProfile
        {
            Id = Owner,
            Name = "Owner",
            Email = "owner@example.com",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = 20,
            CreatedAt = created
        });

        db.SkillProfiles.Add(new SkillProfile
        {
            Id = "skill-1",
            Title = "Ordering food",
            Language = "Korean",
            UserProfileId = Owner,
            CreatedAt = created,
            UpdatedAt = created
        });

        db.LearningResources.Add(new LearningResource
        {
            Id = "resource-1",
            Title = "Travel phrases",
            MediaType = "Podcast",
            Transcript = "Content that must stay in the database.",
            Language = "Korean",
            UserProfileId = Owner,
            CreatedAt = created,
            UpdatedAt = created
        });

        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = "word-1",
            TargetLanguageTerm = "사과",
            NativeLanguageTerm = "apple",
            Tags = "food",
            Language = "Korean",
            CreatedAt = created,
            UpdatedAt = created
        });

        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = "progress-1",
            UserId = Owner,
            VocabularyWordId = "word-1",
            MasteryScore = 0.5f,
            TotalAttempts = 4,
            CorrectAttempts = 3,
            NextReviewDate = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc)
        });

        db.DailyPlans.Add(new DailyPlan
        {
            Id = "plan-1",
            UserProfileId = Owner,
            Date = planDate,
            GeneratedAtUtc = planDate,
            Strategy = "deterministic",
            CreatedAt = planDate,
            UpdatedAt = planDate
        });

        db.DailyPlanCompletions.Add(new DailyPlanCompletion
        {
            Id = "completion-1",
            UserProfileId = Owner,
            PlanItemId = "item-1",
            ActivityType = "Reading",
            MinutesSpent = 10,
            EstimatedMinutes = 10,
            IsCompleted = true,
            ResourceId = "resource-1",
            Date = planDate,
            CreatedAt = planDate,
            UpdatedAt = planDate
        });

        db.UserActivities.Add(new UserActivity
        {
            Id = "activity-1",
            UserProfileId = Owner,
            Activity = "VocabularyQuiz",
            CreatedAt = planDate.AddHours(9),
            UpdatedAt = planDate.AddHours(9)
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>Counts every statement that reaches the database, so "no query ran" is provable.</summary>
    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecuting(command, eventData, result);
        }
    }
}
