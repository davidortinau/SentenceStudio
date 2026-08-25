using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// A broken ledger must be invisible to the learner.
/// </summary>
/// <remarks>
/// The strongest claim this design makes is that capture cannot change a turn. That claim is what
/// makes it defensible to run capture in Production while the review surface stays Development-
/// only, so it is a tested invariant rather than an argument.
/// </remarks>
public class CoachOpportunityResilienceTests
{
    private static CoachOpportunitySignal Signal() =>
        new(CoachOpportunityKind.UnsupportedCapability,
            CoachOpportunityCapabilityCodes.EntityLookupByName,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposeVocabularyRemoval,
            Evidence: new CoachOpportunityEvidencePointer("conv-1"));

    [Fact]
    public async Task ADatabaseFailureNeverEscapesTheRecorder()
    {
        using var harness = new CoachOpportunityHarness();
        var logger = new CapturingLogger<CoachOpportunityRecorder>();
        var recorder = harness.RecorderWithLogger(logger);

        // Drop the table under the recorder. This is the shape of every real failure — a
        // migration mid-flight, a permissions change, a dropped connection.
        await using (var db = harness.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"CoachOpportunity\";");
        }

        var act = async () => await recorder.RecordAsync(Signal());

        await act.Should().NotThrowAsync(
            "a ledger that could fail a turn would be worse than no ledger");

        logger.Messages.Should().Contain(message =>
            message.Contains("could not be recorded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARecorderFailureLogsShapeOnly()
    {
        using var harness = new CoachOpportunityHarness();
        var logger = new CapturingLogger<CoachOpportunityRecorder>();
        var recorder = harness.RecorderWithLogger(logger);

        await using (var db = harness.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"CoachOpportunity\";");
        }

        await recorder.RecordAsync(Signal());

        foreach (var message in logger.Messages)
        {
            // CoachExceptionSanitizer is the only path from an exception to a log line on this
            // codebase, because Exception.ToString concatenates the message, the inner chain,
            // and Data — which on a coach path carry prompt and learner text.
            message.Should().NotContain("INSERT INTO",
                "the failing statement must not be logged; it names every column");
            message.Should().NotContain("conv-1");
        }
    }

    [Fact]
    public async Task ACancelledRecordDoesNotThrow()
    {
        using var harness = new CoachOpportunityHarness();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await harness.Recorder.RecordAsync(Signal(), cancellation.Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TheRecorderRunsOnItsOwnScopeAndCannotCorruptTheCallersContext()
    {
        using var harness = new CoachOpportunityHarness();

        await using var callerContext = harness.NewContext();

        // The caller has pending work on its own change tracker. A recorder that shared the
        // context would either flush this early or be wiped by the caller's own
        // ChangeTracker.Clear() on an error path — both of which are silent corruption.
        callerContext.CoachSessions.Add(new CoachSession
        {
            Id = "session-1",
            UserProfileId = "learner-a",
            AgentImplementation = "baseline",
            AgentName = "Sam",
            AgentConfigVersion = "2",
            SessionSchemaVersion = 1,
            ActiveConstraintsJson = "{}",
            ExpiresAt = harness.Time.GetUtcNow().UtcDateTime.AddHours(1)
        });

        await harness.Recorder.RecordAsync(Signal());

        callerContext.ChangeTracker.Entries().Should().ContainSingle(
            "the recorder used its own scope, so the caller's pending work is untouched");

        (await harness.RowsAsync()).Should().ContainSingle(
            "and the ledger row committed independently of the caller's unsaved work");
    }

    /// <summary>
    /// The write ledger's audit survives a broken recorder, driven through the real save path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim being tested is about <c>SaveAuditedAsync</c>: it queues signals during a write
    /// and flushes them only <em>after</em> <c>SaveChanges</c> commits. A test that called
    /// <c>RecordAsync</c> directly and asserted it threw would prove the fake works and never
    /// enter the flush loop at all, so this drives the real refusal path — an approval naming an
    /// operation that does not resolve, which <c>AuditOrphanDenialAsync</c> owns.
    /// </para>
    /// <para>
    /// Runs on the harness's relational context so it executes everywhere, including hosts with
    /// no PostgreSQL server. <c>CoachWriteLedgerOpportunityResiliencePostgresTests</c> repeats it
    /// on the real provider with the full application schema, where the audit row's composite
    /// foreign key to its conversation is genuinely enforced.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(BrokenRecorders))]
    public async Task TheWriteLedgerStillAuditsWhenTheRecorderIsBroken(
        string description,
        ICoachOpportunityRecorder recorder,
        Func<ICoachOpportunityRecorder, int> callCount)
    {
        using var harness = new CoachOpportunityHarness();
        await using var db = harness.NewContext();
        await SeedConversationAsync(harness, db, "conv-write");

        var ledger = new CoachWriteOperationService(
            db,
            harness.ContentProtector,
            new CoachWriteHandlerCatalog([]),
            harness.Registry,
            new TestUserScope("learner-a"),
            harness.Time,
            NullLogger<CoachWriteOperationService>.Instance,
            recorder);

        // An approval for an operation that does not exist. The ledger writes a standalone
        // Denied audit row — the forensic record of what is, in shape, a cross-tenant probe —
        // commits it, then flushes the queued signal into the broken recorder.
        var act = async () => await ledger.AcceptAsync("conv-write", "no-such-operation");

        // The caller's outcome is the tool refusal, not the recorder's exception.
        await act.Should().ThrowAsync<CoachToolException>(
            $"a {description} recorder must not replace the refusal the caller is entitled to");

        callCount(recorder).Should().BeGreaterThan(0,
            "the flush loop has to have been entered, or this proves nothing about it");

        await using var check = harness.NewContext();
        var audits = await check.CoachWriteAudits
            .AsNoTracking()
            .Where(row => row.UserProfileId == "learner-a")
            .ToListAsync();

        audits.Should().ContainSingle(
            "the audit committed before the flush ran; the audit row is the forensic record and " +
            "the ledger row is telemetry, and telemetry never outranks forensics");

        audits[0].Event.Should().Be(CoachWriteAuditEvent.Denied);
        audits[0].FailureCode.Should().Be(CoachWriteFailureCodes.OperationNotFound);
    }

    /// <summary>
    /// The two failure shapes an observation boundary has to contain, as separate mutants.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellingCoachOpportunityRecorder"/> is the narrow one: a
    /// <c>catch (Exception ex) when (ex is not OperationCanceledException)</c> clause reads as
    /// prudent and lets it through, and a suite that only used the throwing recorder passed
    /// against exactly that broken version.
    /// </remarks>
    public static TheoryData<string, ICoachOpportunityRecorder, Func<ICoachOpportunityRecorder, int>> BrokenRecorders() =>
        new()
        {
            {
                "throwing",
                new ThrowingCoachOpportunityRecorder(),
                r => ((ThrowingCoachOpportunityRecorder)r).Calls
            },
            {
                "cancelling",
                new CancellingCoachOpportunityRecorder(),
                r => ((CancellingCoachOpportunityRecorder)r).Calls
            }
        };

    /// <summary>The conversation a write audit hangs from.</summary>
    private static async Task SeedConversationAsync(
        CoachOpportunityHarness harness,
        Api.Coach.Persistence.CoachDbContext db,
        string conversationId)
    {
        var now = harness.Time.GetUtcNow().UtcDateTime;

        db.CoachConversations.Add(new Api.Coach.Persistence.History.CoachConversation
        {
            Id = conversationId,
            UserProfileId = "learner-a",
            ProtectedTitle = "seeded",
            HistoryStartsAt = now,
            ContentProtectionVersion = harness.ContentProtector.CurrentVersion,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task CaptureOnAndOffProduceTheSameLedgerlessBehaviour()
    {
        // The response-neutrality invariant reduced to what a unit test can hold: the recorder
        // takes a value, returns nothing, and has no way to signal anything back to its caller.
        // A caller therefore cannot branch on it, whatever it does.
        using var on = new CoachOpportunityHarness(
            options: new CoachOpportunityOptions { Enabled = true });
        using var off = new CoachOpportunityHarness(
            options: new CoachOpportunityOptions { Enabled = false });

        await on.Recorder.RecordAsync(Signal());
        await off.Recorder.RecordAsync(Signal());

        (await on.RowsAsync()).Should().ContainSingle();
        (await off.RowsAsync()).Should().BeEmpty();

        typeof(ICoachOpportunityRecorder)
            .GetMethod(nameof(ICoachOpportunityRecorder.RecordAsync))!
            .ReturnType.Should().Be(typeof(ValueTask),
                "there is no outcome for a caller to branch on, which is what makes response " +
                "neutrality structural rather than a promise");
    }

    [Fact]
    public void TheNullRecorderIsAlwaysSafe()
    {
        var act = async () => await NullCoachOpportunityRecorder.Instance.RecordAsync(Signal());
        act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ADisabledHostDoesNotEvenOpenAScope()
    {
        // Off is a no-op before the scope is created, so a host with capture off pays nothing —
        // not a connection, not a scope, not a query.
        var services = new ServiceCollection();
        services.AddScoped<CoachDbContext>(_ =>
            throw new InvalidOperationException("A disabled recorder must not resolve a context."));

        using var provider = services.BuildServiceProvider();

        var recorder = new CoachOpportunityRecorder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestUserScope("learner-a"),
            new CoachToolRegistry(new SentenceStudio.Api.Coach.Runtime.CoachOptions { Enabled = true }),
            new TestOptionsMonitor<CoachOpportunityOptions>(
                new CoachOpportunityOptions { Enabled = false }),
            new TestOptionsMonitor<SentenceStudio.Api.Coach.Reports.CoachResponseReportOptions>(
                new SentenceStudio.Api.Coach.Reports.CoachResponseReportOptions { Enabled = false }),
            TimeProvider.System,
            NullLogger<CoachOpportunityRecorder>.Instance);

        var act = async () => await recorder.RecordAsync(Signal());
        await act.Should().NotThrowAsync();
    }
}
