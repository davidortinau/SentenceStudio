using CoreSync;
using CoreSync.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentenceStudio;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Static guard for the dual-provider migration pair that gives the legacy
/// Conversation activity an owner column. A SQLite copy missing
/// <c>[Migration]</c> is invisible to EF and silently no-ops on mobile — the
/// bug that shipped twice (RefreshToken 2026-05-03, ActivitySession
/// 2026-07-02).
/// </summary>
public sealed class ConversationOwnerScopeMigrationTests
{
    private const string MigrationId = "20260817021500_AddConversationOwnerScope";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.Shared")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repository root");
        return dir!.FullName;
    }

    private static string MigrationPath(string relativeDir) => Path.Combine(
        RepoRoot(),
        "src",
        "SentenceStudio.Shared",
        relativeDir.Replace('/', Path.DirectorySeparatorChar),
        $"{MigrationId}.cs");

    [Theory]
    [InlineData("Migrations", "text")]
    [InlineData("Migrations/Sqlite", "TEXT")]
    public void BothProviderMigrationsExistWithDiscoveryAttributes(string relativeDir, string columnType)
    {
        var path = MigrationPath(relativeDir);
        File.Exists(path).Should().BeTrue($"the {relativeDir} migration must exist at {path}");

        var source = File.ReadAllText(path);
        source.Should().Contain("[DbContext(typeof(ApplicationDbContext))]");
        source.Should().Contain($"[Migration(\"{MigrationId}\")]",
            "without this attribute EF never discovers the migration and silently skips it on mobile");
        source.Should().Contain($"type: \"{columnType}\"", "provider column types must not be swapped");
        source.Should().Contain("nullable: true",
            "existing rows have no known owner, so the column must be a nullable add");
        source.Should().NotContain("Sql(",
            "guessing ownership via backfill would hand one learner's transcript to another");
    }

    [Theory]
    [InlineData("Migrations")]
    [InlineData("Migrations/Sqlite")]
    public void MigrationIsAddColumnOnlyAndReversible(string relativeDir)
    {
        var source = File.ReadAllText(MigrationPath(relativeDir));

        source.Should().Contain("AddColumn<string>");
        source.Should().Contain("table: \"Conversation\"");
        source.Should().Contain("table: \"ConversationChunk\"");
        source.Should().Contain("CreateIndex");

        source.Should().Contain("DropColumn");
        source.Should().Contain("DropIndex", "Down must undo the indexes it created");

        source.Should().NotContain("DropTable");
        source.Should().NotContain("AlterColumn");
        source.Should().NotContain("UPDATE ");
        source.Should().NotContain("DELETE ");
    }

    [Theory]
    [InlineData("Migrations")]
    [InlineData("Migrations/Sqlite")]
    public void ModelSnapshotCarriesOwnerColumnForBothProviders(string relativeDir)
    {
        var path = Path.Combine(
            RepoRoot(),
            "src",
            "SentenceStudio.Shared",
            relativeDir.Replace('/', Path.DirectorySeparatorChar),
            "ApplicationDbContextModelSnapshot.cs");

        var source = File.ReadAllText(path);
        var conversation = SnapshotSection(source, "SentenceStudio.Shared.Models.Conversation\"");
        var chunk = SnapshotSection(source, "SentenceStudio.Shared.Models.ConversationChunk\"");

        conversation.Should().Contain("b.Property<string>(\"UserProfileId\")");
        conversation.Should().Contain("b.HasIndex(\"UserProfileId\")");
        chunk.Should().Contain("b.Property<string>(\"UserProfileId\")");
        chunk.Should().Contain("b.HasIndex(\"UserProfileId\")");
    }

    private static string SnapshotSection(string source, string entityMarker)
    {
        var start = source.IndexOf(entityMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"the snapshot must configure {entityMarker}");

        var end = source.IndexOf("modelBuilder.Entity(", start + entityMarker.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}

/// <summary>
/// CoreSync ships Conversation and ConversationChunk between the device store
/// and the server. These tests pin that the new owner column travels with the
/// row (so a synced conversation stays attributable) and that a null legacy
/// owner is preserved as null rather than being materialized as somebody.
///
/// Note the deliberate limitation documented in
/// <c>docs/conversation-owner-scoping.md</c>: this repository applies no
/// CoreSync table filters to ANY of its 18 synced tables, so sync fidelity —
/// not sync filtering — is what these tests can assert. The repository is the
/// enforcement boundary for reads.
/// </summary>
public sealed class ConversationSyncOwnerFidelityTests
{
    [Fact]
    public async Task SyncedConversationTables_ExposeOwnerColumn()
    {
        const string connectionString = "Data Source=CoreSyncConversationOwner;Mode=Memory;Cache=Shared";

        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(keepAlive)
            .Options;

        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var configuration = new SqliteSyncConfigurationBuilder(connectionString)
            .ConfigureSyncTables()
            .Build();
        var provider = new SqliteSyncProvider(configuration, ProviderMode.Remote);
        await provider.ApplyProvisionAsync();

        (await GetColumnNamesAsync(keepAlive, "Conversation"))
            .Should().Contain(nameof(Conversation.UserProfileId));
        (await GetColumnNamesAsync(keepAlive, "ConversationChunk"))
            .Should().Contain(nameof(ConversationChunk.UserProfileId));
    }

    [Fact]
    public async Task SyncRoundTrip_PreservesOwnerAndLeavesLegacyRowsUnowned()
    {
        var local = await CreateProvisionedStoreAsync($"ConvSyncLocal{Guid.NewGuid():N}", ProviderMode.Local);
        var remote = await CreateProvisionedStoreAsync($"ConvSyncRemote{Guid.NewGuid():N}", ProviderMode.Remote);
        await using var localKeepAlive = local.KeepAlive;
        await using var remoteKeepAlive = remote.KeepAlive;

        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = new ApplicationDbContext(local.Options))
        {
            db.Conversations.Add(new Conversation
            {
                Id = "owned",
                Language = "ko",
                CreatedAt = now,
                UserProfileId = "user-a"
            });
            db.Conversations.Add(new Conversation
            {
                Id = "legacy",
                Language = "ko",
                CreatedAt = now,
                UserProfileId = null
            });
            db.ConversationChunks.Add(new ConversationChunk
            {
                Id = "owned-chunk",
                ConversationId = "owned",
                Text = "안녕하세요",
                SentTime = now,
                UserProfileId = "user-a"
            });
            db.ConversationChunks.Add(new ConversationChunk
            {
                Id = "legacy-chunk",
                ConversationId = "legacy",
                Text = "legacy",
                SentTime = now,
                UserProfileId = null
            });
            await db.SaveChangesAsync();
        }

        await new SyncAgent(local.Provider, remote.Provider).SynchronizeAsync();

        await using (var db = new ApplicationDbContext(remote.Options))
        {
            (await db.Conversations.SingleAsync(c => c.Id == "owned")).UserProfileId
                .Should().Be("user-a", "the owner must survive the trip or the row becomes unattributable");
            (await db.Conversations.SingleAsync(c => c.Id == "legacy")).UserProfileId
                .Should().BeNull("sync must not invent an owner for a legacy row");
            (await db.ConversationChunks.SingleAsync(cc => cc.Id == "owned-chunk")).UserProfileId
                .Should().Be("user-a");
            (await db.ConversationChunks.SingleAsync(cc => cc.Id == "legacy-chunk")).UserProfileId
                .Should().BeNull();
        }
    }

    private static async Task<ProvisionedStore> CreateProvisionedStoreAsync(string storeName, ProviderMode mode)
    {
        var connectionString = $"Data Source={storeName};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(keepAlive)
            .Options;

        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var configuration = new SqliteSyncConfigurationBuilder(connectionString)
            .ConfigureSyncTables()
            .Build();
        var provider = new SqliteSyncProvider(configuration, mode);
        await provider.ApplyProvisionAsync();

        return new ProvisionedStore(keepAlive, options, provider);
    }

    private sealed record ProvisionedStore(
        SqliteConnection KeepAlive,
        DbContextOptions<ApplicationDbContext> Options,
        ISyncProvider Provider);

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
