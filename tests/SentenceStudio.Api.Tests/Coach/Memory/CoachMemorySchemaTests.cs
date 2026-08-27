using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Migration discovery and column shape for the memory table.
/// </summary>
/// <remarks>
/// A migration EF cannot discover is silently skipped by <c>MigrateAsync</c>, which is how a
/// missing table reaches production looking like a healthy deploy. These assertions run against
/// the Npgsql model without opening a connection, so they hold in CI with no database present.
/// </remarks>
public sealed class CoachMemorySchemaTests
{
    private const string MigrationSuffix = "_AddCoachMemoryFacts";
    private const string PrecedingMigration = "20260817023329_AddCoachConversationHistory";

    private static CoachDbContext NewNpgsqlModelContext() =>
        new(new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql("Host=localhost;Database=sentencestudio_coach_design;Username=postgres")
            .Options);

    [Fact]
    public void TheMemoryMigrationIsDiscoverable()
    {
        using var db = NewNpgsqlModelContext();

        db.Database.GetMigrations()
            .Should().ContainSingle(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void TheMemoryMigrationIsAdditiveAndSortsAfterTheHistoryMigration()
    {
        using var db = NewNpgsqlModelContext();

        var migrations = db.Database.GetMigrations().ToArray();
        var memory = migrations.Single(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));

        migrations.Should().Contain(PrecedingMigration);
        string.CompareOrdinal(memory, PrecedingMigration).Should().BePositive(
            "the memory table is additive: it must apply after the history migration, never rewrite it");

        // Deliberately not asserted to be the newest migration. Being additive means applying
        // after what came before, not forbidding anything from coming after — an assertion that
        // this is the last migration would fail every time an unrelated column is added, which
        // says nothing about whether this migration is additive.
    }

    [Fact]
    public void TheSnapshotCarriesTheMemoryTableAfterTheHistoryTables()
    {
        using var db = NewNpgsqlModelContext();

        // The snapshot is what the next `migrations add` diffs against. If it lost the memory
        // entity, the following migration would try to create the table a second time.
        var tables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => t is not null)
            .ToArray();

        tables.Should().Contain("CoachMemoryFact");
        tables.Should().Contain(["CoachConversation", "CoachMessage", "CoachTurnOperation"],
            "the memory table is added on top of the history tables, not instead of them");
    }

    [Fact]
    public void TheContextOwnsExactlyElevenTables()
    {
        using var db = NewNpgsqlModelContext();

        var tables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => t is not null)
            .Distinct()
            .ToArray();

        // Three session tables, three history tables, one memory table, two write-ledger tables,
        // one opportunity ledger table, one learner-report table. Pinning the count means a stray
        // entity cannot ride into the coach migrations unnoticed.
        tables.Should().HaveCount(11);
        tables.Should().BeEquivalentTo(
        [
            "CoachSession", "CoachPlanRevision", "CoachUsage",
            "CoachConversation", "CoachMessage", "CoachTurnOperation",
            "CoachMemoryFact",
            "CoachWriteOperation", "CoachWriteAudit",
            "CoachOpportunity", "CoachResponseReport"
        ]);
    }

    [Fact]
    public void TheModelHasNoPendingChanges()
    {
        using var db = NewNpgsqlModelContext();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a drifted snapshot means the next migration silently omits these columns");
    }

    [Fact]
    public void EveryTimestampIsStoredWithTimeZoneOnPostgres()
    {
        using var db = NewNpgsqlModelContext();
        var entity = db.Model.FindEntityType(typeof(CoachMemoryFact))!;

        var timestamps = entity.GetProperties()
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
            .ToArray();

        timestamps.Should().NotBeEmpty();
        timestamps.Should().OnlyContain(p => p.GetColumnType() == "timestamp with time zone");
    }

    [Fact]
    public void TheProtectedValueIsCiphertextNotJson()
    {
        using var db = NewNpgsqlModelContext();

        db.Model.FindEntityType(typeof(CoachMemoryFact))!
            .FindProperty(nameof(CoachMemoryFact.ProtectedValue))!
            .GetColumnType()
            .Should().NotBe("jsonb", "the value is opaque ciphertext and must never be queryable as JSON");
    }

    [Fact]
    public void OneActiveFactPerOwnerKindAndScopeIsEnforcedByAFilteredUniqueIndex()
    {
        using var db = NewNpgsqlModelContext();
        var entity = db.Model.FindEntityType(typeof(CoachMemoryFact))!;

        var index = entity.GetIndexes().Single(i => i.IsUnique);

        index.Properties.Select(p => p.Name).Should().Equal(
            nameof(CoachMemoryFact.UserProfileId),
            nameof(CoachMemoryFact.Kind),
            nameof(CoachMemoryFact.ScopeKey));

        // Superseded and expired rows are kept for provenance, so the uniqueness rule has to apply
        // to active rows only.
        index.GetFilter().Should().Contain($"= {(int)CoachMemoryStatus.Active}");
    }

    [Fact]
    public void OwnerScopedLookupsAreIndexed()
    {
        using var db = NewNpgsqlModelContext();
        var entity = db.Model.FindEntityType(typeof(CoachMemoryFact))!;

        var indexes = entity.GetIndexes()
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToArray();

        indexes.Should().Contain($"{nameof(CoachMemoryFact.UserProfileId)},{nameof(CoachMemoryFact.SourceConversationId)}",
            "deleting a conversation has to find its facts without scanning the table");
        indexes.Should().OnlyContain(i => i.StartsWith(nameof(CoachMemoryFact.UserProfileId), StringComparison.Ordinal),
            "every index has to lead with the owner, or a query can be written that crosses accounts cheaply");
    }

    [Fact]
    public void ThereIsNoForeignKeyToTheConversationTable()
    {
        using var db = NewNpgsqlModelContext();

        db.Model.FindEntityType(typeof(CoachMemoryFact))!
            .GetForeignKeys()
            .Should().BeEmpty(
                "a database cascade would delete memory without going through the change notifier, "
                + "leaving forgotten preferences alive inside serialized checkpoints");
    }

    [Fact]
    public void TheConcurrencyTokenIsTheVersionColumn()
    {
        using var db = NewNpgsqlModelContext();

        db.Model.FindEntityType(typeof(CoachMemoryFact))!
            .FindProperty(nameof(CoachMemoryFact.Version))!
            .IsConcurrencyToken
            .Should().BeTrue("every write echoes a version, and a mismatch has to be a conflict rather than an overwrite");
    }
}
