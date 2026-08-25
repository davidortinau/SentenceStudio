using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// A trusted user scope a test controls, including the "no owner" case the recorder must
/// fail closed on.
/// </summary>
internal sealed class TestUserScope : IUserScopeProvider
{
    public TestUserScope(string? userProfileId = "learner-a") => Current = userProfileId;

    /// <summary>The active owner, or null to simulate an unauthenticated request.</summary>
    public string? Current { get; set; }

    public string UserProfileId =>
        Current ?? throw new UnauthorizedAccessException("No user profile on this test scope.");

    public bool TryGetUserProfileId(out string userProfileId)
    {
        userProfileId = Current ?? string.Empty;
        return !string.IsNullOrWhiteSpace(Current);
    }
}

/// <summary>
/// A recorder that always throws, for proving the turn survives a broken ledger.
/// </summary>
internal sealed class ThrowingCoachOpportunityRecorder : ICoachOpportunityRecorder
{
    public int Calls { get; private set; }

    public ValueTask RecordAsync(
        CoachOpportunitySignal signal,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException("The ledger is unavailable.");
    }
}

/// <summary>
/// A recorder that always throws <see cref="OperationCanceledException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The narrow case a <c>catch (Exception ex) when (ex is not OperationCanceledException)</c>
/// clause lets through. That shape reads as prudent — cancellation should normally propagate —
/// but at an observation boundary it is exactly backwards: the learner's operation has already
/// finished and its outcome is already decided, so letting a cancelled <em>observation</em>
/// escape replaces a real result with an unrelated cancellation.
/// </para>
/// <para>
/// Distinct from <see cref="ThrowingCoachOpportunityRecorder"/> because the two exercise
/// different catch clauses, and a test that only used the first would pass against the broken
/// version of every boundary this fake exists to cover.
/// </para>
/// </remarks>
internal sealed class CancellingCoachOpportunityRecorder : ICoachOpportunityRecorder
{
    public int Calls { get; private set; }

    public ValueTask RecordAsync(
        CoachOpportunitySignal signal,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new OperationCanceledException("The ledger write was cancelled.");
    }
}

/// <summary>Captures signals without touching a database.</summary>
internal sealed class RecordingCoachOpportunityRecorder : ICoachOpportunityRecorder
{
    public List<CoachOpportunitySignal> Signals { get; } = new();

    public ValueTask RecordAsync(
        CoachOpportunitySignal signal,
        CancellationToken cancellationToken = default)
    {
        Signals.Add(signal);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Builds a real relational <see cref="CoachDbContext"/> and a production
/// <see cref="CoachOpportunityRecorder"/> over it.
/// </summary>
/// <remarks>
/// <para>
/// The recorder resolves its context from a service scope in production, so the harness builds a
/// real <see cref="IServiceScopeFactory"/> rather than handing it a context directly. Testing the
/// production wiring is the point: the private-scope decision is what keeps the recorder off the
/// caller's ambient transaction and off a change tracker somebody else is clearing.
/// </para>
/// <para>
/// SQLite here; <c>CoachOpportunityPostgresTests</c> runs the same recorder against a real
/// PostgreSQL server so the <c>ON CONFLICT</c> statement, the unique index, and the <c>date</c>
/// and <c>timestamptz</c> casts are proven rather than assumed.
/// </para>
/// </remarks>
internal sealed class CoachOpportunityHarness : IDisposable
{
    private readonly CoachPersistenceHarness _persistence;
    private readonly ServiceProvider _provider;

    public CoachOpportunityHarness(
        DateTimeOffset? start = null,
        CoachOpportunityOptions? options = null,
        string? userProfileId = "learner-a",
        CoachResponseReportOptions? reportOptions = null)
    {
        _persistence = new CoachPersistenceHarness(start);

        Options = new TestOptionsMonitor<CoachOpportunityOptions>(
            options ?? new CoachOpportunityOptions { Enabled = true });

        // The learner-report switch is separate from automatic capture on purpose: a deployment
        // that stops inferring problems from its own turns has not said "discard the reports my
        // learners deliberately filed". The harness defaults it on so the report path is
        // exercised, and the tests that care flip it explicitly.
        ReportOptions = new TestOptionsMonitor<CoachResponseReportOptions>(
            reportOptions ?? new CoachResponseReportOptions { Enabled = true });

        UserScope = new TestUserScope(userProfileId);
        Registry = new CoachToolRegistry(new CoachOptions
        {
            Enabled = true,
            DurableHistory = new CoachFeatureSwitch { Enabled = true },
            SamOverlay = new CoachFeatureSwitch { Enabled = true },
            SamReadTools = new CoachFeatureSwitch { Enabled = true },
            SamWriteTools = new CoachFeatureSwitch { Enabled = true }
        });

        var services = new ServiceCollection();
        services.AddSingleton(_persistence.DbOptions);
        services.AddScoped<CoachDbContext>(sp =>
            new CoachDbContext(sp.GetRequiredService<DbContextOptions<CoachDbContext>>()));
        _provider = services.BuildServiceProvider();

        Recorder = new CoachOpportunityRecorder(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            UserScope,
            Registry,
            Options,
            ReportOptions,
            _persistence.Time,
            NullLogger<CoachOpportunityRecorder>.Instance);
    }

    public TestTimeProvider Time => _persistence.Time;

    public TestOptionsMonitor<CoachOpportunityOptions> Options { get; }

    public TestOptionsMonitor<CoachResponseReportOptions> ReportOptions { get; }

    public TestUserScope UserScope { get; }

    public CoachToolRegistry Registry { get; }

    public CoachOpportunityRecorder Recorder { get; }

    public ICoachContentProtector ContentProtector => _persistence.ContentProtector;

    public CoachDbContext NewContext() => _persistence.NewContext();

    public CoachMessageStore NewMessageStore(CoachDbContext db) => _persistence.NewMessageStore(db);

    public CoachConversationStore NewConversationStore(CoachDbContext db) =>
        _persistence.NewConversationStore(db);

    /// <summary>A second recorder for the same database under a different owner.</summary>
    public CoachOpportunityRecorder RecorderFor(string userProfileId) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            new TestUserScope(userProfileId),
            Registry,
            Options,
            ReportOptions,
            _persistence.Time,
            NullLogger<CoachOpportunityRecorder>.Instance);

    /// <summary>The same recorder with a logger a test can read back.</summary>
    public CoachOpportunityRecorder RecorderWithLogger(ILogger<CoachOpportunityRecorder> logger) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            UserScope,
            Registry,
            Options,
            ReportOptions,
            _persistence.Time,
            logger);

    public CoachOpportunityRetentionSweep NewRetentionSweep(CoachDbContext db) =>
        new(db, Options, _persistence.Time, NullLogger<CoachOpportunityRetentionSweep>.Instance);

    public CoachOpportunityDeletionContributor NewDeletionContributor(CoachDbContext db) =>
        new(db, NullLogger<CoachOpportunityDeletionContributor>.Instance);

    public CoachUnboundAnswerDetector NewDetector(ICoachMessageStore? messages = null) =>
        new(new CoachExplicitAcceptanceClassifier(),
            NullLogger<CoachUnboundAnswerDetector>.Instance,
            messages);

    /// <summary>Every row in the ledger, newest first.</summary>
    public async Task<List<CoachOpportunity>> RowsAsync()
    {
        await using var db = NewContext();
        return await db.CoachOpportunities
            .AsNoTracking()
            .OrderByDescending(row => row.LastObservedAtUtc)
            .ThenBy(row => row.Id)
            .ToListAsync();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _persistence.Dispose();
    }
}
