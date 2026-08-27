using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Builds a real relational <see cref="CoachDbContext"/> over a private in-memory SQLite
/// connection, plus the stores under test. The coach model applies its Npgsql-only column
/// types conditionally, so this exercises the same entity configuration the PostgreSQL
/// migration was generated from.
/// </summary>
internal sealed class CoachPersistenceHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public CoachPersistenceHarness(DateTimeOffset? start = null, CoachPersistenceOptions? options = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        DbOptions = new DbContextOptionsBuilder<CoachDbContext>()
            .UseSqlite(_connection)
            .Options;

        Time = new TestTimeProvider(start ?? new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        Options = options ?? new CoachPersistenceOptions();

        var dataProtection = new EphemeralDataProtectionProvider();
        Protector = new DataProtectionCoachAgentSessionProtector(
            dataProtection,
            NullLogger<DataProtectionCoachAgentSessionProtector>.Instance);
        ContentProtector = new SentenceStudio.Api.Coach.Persistence.History.DataProtectionCoachContentProtector(
            dataProtection,
            NullLogger<SentenceStudio.Api.Coach.Persistence.History.DataProtectionCoachContentProtector>.Instance);

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public DbContextOptions<CoachDbContext> DbOptions { get; }

    public TestTimeProvider Time { get; }

    public CoachPersistenceOptions Options { get; }

    public ICoachAgentSessionProtector Protector { get; }

    public CoachDbContext NewContext() => new(DbOptions);

    public CoachSessionStore NewSessionStore(CoachDbContext db) =>
        new(db, Protector, Microsoft.Extensions.Options.Options.Create(Options), Time, NullLogger<CoachSessionStore>.Instance);

    public CoachUsageStore NewUsageStore(CoachDbContext db) =>
        new(db, Time, NullLogger<CoachUsageStore>.Instance);

    public CoachExpiryCleanupService NewCleanupService(CoachDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(Options), Time, NullLogger<CoachExpiryCleanupService>.Instance);

    /// <summary>
    /// The content protector the history stores use. Shared across every store the harness
    /// builds so ciphertext written by one is readable by another, which is what production
    /// does through a single key ring.
    /// </summary>
    public SentenceStudio.Api.Coach.Persistence.History.ICoachContentProtector ContentProtector { get; }

    public SentenceStudio.Api.Coach.Persistence.History.CoachConversationStore NewConversationStore(CoachDbContext db) =>
        new(db, ContentProtector, Time, NullLogger<SentenceStudio.Api.Coach.Persistence.History.CoachConversationStore>.Instance);

    public SentenceStudio.Api.Coach.Persistence.History.CoachMessageStore NewMessageStore(CoachDbContext db) =>
        new(db, ContentProtector, Time, NullLogger<SentenceStudio.Api.Coach.Persistence.History.CoachMessageStore>.Instance);

    public SentenceStudio.Api.Coach.Persistence.History.CoachTurnOperationStore NewTurnOperationStore(CoachDbContext db) =>
        new(db, ContentProtector, Time, NullLogger<SentenceStudio.Api.Coach.Persistence.History.CoachTurnOperationStore>.Instance);

    public SentenceStudio.Api.Coach.Persistence.History.CoachHistoryExportReader NewExportReader(CoachDbContext db) =>
        new(db, ContentProtector, NullLogger<SentenceStudio.Api.Coach.Persistence.History.CoachHistoryExportReader>.Instance);

    public SentenceStudio.Api.Coach.Persistence.History.CoachHistoryDeletionContributor NewDeletionContributor(CoachDbContext db) =>
        new(db, NullLogger<SentenceStudio.Api.Coach.Persistence.History.CoachHistoryDeletionContributor>.Instance);

    /// <summary>
    /// A real memory store over the same database and the same content protector, so a test
    /// that writes a fact through the store and reads it back through the selector exercises
    /// the encryption path rather than a dictionary standing in for it.
    /// </summary>
    public SentenceStudio.Api.Coach.Memory.CoachMemoryStore NewMemoryStore(
        CoachDbContext db,
        SentenceStudio.Api.Coach.Memory.ICoachMemoryChangedNotifier notifier,
        SentenceStudio.Api.Coach.Memory.CoachMemoryOptions? options = null,
        Microsoft.Extensions.Logging.ILogger<SentenceStudio.Api.Coach.Memory.CoachMemoryStore>? logger = null) =>
        new(db,
            ContentProtector,
            Time,
            Microsoft.Extensions.Options.Options.Create(
                options ?? new SentenceStudio.Api.Coach.Memory.CoachMemoryOptions { Enabled = true }),
            notifier,
            logger ?? NullLogger<SentenceStudio.Api.Coach.Memory.CoachMemoryStore>.Instance);

    /// <summary>Opens a raw ADO connection to the same in-memory database, for ciphertext scans.</summary>
    public SqliteCommand NewRawCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Canonical sample payloads for the coach persistence tests.</summary>
internal static class CoachPersistenceSamples
{
    public const string OwnerUserId = "user-owner-1";
    public const string OtherUserId = "user-intruder-2";

    /// <summary>
    /// A phrase that only ever exists in plaintext learner input. Any appearance of this
    /// string in a raw database column is a leak.
    /// </summary>
    public const string LearnerSentinel = "SENTINEL_LEARNER_TEXT_7f3a";

    public static CoachConstraintSetDto Constraints(int minutes = 20) => new()
    {
        AvailableMinutes = minutes,
        AudioAllowed = true,
        SpeechAllowed = true,
        TypingAllowed = true,
        SkillEmphasis = CoachSkillEmphasis.Listening,
        GoalTag = "travel",
        GoalHorizonDays = 30,
        EnergyLevel = CoachEnergyLevel.Normal
    };

    public static CoachConstraintDeltaDto Delta(int minutes = 12) => new()
    {
        AvailableMinutes = minutes,
        ChangedFields = new[] { CoachConstraintField.AvailableMinutes }
    };

    public static CreateCoachSessionRequest CreateRequest(string? agentSessionJson = null) => new()
    {
        AgentImplementation = "baseline",
        AgentName = "learning-coach",
        ActiveConstraints = Constraints(),
        AgentSessionJson = agentSessionJson
    };

    public static CoachPlanStateDto Plan(string version, int completed = 1, int total = 4) => new()
    {
        PlanDate = new DateOnly(2026, 8, 14),
        PlanVersion = version,
        AppliedConstraints = Constraints(),
        EstimatedTotalMinutes = 20,
        CompletedCount = completed,
        TotalCount = total,
        CompletionPercentage = total == 0 ? 0 : completed * 100d / total,
        Items = new[]
        {
            new CoachPlanItemDto
            {
                Id = "item-1",
                ActivityType = CoachPlanActivityType.VocabularyReview,
                Title = "Vocabulary review",
                Description = "Review due words",
                Priority = 1,
                EstimatedMinutes = 5,
                MinutesSpent = 5,
                IsCompleted = true,
                ChangeKind = CoachPlanItemChangeKind.PreservedCompleted
            }
        }
    };

    public static CoachPlanRevisionInput RevisionInput(string beforeVersion = "v1", string afterVersion = "v2") => new()
    {
        Source = CoachRevisionSource.DirectRequest,
        IntentKind = CoachIntentKind.DirectConstraintChange,
        AcceptedDelta = Delta(),
        BeforePlanVersion = beforeVersion,
        AfterPlanVersion = afterVersion,
        BeforePlan = Plan(beforeVersion),
        AfterPlan = Plan(afterVersion, completed: 1, total: 3),
        PreservedCompletedCount = 1,
        PreservedInProgressCount = 2
    };
}
