using System.Data.Common;
using System.Globalization;
using System.Resources;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// A Learning Resource's study history (issue #219) read from a real SQLite file: the facts for
/// the owner, two-learner isolation, the no-owner refusal, and the same answer after the app
/// restarts (a new provider over the same file).
/// </summary>
public sealed class ResourceStudySummaryServiceTests : IDisposable
{
    private const string Alice = "owner-alice";
    private const string Bob = "owner-bob";
    private const string AliceResource = "resource-alice";
    private const string BobResource = "resource-bob";
    private static readonly DateTime Created = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day1 = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day3 = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"ss-resource-study-{Guid.NewGuid():N}.db3");
    private readonly StatementCounter _statements = new();
    private ServiceProvider _provider;

    public ResourceStudySummaryServiceTests()
    {
        _provider = BuildProvider();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        Seed(db);
        _statements.Reset();
    }

    private ResourceStudySummaryService Service =>
        _provider.GetRequiredService<ResourceStudySummaryService>();

    [Fact]
    public async Task Summarizes_the_owners_sessions_minutes_last_session_and_word_mastery()
    {
        var summary = await Service.GetAsync(Alice, AliceResource);

        summary.Should().NotBeNull();
        AssertAliceSummary(summary!);
    }

    [Fact]
    public async Task Another_learner_cannot_read_the_owners_resource()
    {
        (await Service.GetAsync(Bob, AliceResource)).Should().BeNull();
    }

    [Fact]
    public async Task Each_learner_sees_only_their_own_rows()
    {
        // Bob's completion on Alice's resource id, his progress on Alice's words and his word
        // mapped into her resource are in the database; none reaches Alice's summary (above).
        // Bob's own resource shows only Bob's rows.
        var bob = await Service.GetAsync(Bob, BobResource);

        bob.Should().NotBeNull();
        bob!.SessionCount.Should().Be(1);
        bob.TotalMinutes.Should().Be(7);
        bob.Activities.Should().ContainSingle()
            .Which.Should().Be(new ResourceActivityStudy(PlanActivityType.Shadowing, 1, 7));
        bob.LastStudiedDate.Should().Be(Day1.Date);
        bob.WordCount.Should().Be(1);
        bob.KnownCount.Should().Be(1);
        bob.AverageMastery.Should().BeApproximately(1.0, 0.0001);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_without_an_owner_and_reads_nothing(string? owner)
    {
        (await Service.GetAsync(owner, AliceResource)).Should().BeNull();
        _statements.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_resource_never_studied_reports_no_sessions_and_unpracticed_words_as_unknown()
    {
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(Resource("resource-fresh", Alice));
            db.VocabularyWords.Add(Word("word-fresh", Alice, "새"));
            db.ResourceVocabularyMappings.Add(Map("resource-fresh", "word-fresh"));
            db.SaveChanges();
        }

        var summary = await Service.GetAsync(Alice, "resource-fresh");

        summary!.HasStudy.Should().BeFalse();
        summary.SessionCount.Should().Be(0);
        summary.LastStudiedDate.Should().BeNull();
        summary.LastActivity.Should().BeNull();
        summary.Activities.Should().BeEmpty();
        summary.WordCount.Should().Be(1);
        summary.UnknownCount.Should().Be(1);
        summary.AverageMastery.Should().Be(0);
    }

    [Fact]
    public async Task In_Practice_led_a_Known_mark_alone_does_not_make_a_word_Known()
    {
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.UserProfiles.Single(profile => profile.Id == Alice).LearningMode = LearningMode.PracticeLed;
            db.SaveChanges();
        }

        var summary = await Service.GetAsync(Alice, AliceResource);

        summary!.UnknownCount.Should().Be(1);
        summary.LearningCount.Should().Be(2);
        summary.KnownCount.Should().Be(1);
    }

    [Fact]
    public async Task Gives_the_same_answer_after_a_restart()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _provider = BuildProvider();

        var summary = await Service.GetAsync(Alice, AliceResource);

        AssertAliceSummary(summary!);
    }

    [Theory]
    [InlineData("en", "Last studied", "11/9/2026 · Vocabulary Review", "Average mastery", "45%",
        "Known", "2 of 4", "2 sessions · 15 min")]
    [InlineData("ko", "마지막 학습", null, "평균 숙련도", null, "알고 있음", "4개 중 2개", "2회 · 15분")]
    public async Task Projects_the_summary_through_the_apps_text_in_English_and_Korean(
        string cultureName, string lastLabel, string? lastValue, string averageLabel,
        string? averageValue, string knownLabel, string knownValue, string readingValue)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var resources = new ResourceManager("SentenceStudio.Resources.Strings.AppResources",
            typeof(ResourceStudySummary).Assembly);
        string Text(string key) => resources.GetString(key, culture)
            ?? throw new InvalidOperationException($"Missing text {key}");
        var summary = await Service.GetAsync(Alice, AliceResource);

        var text = ResourceStudyText.From(summary!, Text, culture);

        text.StudyEmpty.Should().BeNull();
        text.MasteryEmpty.Should().BeNull();
        text.Study.Select(line => line.Id).Should().Equal(
            "study.last", "study.sessions", "study.minutes",
            "study.activity.reading", "study.activity.vocabularyreview");
        var last = text.Study[0];
        last.Label.Should().Be(lastLabel);
        last.Value.Should().StartWith(Day3.ToString("d", culture));
        if (lastValue is not null)
            last.Value.Should().Be(lastValue);
        text.Study.Single(line => line.Id == "study.activity.reading").Value
            .Should().Be(readingValue);
        text.Mastery.Select(line => line.Id).Should().Equal(
            "mastery.average", "mastery.unknown", "mastery.learning", "mastery.known");
        text.Mastery[0].Label.Should().Be(averageLabel);
        if (averageValue is not null)
            text.Mastery[0].Value.Should().Be(averageValue);
        text.Mastery[^1].Label.Should().Be(knownLabel);
        text.Mastery[^1].Value.Should().Be(knownValue);
        text.Study.Concat(text.Mastery).Should().OnlyContain(line =>
            !string.IsNullOrWhiteSpace(line.Label) && !string.IsNullOrWhiteSpace(line.Value));
    }

    [Fact]
    public void Shows_why_a_group_is_empty()
    {
        var text = ResourceStudyText.From(
            new ResourceStudySummary("r", 0, 0, [], null, null, 0, 0, 0, 0, 0),
            key => key, CultureInfo.InvariantCulture);

        text.Study.Should().BeEmpty();
        text.StudyEmpty.Should().Be("ResourceEdit_StudyNotYet");
        text.Mastery.Should().BeEmpty();
        text.MasteryEmpty.Should().Be("ResourceEdit_MasteryNoWords");
    }

    private static void AssertAliceSummary(ResourceStudySummary summary)
    {
        summary.ResourceId.Should().Be(AliceResource);
        // Reading completed (10) and Reading begun (5); Vocabulary Review completed (0);
        // the never-started Shadowing plan item is not a session.
        summary.SessionCount.Should().Be(3);
        summary.TotalMinutes.Should().Be(15);
        summary.Activities.Should().Equal(
            new ResourceActivityStudy(PlanActivityType.Reading, 2, 15),
            new ResourceActivityStudy(PlanActivityType.VocabularyReview, 1, 0));
        summary.LastStudiedDate.Should().Be(Day3.Date);
        summary.LastActivity.Should().Be(PlanActivityType.VocabularyReview);
        // Four of Alice's words; Bob's word mapped into her resource is not hers.
        summary.WordCount.Should().Be(4);
        summary.UnknownCount.Should().Be(1);
        summary.LearningCount.Should().Be(1);
        // Learner-led (the default): the word she marked Known counts with the practiced one.
        summary.KnownCount.Should().Be(2);
        // (0.4 + 0.5 + 0.9 + 0) / 4; Bob's mastery 1.0 on her words does not count.
        summary.AverageMastery.Should().BeApproximately(0.45, 0.0001);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite($"Data Source={_databasePath}")
                .AddInterceptors(_statements)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(builder => builder.ClearProviders());
        services.AddSingleton<ResourceStudySummaryService>();
        return services.BuildServiceProvider();
    }

    private static void Seed(ApplicationDbContext db)
    {
        db.UserProfiles.AddRange(Profile(Alice), Profile(Bob));
        db.LearningResources.AddRange(
            Resource(AliceResource, Alice), Resource("resource-alice-other", Alice),
            Resource(BobResource, Bob));
        db.VocabularyWords.AddRange(
            Word("a-learning", Alice, "사과"), Word("a-marked", Alice, "배"),
            Word("a-known", Alice, "포도"), Word("a-unknown", Alice, "딸기"),
            Word("b-word", Bob, "수박"));
        db.ResourceVocabularyMappings.AddRange(
            Map(AliceResource, "a-learning"), Map(AliceResource, "a-marked"),
            Map(AliceResource, "a-known"), Map(AliceResource, "a-unknown"),
            // A foreign word mapped into Alice's resource (one owner per word): not hers.
            Map(AliceResource, "b-word"),
            Map(BobResource, "b-word"));
        db.VocabularyProgresses.AddRange(
            Progress(Alice, "a-learning", 0.4f),
            new VocabularyProgress
            {
                Id = Guid.NewGuid().ToString(), UserId = Alice, VocabularyWordId = "a-marked",
                MasteryScore = 0.5f, IsUserDeclared = true,
                VerificationState = VerificationStatus.Pending,
            },
            Progress(Alice, "a-known", 0.9f, productionInStreak: 2),
            // Bob's progress on Alice's words, and on his own word.
            Progress(Bob, "a-learning", 1f, productionInStreak: 3),
            Progress(Bob, "a-unknown", 1f, productionInStreak: 3),
            Progress(Bob, "b-word", 1f, productionInStreak: 3));
        db.DailyPlanCompletions.AddRange(
            Completion(Alice, AliceResource, "Reading", Day1, minutes: 10, completed: true),
            Completion(Alice, AliceResource, "Reading", Day2, minutes: 5, completed: false,
                archived: true),
            Completion(Alice, AliceResource, "VocabularyReview", Day3, minutes: 0, completed: true),
            // A plan item never started is not a session, even when it is the latest row.
            Completion(Alice, AliceResource, "Shadowing", Day3.AddDays(1), minutes: 0,
                completed: false),
            Completion(Alice, "resource-alice-other", "Writing", Day3, minutes: 20, completed: true),
            // Bob's row carrying Alice's resource id (it must never count for her).
            Completion(Bob, AliceResource, "Reading", Day3, minutes: 30, completed: true),
            Completion(Bob, BobResource, "Shadowing", Day1, minutes: 7, completed: true));
        db.SaveChanges();
    }

    private static UserProfile Profile(string id) => new()
    {
        Id = id, Name = id, Email = $"{id}@example.com", NativeLanguage = "English",
        TargetLanguage = "Korean", CreatedAt = Created,
    };

    private static LearningResource Resource(string id, string owner) => new()
    {
        Id = id, Title = id, MediaType = "Video", Language = "Korean",
        UserProfileId = owner, CreatedAt = Created, UpdatedAt = Created,
    };

    private static VocabularyWord Word(string id, string owner, string term) => new()
    {
        Id = id, UserProfileId = owner, TargetLanguageTerm = term, NativeLanguageTerm = term,
        Language = "Korean", CreatedAt = Created, UpdatedAt = Created,
    };

    private static ResourceVocabularyMapping Map(string resourceId, string wordId) => new()
    {
        Id = Guid.NewGuid().ToString(), ResourceId = resourceId, VocabularyWordId = wordId,
    };

    private static VocabularyProgress Progress(string owner, string wordId, float mastery,
        int productionInStreak = 0) => new()
    {
        Id = Guid.NewGuid().ToString(), UserId = owner, VocabularyWordId = wordId,
        MasteryScore = mastery, ProductionInStreak = productionInStreak, TotalAttempts = 4,
    };

    private static DailyPlanCompletion Completion(string owner, string resourceId,
        string activity, DateTime date, int minutes, bool completed, bool archived = false) => new()
    {
        Id = Guid.NewGuid().ToString(), UserProfileId = owner, ResourceId = resourceId,
        PlanItemId = Guid.NewGuid().ToString(), ActivityType = activity, Date = date,
        MinutesSpent = minutes, EstimatedMinutes = 10, IsCompleted = completed,
        IsArchived = archived, CompletedAt = completed ? date.AddHours(9) : null,
        CreatedAt = date, UpdatedAt = date.AddHours(9),
    };

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_databasePath + suffix);
    }

    /// <summary>Counts every statement that reaches the database.</summary>
    private sealed class StatementCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}
