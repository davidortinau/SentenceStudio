using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Builds a real relational <see cref="CoachDbContext"/> over a private in-memory SQLite
/// connection plus the memory services under test.
/// </summary>
/// <remarks>
/// The memory entity configuration runs before the context's Npgsql-only branch, so the schema
/// created here is the same one the PostgreSQL migration was generated from — including the
/// filtered unique index that enforces one active fact per owner, kind, and scope.
/// </remarks>
internal sealed class CoachMemoryHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public CoachMemoryHarness(DateTimeOffset? start = null, CoachMemoryOptions? options = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        DbOptions = new DbContextOptionsBuilder<CoachDbContext>()
            .UseSqlite(_connection)
            .Options;

        Time = new TestTimeProvider(start ?? new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        Options = options ?? new CoachMemoryOptions { Enabled = true };

        var dataProtection = new EphemeralDataProtectionProvider();
        ContentProtector = new DataProtectionCoachContentProtector(
            dataProtection,
            NullLogger<DataProtectionCoachContentProtector>.Instance);

        Notifier = new RecordingNotifier();

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public DbContextOptions<CoachDbContext> DbOptions { get; }

    public TestTimeProvider Time { get; }

    public CoachMemoryOptions Options { get; }

    public ICoachContentProtector ContentProtector { get; }

    public RecordingNotifier Notifier { get; }

    public CoachDbContext NewContext() => new(DbOptions);

    public CoachMemoryStore NewStore(CoachDbContext db) => new(
        db,
        ContentProtector,
        Time,
        Microsoft.Extensions.Options.Options.Create(Options),
        Notifier,
        NullLogger<CoachMemoryStore>.Instance);

    public CoachMemoryContextSelector NewSelector(ICoachMemoryStore store) => new(
        store,
        Microsoft.Extensions.Options.Options.Create(Options),
        NullLogger<CoachMemoryContextSelector>.Instance);

    public CoachMemoryDeletionContributor NewDeletionContributor(ICoachMemoryStore store) => new(
        store,
        Notifier,
        NullLogger<CoachMemoryDeletionContributor>.Instance);

    public CoachMemorySourceDeletionHandler NewSourceDeletionHandler(ICoachMemoryStore store) => new(
        store,
        Notifier,
        NullLogger<CoachMemorySourceDeletionHandler>.Instance);

    /// <summary>Opens a raw ADO connection to the same database, for ciphertext scans.</summary>
    public SqliteCommand NewRawCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Captures notifications so tests can assert the checkpoint hook actually fires.</summary>
internal sealed class RecordingNotifier : ICoachMemoryChangedNotifier
{
    private readonly List<(CoachMemoryChangeKind Change, int Affected)> _changes = new();

    public IReadOnlyList<(CoachMemoryChangeKind Change, int Affected)> Changes => _changes;

    public bool Throws { get; set; }

    public Task MemoryChangedAsync(
        CoachOwner owner,
        CoachMemoryChangeKind change,
        int affectedCount,
        CancellationToken cancellationToken = default)
    {
        _changes.Add((change, affectedCount));

        if (Throws)
        {
            throw new InvalidOperationException("notifier failure");
        }

        return Task.CompletedTask;
    }

    public void Clear() => _changes.Clear();
}

/// <summary>Canonical samples for the memory tests.</summary>
internal static class CoachMemorySamples
{
    public const string OwnerUserId = "user-owner-1";
    public const string OtherUserId = "user-intruder-2";
    public const string Korean = "ko";
    public const string Japanese = "ja";

    /// <summary>
    /// A phrase that only ever exists in a plaintext memory value. Any appearance of this string
    /// in a raw database column is a leak.
    /// </summary>
    public const string ValueSentinel = "SENTINEL_MEMORY_VALUE_9c4b";

    public static CoachOwner Owner(string? userProfileId = null, string? tenantId = null) =>
        CoachOwner.ForUser(userProfileId ?? OwnerUserId, tenantId);

    public static CoachOwner Other() => CoachOwner.ForUser(OtherUserId);

    /// <summary>An owner with no trusted learner. Every store method must refuse it.</summary>
    public static CoachOwner Empty() => default;

    public static CoachMemoryStoredValue Goal(string text = "Prepare for a trip to Seoul") =>
        CoachMemoryStoredValue.StudyGoal(text);

    public static CoachMemoryStoredValue Depth(
        CoachMemoryExplanationDepth depth = CoachMemoryExplanationDepth.Concise) =>
        CoachMemoryStoredValue.Depth(depth);

    public static CoachMemoryStoredValue Timing(
        CoachMemoryCorrectionTiming timing = CoachMemoryCorrectionTiming.AfterResponse) =>
        CoachMemoryStoredValue.Timing(timing);

    public static CoachMemoryStoredValue Register(
        CoachMemoryExampleRegister register = CoachMemoryExampleRegister.Casual) =>
        CoachMemoryStoredValue.Register(register);

    /// <summary>
    /// Builds a candidate request whose evidence span really is a substring of the learner message,
    /// which is what the store verifies before it will accept anything.
    /// </summary>
    public static CreateCoachMemoryCandidateRequest Candidate(
        CoachMemoryStoredValue? value = null,
        CoachMemoryScope scope = CoachMemoryScope.TargetLanguage,
        string? language = Korean,
        string? conversationId = "conv-1",
        string? messageId = "msg-1",
        string? evidence = null,
        string? message = null)
    {
        var span = evidence ?? "please keep explanations concise";
        var text = message ?? $"For future sessions {span}, thanks.";

        return new CreateCoachMemoryCandidateRequest(
            value ?? Depth(),
            scope,
            scope == CoachMemoryScope.Global ? null : language,
            text,
            span,
            conversationId,
            messageId);
    }
}
