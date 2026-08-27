using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The opportunity ledger against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite tests cover provider-independent behaviour. These prove the parts that only exist
/// on the real provider and that a relational stand-in would let pass: the migration applies, the
/// unique index is enforced by the database rather than by the application, the single
/// <c>ON CONFLICT</c> upsert is atomic across independent connections, and the explicit
/// <c>::date</c> and <c>::timestamptz</c> casts survive the host's process-wide
/// <c>Npgsql.EnableLegacyTimestampBehavior</c> switch.
/// </para>
/// <para>
/// That last one is not hypothetical. The switch changes which PostgreSQL type an inferred
/// <see cref="DateTime"/> parameter maps to, which is exactly why the recorder passes timestamps
/// as ISO-8601 text and casts them in SQL.
/// </para>
/// </remarks>
public class CoachOpportunityPostgresTests
{
    [PostgresFact]
    public async Task TheMigrationCreatesTheTableAndItsIndexes()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-schema");

        var columns = await harness.StringsAsync(
            "select column_name from information_schema.columns " +
            "where table_name = 'CoachOpportunity' order by column_name;");

        columns.Should().Contain("Fingerprint");
        columns.Should().Contain("DedupBucketDate");
        columns.Should().Contain("OccurrenceCount");
        columns.Should().Contain("EvidenceRevealCount");

        // The privacy claim, checked against the shipped schema rather than the model.
        columns.Should().NotContain(column =>
            column.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || column.Contains("Protected", StringComparison.OrdinalIgnoreCase)
            || column.Contains("Text", StringComparison.OrdinalIgnoreCase));

        var indexes = await harness.StringsAsync(
            "select indexname from pg_indexes where tablename = 'CoachOpportunity' order by indexname;");

        indexes.Should().Contain("IX_CoachOpportunity_UserProfileId_Fingerprint_DedupBucketDate");
        indexes.Should().Contain("IX_CoachOpportunity_Status_LastObservedAtUtc");
        indexes.Should().Contain("IX_CoachOpportunity_Kind_CapabilityCode_LastObservedAtUtc");
        indexes.Should().Contain("IX_CoachOpportunity_UserProfileId_ConversationId");
        indexes.Should().Contain("IX_CoachOpportunity_LastObservedAtUtc");
    }

    [PostgresFact]
    public async Task TheTimestampColumnsArePinnedToTimestampTz()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-types");

        var types = await harness.StringsAsync(
            "select data_type from information_schema.columns " +
            "where table_name = 'CoachOpportunity' " +
            "and column_name in ('FirstObservedAtUtc','LastObservedAtUtc','ReviewedAtUtc'," +
            "'EvidenceLastRevealedAtUtc');");

        types.Should().OnlyContain(type => type == "timestamp with time zone");

        var bucket = await harness.StringsAsync(
            "select data_type from information_schema.columns " +
            "where table_name = 'CoachOpportunity' and column_name = 'DedupBucketDate';");

        bucket.Should().ContainSingle().Which.Should().Be("date");
    }

    [PostgresFact]
    public async Task TheUpsertIncrementsRatherThanDuplicating()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-upsert");
        var recorder = NewRecorder(harness, "learner-a");

        await recorder.RecordAsync(Signal());
        await recorder.RecordAsync(Signal());
        await recorder.RecordAsync(Signal());

        await using var db = harness.NewContext();
        var rows = await db.CoachOpportunities.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle();
        rows[0].OccurrenceCount.Should().Be(3);
        rows[0].LastObservedAtUtc.Should().BeOnOrAfter(rows[0].FirstObservedAtUtc);
    }

    [PostgresFact]
    public async Task TheUniqueIndexIsEnforcedByTheDatabase()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-unique");
        var recorder = NewRecorder(harness, "learner-a");
        await recorder.RecordAsync(Signal());

        await using var db = harness.NewContext();
        var existing = await db.CoachOpportunities.AsNoTracking().SingleAsync();

        // A hand-rolled duplicate that bypasses the upsert entirely. The database must refuse it,
        // so the invariant does not depend on the application always taking the ON CONFLICT path.
        var act = async () => await harness.ExecuteAsync(
            "insert into \"CoachOpportunity\" " +
            "(\"Id\",\"UserProfileId\",\"Kind\",\"Disposition\",\"Surface\",\"CapabilityCode\"," +
            " \"OfferLink\",\"Fingerprint\",\"DedupBucketDate\",\"OccurrenceCount\"," +
            " \"FirstObservedAtUtc\",\"LastObservedAtUtc\",\"Status\",\"EvidenceRevealCount\"," +
            " \"SchemaVersion\",\"Version\") values " +
            $"('duplicate','{existing.UserProfileId}',0,0,2,'{existing.CapabilityCode}',0," +
            $"'{existing.Fingerprint}','{existing.DedupBucketDate:yyyy-MM-dd}',1," +
            "now(),now(),0,0,1,0);");

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
    }

    [PostgresFact]
    public async Task ConcurrentRecordersOnIndependentConnectionsProduceOneRow()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-race");

        // Eight recorders, each with its own service provider and therefore its own connection.
        // This is what makes the race a real race rather than two objects taking turns on one
        // handle — and it is the case the single atomic statement exists for.
        var recorders = Enumerable.Range(0, 8)
            .Select(_ => NewRecorder(harness, "learner-a"))
            .ToList();

        await Task.WhenAll(recorders.Select(r => r.RecordAsync(Signal()).AsTask()));

        await using var db = harness.NewContext();
        var rows = await db.CoachOpportunities.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle();
        rows[0].OccurrenceCount.Should().Be(8,
            "the ON CONFLICT upsert is atomic across replicas, so no occurrence is lost and no " +
            "duplicate row is created");
    }

    [PostgresFact]
    public async Task TwoLearnersProduceTwoRowsWithOneFingerprint()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-owners");

        await NewRecorder(harness, "learner-a").RecordAsync(Signal());
        await NewRecorder(harness, "learner-b").RecordAsync(Signal());

        await using var db = harness.NewContext();
        var rows = await db.CoachOpportunities.AsNoTracking().ToListAsync();

        rows.Should().HaveCount(2);
        rows.Select(row => row.Fingerprint).Distinct().Should().ContainSingle();

        // The rollup shape a reviewer actually reads: counts, never identifiers.
        var distinctLearners = await db.CoachOpportunities
            .AsNoTracking()
            .GroupBy(row => row.Fingerprint)
            .Select(group => group.Select(row => row.UserProfileId).Distinct().Count())
            .SingleAsync();

        distinctLearners.Should().Be(2);
    }

    [PostgresFact]
    public async Task ErasureRemovesEveryRowAndTheVerificationPassFindsZero()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-erasure");

        await NewRecorder(harness, "learner-a").RecordAsync(Signal());
        await NewRecorder(harness, "learner-a").RecordAsync(
            Signal(CoachOpportunityCapabilityCodes.WriteToolsDisabled));
        await NewRecorder(harness, "learner-b").RecordAsync(Signal());

        await using var db = harness.NewContext();
        var contributor = new CoachOpportunityDeletionContributor(
            db, NullLogger<CoachOpportunityDeletionContributor>.Instance);

        var owner = Api.Coach.Persistence.History.CoachOwner.ForUser("learner-a");

        (await contributor.DeleteAllAsync(owner)).Should().Be(2);
        (await contributor.DeleteAllAsync(owner)).Should().Be(0);

        var remaining = await db.CoachOpportunities.AsNoTracking().ToListAsync();
        remaining.Should().ContainSingle();
        remaining[0].UserProfileId.Should().Be("learner-b");
    }

    [PostgresFact]
    public async Task RetentionRemovesUndecidedRowsAndSparesDecisions()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-retention");

        await NewRecorder(harness, "learner-a").RecordAsync(Signal());
        await NewRecorder(harness, "learner-a").RecordAsync(
            Signal(CoachOpportunityCapabilityCodes.WriteToolsDisabled));

        await using (var setup = harness.NewContext())
        {
            var kept = await setup.CoachOpportunities
                .SingleAsync(row => row.CapabilityCode == CoachOpportunityCapabilityCodes.WriteToolsDisabled);
            kept.Status = CoachOpportunityStatus.Accepted;
            await setup.SaveChangesAsync();
        }

        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(1));

        await using var db = harness.NewContext();
        var sweep = new CoachOpportunityRetentionSweep(
            db,
            new TestOptionsMonitor<CoachOpportunityOptions>(
                new CoachOpportunityOptions { Enabled = true }),
            harness.Time,
            NullLogger<CoachOpportunityRetentionSweep>.Instance);

        (await sweep.RunAsync()).RowsDeleted.Should().Be(1);

        var remaining = await db.CoachOpportunities.AsNoTracking().ToListAsync();
        remaining.Should().ContainSingle();
        remaining[0].Status.Should().Be(CoachOpportunityStatus.Accepted);
    }

    [PostgresFact]
    public async Task AnAggregateOnlyRowIsWrittenWithNullPointers()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-aggregate");

        await NewRecorder(harness, "learner-a").RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.HarmfulOrUnsafeRequest,
            CoachOpportunityCapabilityCodes.DestructiveRequestRefused,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly,
            Evidence: new CoachOpportunityEvidencePointer("conv-secret", "msg-secret", 9, "msg-8", 8),
            TurnId: "turn-secret",
            WriteOperationId: "write-secret"));

        // Read straight through ADO, so this asserts what is on disk rather than what the model
        // projects.
        var nulls = await harness.ScalarAsync<long>(
            "select count(*) from \"CoachOpportunity\" " +
            "where \"ConversationId\" is null and \"TurnId\" is null " +
            "and \"EvidenceMessageId\" is null and \"EvidenceOfferMessageId\" is null " +
            "and \"WriteOperationId\" is null;");

        nulls.Should().Be(1);
    }

    [PostgresFact]
    public async Task TheDayBucketRollsOverAtUtcMidnight()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync(
            "opportunity-bucket",
            start: new DateTimeOffset(2026, 8, 20, 23, 59, 0, TimeSpan.Zero));

        await NewRecorder(harness, "learner-a").RecordAsync(Signal());
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        await NewRecorder(harness, "learner-a").RecordAsync(Signal());

        await using var db = harness.NewContext();
        var rows = await db.CoachOpportunities.AsNoTracking()
            .OrderBy(row => row.DedupBucketDate)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows[0].DedupBucketDate.Should().Be(new DateOnly(2026, 8, 20));
        rows[1].DedupBucketDate.Should().Be(new DateOnly(2026, 8, 21));
    }

    // ---------------------------------------------------------------- the rollup

    /// <summary>
    /// The rollup counts distinct learners on the real provider.
    /// </summary>
    /// <remarks>
    /// <c>COUNT(DISTINCT UserProfileId)</c> is the only trace of who was affected, and it is
    /// produced by a grouped LINQ projection — which SQLite and PostgreSQL do not translate
    /// identically. Asserting it against the real provider is the difference between "the
    /// expression compiles" and "the number an operator reads is right".
    /// </remarks>
    [PostgresFact]
    public async Task TheRollupCountsDistinctLearnersWithoutNamingThem()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-rollup");

        // Three learners on one problem, one of them twice on different days.
        await NewRecorder(harness, "learner-a").RecordAsync(Signal());
        await NewRecorder(harness, "learner-b").RecordAsync(Signal());
        await NewRecorder(harness, "learner-c").RecordAsync(Signal());
        harness.Time.Advance(TimeSpan.FromDays(1));
        await NewRecorder(harness, "learner-a").RecordAsync(Signal());

        // A second, different problem, one learner.
        await NewRecorder(harness, "learner-a").RecordAsync(
            Signal(CoachOpportunityCapabilityCodes.WriteToolsDisabled));

        await using var db = harness.NewContext();
        var service = NewOperatorService(harness, db, "learner-a");

        var rollup = await service.RollupAsync(null);

        rollup.IsOk.Should().BeTrue();
        rollup.Value!.Should().HaveCount(2, "two distinct fingerprints");

        var first = rollup.Value.Single(line =>
            line.CapabilityCode == CoachOpportunityCapabilityCodes.EntityLookupByName);

        first.DistinctLearners.Should().Be(3,
            "three learners hit it, and learner-a's second day must not be counted twice");
        first.RowCount.Should().Be(4, "four rows: three learners plus a second day for one");
        first.TotalOccurrences.Should().Be(4);

        var second = rollup.Value.Single(line =>
            line.CapabilityCode == CoachOpportunityCapabilityCodes.WriteToolsDisabled);

        second.DistinctLearners.Should().Be(1);

        // The projection carries no owner identifier of any kind.
        typeof(CoachOpportunityRollupDto).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(name =>
                name.Contains("UserProfile", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Owner", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The status set respects the same <c>since</c> window as the counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The statuses are read by a second query. Without the same bound it reads every row that
    /// ever carried the fingerprint, so a rollup windowed to the last seven days reports statuses
    /// belonging to rows outside the window.
    /// </para>
    /// <para>
    /// The scenario below is the one that matters: a problem dismissed long ago that is recurring
    /// now. Under the unbounded query it renders as <c>Dismissed</c> against fresh occurrences
    /// the reviewer is looking at precisely because they are new — which inverts the decision the
    /// window was opened to support.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task TheRollupStatusSetRespectsTheSinceWindow()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-rollup-since");

        // An old occurrence, dismissed at the time.
        await NewRecorder(harness, "learner-a").RecordAsync(Signal());

        await using (var setup = harness.NewContext())
        {
            var old = await setup.CoachOpportunities.SingleAsync();
            old.Status = CoachOpportunityStatus.Dismissed;
            await setup.SaveChangesAsync();
        }

        var boundary = harness.Time.GetUtcNow().UtcDateTime.AddDays(30);
        harness.Time.Advance(TimeSpan.FromDays(60));

        // The same problem, recurring now, undecided.
        await NewRecorder(harness, "learner-b").RecordAsync(Signal());

        await using var db = harness.NewContext();
        var service = NewOperatorService(harness, db, "learner-a");

        var windowed = await service.RollupAsync(boundary);
        var line = windowed.Value!.Should().ContainSingle().Subject;

        line.DistinctLearners.Should().Be(1, "only the recent occurrence is in the window");
        line.Statuses.Should().BeEquivalentTo([nameof(CoachOpportunityStatus.New)],
            "the old dismissal is outside the window and must not colour a fresh recurrence");

        // Unwindowed, both are visible and both statuses are reported.
        var everything = await service.RollupAsync(null);
        var all = everything.Value!.Should().ContainSingle().Subject;

        all.DistinctLearners.Should().Be(2);
        all.Statuses.Should().BeEquivalentTo([
            nameof(CoachOpportunityStatus.New),
            nameof(CoachOpportunityStatus.Dismissed)
        ]);
    }

    /// <summary>
    /// Aggregate-only rows appear in the rollup and never in the listing.
    /// </summary>
    /// <remarks>
    /// The rollup is where an aggregate-only row's whole signal lives; a triage listing would be a
    /// line a reviewer can do nothing with, because the row carries no conversation and no
    /// pointers. Asserted on the real provider because both are database queries.
    /// </remarks>
    [PostgresFact]
    public async Task AnAggregateOnlyRowIsCountedButNotListed()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("opportunity-rollup-agg");

        await NewRecorder(harness, "learner-a").RecordAsync(Signal());
        await NewRecorder(harness, "learner-a").RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.CapacityOrBudgetRefusal,
            CoachOpportunityCapabilityCodes.DailyRunLimit,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly,
            StopReason: CoachStopReason.RateLimit));

        await using var db = harness.NewContext();
        var service = NewOperatorService(harness, db, "learner-a");

        var rollup = await service.RollupAsync(null);
        rollup.Value!.Should().HaveCount(2);
        rollup.Value.Should().Contain(line =>
            line.CapabilityCode == CoachOpportunityCapabilityCodes.DailyRunLimit);

        var page = await service.ListAsync(null, null, null, null, 0, 0);
        page.Value!.Items.Should().ContainSingle()
            .Which.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.EntityLookupByName);
    }

    private static CoachOpportunityOperatorService NewOperatorService(
        CoachPostgresHarness harness,
        CoachDbContext db,
        string callerId) =>
        new(db,
            new TestUserScope(callerId),
            new TestOptionsMonitor<CoachOpportunityOptions>(new CoachOpportunityOptions
            {
                Enabled = true,
                OperatorSurface = new CoachOpportunityOperatorSurfaceOptions { Enabled = true }
            }),
            new TestOptionsMonitor<CoachOptions>(new CoachOptions
            {
                Enabled = true,
                AllowedUserProfileIds = [callerId]
            }),
            harness.Time,
            NullLogger<CoachOpportunityOperatorService>.Instance);

    private static CoachOpportunitySignal Signal(
        string capability = CoachOpportunityCapabilityCodes.EntityLookupByName) =>
        new(CoachOpportunityKind.UnsupportedCapability,
            capability,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposeVocabularyRemoval,
            FailureCode: Api.Coach.Operations.CoachWriteFailureCodes.EntityNotOwned,
            StopReason: CoachStopReason.Completed,
            Evidence: new CoachOpportunityEvidencePointer("conv-1", "msg-2", 2, "msg-1", 1));

    /// <summary>
    /// A production recorder with its own service provider, so each one resolves a context on its
    /// own pooled connection.
    /// </summary>
    private static CoachOpportunityRecorder NewRecorder(
        CoachPostgresHarness harness,
        string userProfileId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(harness.DbOptions);
        services.AddScoped<CoachDbContext>(sp =>
            new CoachDbContext(sp.GetRequiredService<DbContextOptions<CoachDbContext>>()));

        var provider = services.BuildServiceProvider();

        return new CoachOpportunityRecorder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestUserScope(userProfileId),
            new CoachToolRegistry(new CoachOptions
            {
                Enabled = true,
                DurableHistory = new CoachFeatureSwitch { Enabled = true },
                SamOverlay = new CoachFeatureSwitch { Enabled = true },
                SamReadTools = new CoachFeatureSwitch { Enabled = true },
                SamWriteTools = new CoachFeatureSwitch { Enabled = true }
            }),
            new TestOptionsMonitor<CoachOpportunityOptions>(
                new CoachOpportunityOptions { Enabled = true }),
            new TestOptionsMonitor<SentenceStudio.Api.Coach.Reports.CoachResponseReportOptions>(
                new SentenceStudio.Api.Coach.Reports.CoachResponseReportOptions { Enabled = true }),
            harness.Time,
            NullLogger<CoachOpportunityRecorder>.Instance);
    }
}
