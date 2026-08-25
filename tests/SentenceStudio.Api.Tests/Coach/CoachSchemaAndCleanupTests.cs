using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Coach migration discovery and model-snapshot health, plus the cleanup pass a future
/// scheduled host will call.
/// </summary>
public class CoachSchemaAndCleanupTests
{
    [Fact]
    public void CoachMigration_IsDiscoverable()
    {
        using var db = NewNpgsqlModelContext();

        var migrations = db.Database.GetMigrations().ToArray();

        migrations.Should().ContainSingle(m => m.EndsWith("_InitialCoachSchema", StringComparison.Ordinal),
            "a coach migration that EF cannot discover is silently skipped by MigrateAsync");
    }

    [Fact]
    public void CoachModel_HasNoPendingChanges()
    {
        using var db = NewNpgsqlModelContext();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "the model snapshot must match the entity configuration, or the next migration silently drifts");
    }

    [Fact]
    public void CoachModel_UsesJsonbForNormalizedColumnsOnPostgres()
    {
        using var db = NewNpgsqlModelContext();

        var session = db.Model.FindEntityType(typeof(CoachSession))!;

        session.FindProperty(nameof(CoachSession.ActiveConstraintsJson))!.GetColumnType().Should().Be("jsonb");
        session.FindProperty(nameof(CoachSession.ProtectedAgentSession))!.GetColumnType().Should().NotBe("jsonb",
            "the encrypted agent session is ciphertext, not JSON");
    }

    [Fact]
    public void AgentConfigVersionColumn_IsABoundedString()
    {
        using var db = NewNpgsqlModelContext();

        var property = db.Model.FindEntityType(typeof(CoachSession))!
            .FindProperty(nameof(CoachSession.AgentConfigVersion))!;

        property.ClrType.Should().Be(typeof(string),
            "the stamp is copied verbatim from the operator's Coach:AgentConfigVersion value");
        property.GetMaxLength().Should().Be(CoachOptionsValidator.MaxAgentConfigVersionLength);
        property.IsNullable.Should().BeFalse();
    }

    /// <summary>
    /// Builds the model against Npgsql without opening a connection, so migration and
    /// snapshot assertions run against the provider the migration was generated for.
    /// </summary>
    private static CoachDbContext NewNpgsqlModelContext() =>
        new(new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql("Host=localhost;Database=sentencestudio_coach_design;Username=postgres")
            .Options);

    [Fact]
    public void CoachDbContext_OwnsOnlyCoachTables()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var tables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => t is not null)
            .ToArray();

        tables.Should().BeEquivalentTo(
            new[]
            {
                "CoachSession", "CoachPlanRevision", "CoachUsage",
                "CoachConversation", "CoachMessage", "CoachTurnOperation",
                "CoachMemoryFact",
                "CoachWriteOperation", "CoachWriteAudit",
                "CoachOpportunity",
                "CoachResponseReport"
            },
            "coach state is server-only and must not pull learner learning tables into its migrations");
    }

    [Fact]
    public async Task Cleanup_RemovesExpiredSessionsAndStaleAudit()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var stale = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, stale.Id, CoachPersistenceSamples.RevisionInput());
        await harness.NewUsageStore(db).RecordRunAsync(
            CoachPersistenceSamples.OwnerUserId, new DateOnly(2026, 1, 1), 10, 10, 0.01m);

        harness.Time.Advance(TimeSpan.FromDays(31));
        var fresh = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var result = await harness.NewCleanupService(db).RunAsync();

        result.ExpiredSessionsDeleted.Should().Be(1);
        result.RevisionsDeleted.Should().Be(1);
        result.UsageRowsDeleted.Should().Be(1);

        var remaining = await db.CoachSessions.AsNoTracking().ToListAsync();
        remaining.Should().ContainSingle().Which.Id.Should().Be(fresh.Id);
    }

    [Fact]
    public async Task Cleanup_LeavesLiveDataAlone()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var live = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, live.Id, CoachPersistenceSamples.RevisionInput());

        var result = await harness.NewCleanupService(db).RunAsync();

        result.IsEmpty.Should().BeTrue();
        (await db.CoachSessions.CountAsync()).Should().Be(1);
        (await db.CoachPlanRevisions.CountAsync()).Should().Be(1);
    }
}
