using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Runtime;using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Application;
using SentenceStudio.Application.Learners;
using SentenceStudio.Application.Practice;
using SentenceStudio.Application.Resources;
using SentenceStudio.Application.Skills;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// A user scope that a test can point at one learner, or leave empty so the
/// scope fails closed exactly as the API scope does.
/// </summary>
internal sealed class FakeUserScopeProvider : IUserScopeProvider
{
    public FakeUserScopeProvider(string? userProfileId = null)
    {
        CurrentUserProfileId = userProfileId;
    }

    public string? CurrentUserProfileId { get; set; }

    public string UserProfileId => string.IsNullOrWhiteSpace(CurrentUserProfileId)
        ? throw new UnauthorizedAccessException("No authenticated user profile is present on the current request.")
        : CurrentUserProfileId;

    public bool TryGetUserProfileId(out string userProfileId)
    {
        userProfileId = CurrentUserProfileId ?? string.Empty;
        return userProfileId.Length > 0;
    }
}

/// <summary>
/// Counts the database commands a tool runs, and keeps their text. A tool must resolve the user
/// scope before it runs any command, so a failed scope check must leave this count at zero.
/// </summary>
/// <remarks>
/// The recorded SQL is what makes a projection claim checkable. "The transcript is not in the
/// answer" is a property of the result object and can be satisfied by loading the column and
/// dropping it; "the transcript is not in the SELECT" is a property of the query, and only the
/// second one is true of a column that never left the database.
/// </remarks>
internal sealed class CoachCommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    private readonly List<string> _texts = [];
    private int _count;

    public int CommandCount => Volatile.Read(ref _count);

    /// <summary>The SQL of every command executed since the last reset, oldest first.</summary>
    public IReadOnlyList<string> CommandTexts
    {
        get { lock (_texts) { return _texts.ToArray(); } }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        lock (_texts) { _texts.Clear(); }
    }

    private void Record(System.Data.Common.DbCommand command)
    {
        Interlocked.Increment(ref _count);
        lock (_texts) { _texts.Add(command.CommandText); }
    }

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }
}

/// <summary>
/// An in-memory database with two learners, so every tool test can prove that
/// one learner never reads the data of the other learner.
/// </summary>
/// <remarks>
/// The tools no longer hold a <c>DbContext</c>; they hold the application query contracts, and
/// those resolve to the same repositories the app screens use. So the fixture builds a small
/// container rather than handing a context around. Every context in it — the one the seeders write
/// through and the ones the repositories open per call — shares one SQLite connection and one
/// command interceptor, which is what keeps the "no query before the scope check" assertions
/// meaningful now that the query runs one layer further away.
/// </remarks>
internal sealed class CoachToolTestFixture : IDisposable
{
    public const string UserA = "user-a";
    public const string UserB = "user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public CoachToolTestFixture(DateTime? utcNow = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        Commands = new CoachCommandCounter();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(Commands)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        Db = new ApplicationDbContext(options);
        Db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddDbContext<ApplicationDbContext>(o => o
            .UseSqlite(_connection)
            .AddInterceptors(Commands)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<SentenceStudio.Abstractions.IFileSystemService, StubFileSystemService>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddApplicationQueries();
        _services = services.BuildServiceProvider();

        Now = utcNow ?? new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        Dates = new PlanDateContext(TimeZoneInfo.Utc, () => Now);
        Scope = new FakeUserScopeProvider(UserA);
    }

    public ApplicationDbContext Db { get; }
    public CoachCommandCounter Commands { get; }
    public IPlanDateContext Dates { get; }
    public FakeUserScopeProvider Scope { get; }
    public DateTime Now { get; }
    public DateOnly Today => DateOnly.FromDateTime(Now);

    private ILearnerProfileQueries Profiles => _services.GetRequiredService<ILearnerProfileQueries>();
    private ISkillProfileQueries Skills => _services.GetRequiredService<ISkillProfileQueries>();
    private ILearningResourceQueries Resources => _services.GetRequiredService<ILearningResourceQueries>();
    private IVocabularyQueries Vocabulary => _services.GetRequiredService<IVocabularyQueries>();
    private IPracticeHistoryQueries History => _services.GetRequiredService<IPracticeHistoryQueries>();

    public LearnerProfileSummaryTool ProfileTool => new(Scope, Profiles, Dates);
    public PracticeBalanceTool BalanceTool => new(Scope, History, Dates);
    public VocabularyDueSummaryTool VocabularyTool => new(Scope, Vocabulary, Dates);
    public ResourceCatalogTool ResourceTool => new(Scope, Resources, History, Dates);

    // --- Sam read tools ---
    // Fresh instance per access, matching the scoped DI lifetime the API gives them, so a test
    // cannot accidentally carry state from one call into the next.

    public SentenceStudio.Api.Coach.Tools.SamTools.VocabularySearchTool VocabularySearchTool =>
        new(Scope, Vocabulary, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.VocabularySearchTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.VocabularyWordDetailTool VocabularyWordDetailTool =>
        new(Scope, Vocabulary, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.VocabularyWordDetailTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.SkillListTool SkillListTool =>
        new(Scope, Skills, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.SkillListTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.SkillDetailTool SkillDetailTool =>
        new(Scope, Skills, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.SkillDetailTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.LearningResourceListTool LearningResourceListTool =>
        new(Scope, Resources, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.LearningResourceListTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.LearningResourceDetailTool LearningResourceDetailTool =>
        new(Scope, Resources, History, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.LearningResourceDetailTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.CurrentProfileSummaryTool CurrentProfileSummaryTool =>
        new(Scope, Profiles, Vocabulary, Skills, Resources, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.CurrentProfileSummaryTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.LearnerSettingsSummaryTool LearnerSettingsSummaryTool =>
        new(Scope, Profiles, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.LearnerSettingsSummaryTool>.Instance);

    public SentenceStudio.Api.Coach.Tools.SamTools.CurrentPlanSummaryTool CurrentPlanSummaryTool =>
        new(Scope, History, Dates, NullLogger<SentenceStudio.Api.Coach.Tools.SamTools.CurrentPlanSummaryTool>.Instance);

    /// <summary>
    /// The plan preview tool, wired to a planner stub that returns <paramref name="skeleton"/>.
    /// </summary>
    /// <remarks>
    /// The preview is a registered read and states a scope, so it belongs in every sweep over the
    /// read surface. It needs a planner rather than a table, which is the only reason it is
    /// constructed differently from the rest — and the reason it was quietly missing from the
    /// scope sweep until now. Defaulting to a two-activity plan means a caller that just wants
    /// "the preview tool" gets one whose answer has rows in it.
    /// </remarks>
    public PreviewPracticePlanTool PreviewTool(PlanSkeleton? skeleton = null) =>
        new(Scope,
            new StubPlanGenerator(skeleton ?? DefaultPreviewSkeleton()),
            new DefaultCoachPlanPreviewFailureAdapter(),
            Dates);

    /// <summary>A minimal feasible plan: two activities, no learner content.</summary>
    public static PlanSkeleton DefaultPreviewSkeleton() => new()
    {
        Activities =
        [
            new PlannedActivity
            {
                ActivityType = "VocabularyReview",
                EstimatedMinutes = 5,
                Priority = 1,
                Rationale = "Due words"
            },
            new PlannedActivity
            {
                ActivityType = "Reading",
                EstimatedMinutes = 5,
                Priority = 2,
                Rationale = "Comprehension"
            }
        ],
        TotalMinutes = 10
    };

    private sealed class StubPlanGenerator(PlanSkeleton? skeleton) : IDeterministicPlanGenerator
    {
        public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default) =>
            Task.FromResult(skeleton);

        public Task<PlanSkeleton?> GenerateAsync(PlanBuildRequest request, CancellationToken ct = default) =>
            Task.FromResult(skeleton);
    }

    private sealed class StubFileSystemService : SentenceStudio.Abstractions.IFileSystemService
    {
        public string AppDataDirectory => AppContext.BaseDirectory;

        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    /// <summary>Seeds a skill profile owned by the given learner.</summary>
    public SkillProfile SeedSkill(
        string userProfileId,
        string title = "Ordering food",
        string? description = "Practising restaurant conversations.",
        string language = "Korean",
        bool archived = false)
    {
        var skill = new SkillProfile
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Description = description,
            Language = language,
            UserProfileId = userProfileId,
            IsArchived = archived,
            CreatedAt = Now.AddDays(-40),
            UpdatedAt = Now.AddDays(-10)
        };
        Db.SkillProfiles.Add(skill);
        Db.SaveChanges();
        return skill;
    }

    public void SeedProfile(
        string userProfileId,
        string name = "Captain",
        string email = "captain@example.com",
        string targetLanguage = "Korean",
        string? targetLanguages = "Korean,Spanish",
        int preferredMinutes = 20,
        string? apiKey = "sk-secret-value")
    {
        Db.UserProfiles.Add(new UserProfile
        {
            Id = userProfileId,
            Name = name,
            Email = email,
            OpenAI_APIKey = apiKey,
            NativeLanguage = "English",
            TargetLanguage = targetLanguage,
            TargetLanguages = targetLanguages,
            DisplayLanguage = "en",
            PreferredSessionMinutes = preferredMinutes,
            TargetCEFRLevel = "B1",
            CreatedAt = Now.AddDays(-100)
        });
        Db.SaveChanges();
    }

    public LearningResource SeedResource(
        string userProfileId,
        string title = "Travel phrases",
        string mediaType = "Podcast",
        string? transcript = "This transcript must never reach the model.",
        string? tags = "travel,food",
        string? mediaUrl = null,
        int vocabularyCount = 0,
        bool isSmartResource = false)
    {
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            MediaType = mediaType,
            Transcript = transcript,
            Translation = "This translation must never reach the model.",
            Tags = tags,
            MediaUrl = mediaUrl,
            Language = "Korean",
            UserProfileId = userProfileId,
            IsSmartResource = isSmartResource,
            CreatedAt = Now.AddDays(-30),
            UpdatedAt = Now.AddDays(-30)
        };
        Db.LearningResources.Add(resource);

        for (var i = 0; i < vocabularyCount; i++)
        {
            var word = SeedWord($"단어{i}", $"word {i}", "food");
            Db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resource.Id,
                VocabularyWordId = word.Id
            });
        }

        Db.SaveChanges();
        return resource;
    }

    public VocabularyWord SeedWord(string target, string native, string? tags = null, string? lemma = null)
    {
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = target,
            NativeLanguageTerm = native,
            Lemma = lemma,
            Tags = tags,
            Language = "Korean",
            MnemonicText = "A memory aid the coach must never read.",
            CreatedAt = Now.AddDays(-20),
            UpdatedAt = Now.AddDays(-20)
        };
        Db.VocabularyWords.Add(word);
        Db.SaveChanges();
        return word;
    }

    public VocabularyProgress SeedProgress(
        string userProfileId,
        string vocabularyWordId,
        float masteryScore = 0.2f,
        int totalAttempts = 10,
        int correctAttempts = 7,
        int productionInStreak = 0,
        DateTime? nextReviewDate = null)
    {
        var progress = new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userProfileId,
            VocabularyWordId = vocabularyWordId,
            MasteryScore = masteryScore,
            TotalAttempts = totalAttempts,
            CorrectAttempts = correctAttempts,
            ProductionInStreak = productionInStreak,
            NextReviewDate = nextReviewDate,
            FirstSeenAt = Now.AddDays(-20),
            LastPracticedAt = Now.AddDays(-2)
        };
        Db.VocabularyProgresses.Add(progress);
        Db.SaveChanges();
        return progress;
    }

    public void SeedCompletion(
        string userProfileId,
        string activityType,
        int minutesSpent,
        int daysAgo,
        bool isCompleted = true,
        string? resourceId = null)
    {
        Db.DailyPlanCompletions.Add(new DailyPlanCompletion
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userProfileId,
            ActivityType = activityType,
            MinutesSpent = minutesSpent,
            EstimatedMinutes = minutesSpent,
            IsCompleted = isCompleted,
            CompletedAt = isCompleted ? Now.AddDays(-daysAgo) : null,
            Date = Now.AddDays(-daysAgo).Date,
            PlanItemId = Guid.NewGuid().ToString(),
            ResourceId = resourceId,
            CreatedAt = Now.AddDays(-daysAgo),
            UpdatedAt = Now.AddDays(-daysAgo)
        });
        Db.SaveChanges();
    }

    public void SeedActivity(string userProfileId, int daysAgo)
    {
        Db.UserActivities.Add(new UserActivity
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userProfileId,
            Activity = "VocabularyQuiz",
            Accuracy = 90,
            Fluency = 80,
            CreatedAt = Now.AddDays(-daysAgo),
            UpdatedAt = Now.AddDays(-daysAgo)
        });
        Db.SaveChanges();
    }

    /// <summary>Seeds a daily plan row for the learner's current local day.</summary>
    public DailyPlan SeedPlan(
        string userProfileId,
        string strategy = "deterministic",
        int generatedHoursAgo = 1)
    {
        var plan = new DailyPlan
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userProfileId,
            Date = Dates.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            GeneratedAtUtc = Now.AddHours(-generatedHoursAgo),
            Strategy = strategy,
            CreatedAt = Now.AddHours(-generatedHoursAgo),
            UpdatedAt = Now.AddHours(-generatedHoursAgo)
        };
        Db.DailyPlans.Add(plan);
        Db.SaveChanges();
        return plan;
    }

    /// <summary>Moves a resource's last-updated stamp, so list order is a property a test can set.</summary>
    public LearningResource Touch(LearningResource resource, int updatedDaysAgo)
    {
        resource.UpdatedAt = Now.AddDays(-updatedDaysAgo);
        Db.SaveChanges();
        return resource;
    }

    /// <summary>Moves a skill's last-updated stamp, so list order is a property a test can set.</summary>
    public SkillProfile Touch(SkillProfile skill, int updatedDaysAgo)
    {
        skill.UpdatedAt = Now.AddDays(-updatedDaysAgo);
        Db.SaveChanges();
        return skill;
    }

    public void Dispose()
    {
        _services.Dispose();
        Db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Creates a core-only registry (no Sam features) for test factories.</summary>
    public static ICoachToolRegistry CoreOnlyRegistry() =>
        new CoachToolRegistry(new CoachOptions
        {
            SamOverlay = new CoachFeatureSwitch { Enabled = false },
            SamReadTools = new CoachFeatureSwitch { Enabled = false },
            SamWriteTools = new CoachFeatureSwitch { Enabled = false }
        });

    /// <summary>Returns a service provider stub for tests that don't resolve Sam tools.</summary>
    public static IServiceProvider NullServiceProvider() =>
        new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
}
