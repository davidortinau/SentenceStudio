using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Deletion;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Account erasure across the two contexts that share one PostgreSQL database.
/// </summary>
/// <remarks>
/// <para>
/// Coach state and the legacy Conversation activity live in the same database behind different
/// contexts, and a context brings its own connection. That is the shape that produced the defect
/// these tests exist for: <c>ConversationRepository.DeleteOwnedAsync</c> resolved its own context
/// from a fresh scope and committed the moment it saved, so a coach failure later in the pass
/// rolled back only the coach half — and the endpoint told the learner "Nothing was removed" over
/// conversations that were already destroyed.
/// </para>
/// <para>
/// No SQLite test can demonstrate this. A shared-cache SQLite database can commit and roll back a
/// transaction perfectly well, but the thing under test is whether two <em>separate contexts</em>
/// end up on one connection and one transaction against a real server. That is why this class
/// creates the application schema and the coach migrations in a single throwaway
/// <c>coach_it_*</c> database, and why the decisive assertion is made from a third connection that
/// is outside the transaction entirely.
/// </para>
/// </remarks>
public sealed class CoachPostgresCrossContextDeletionTests : IAsyncLifetime
{
    private static readonly CoachOwner Owner = CoachOwner.ForUser("xctx-owner");
    private static readonly CoachOwner Bystander = CoachOwner.ForUser("xctx-bystander");

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _root = null!;
    private IServiceScope _scope = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("xctx", withApplicationSchema: true);

        var services = new ServiceCollection();
        services.AddLogging();

        // Scoped options and a scoped context, exactly as the API host registers them. The
        // lifetime matters: the enlistment resolves DbContextOptions<ApplicationDbContext> from
        // the scope it was constructed in, and a root-provider resolution of a scoped service
        // would throw in production long before any of this logic ran.
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseNpgsql(_harness.ConnectionString);
            options.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        _root = services.BuildServiceProvider();
        _scope = _root.CreateScope();
    }

    public async Task DisposeAsync()
    {
        _scope?.Dispose();

        if (_root is not null)
        {
            await _root.DisposeAsync();
        }

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// The regression. A failure after the legacy delete has to leave the learner's conversations
    /// exactly where they were, because the caller is about to tell them nothing was removed.
    /// </summary>
    [PostgresFact]
    public async Task A_failure_after_the_legacy_delete_restores_the_legacy_rows_too()
    {
        await SeedAsync(Owner);
        await SeedAsync(Bystander);

        var before = await CountAsync(Owner);
        before.Conversations.Should().Be(1);
        before.Chunks.Should().Be(2);
        before.CoachSessions.Should().Be(1);

        await using var coachDb = _harness.NewContext();

        // Probes from a connection of its own, then fails the way a contributor would if its table
        // were locked or its statement were rejected.
        var saboteur = new OutsideObservingSaboteur(_harness.ConnectionString, Owner.UserProfileId);

        var report = await NewService(coachDb, [NewLegacyContributor(), NewCheckpointContributor(coachDb), saboteur])
            .DeleteAllForOwnerAsync(Owner);

        report.Succeeded.Should().BeFalse();
        report.FailureCode.Should().Be("deletion_failed");

        // The caller's message is built from this. "Nothing was removed" is only allowed to be
        // said when it is true, and after a clean rollback it is.
        report.DataWasRemoved.Should().BeFalse(
            "the rollback restored every row, so telling the learner nothing was removed is honest");

        saboteur.ConversationsVisibleOutsideTransaction.Should().BeGreaterThan(
            0,
            "a connection outside the transaction must still see the learner's conversations while "
            + "the erasure is in flight — if it sees zero, the legacy delete committed on a "
            + "connection of its own and no rollback can bring those rows back");

        // Guards the other way round: if the legacy contributor had simply been deferred past the
        // failure it would also have left the rows intact, and the assertion above would pass
        // without proving anything about the transaction. It has to have deleted them and had them
        // put back.
        report.DeletesByContributor.Should().ContainKey("LegacyConversation");
        report.DeletesByContributor["LegacyConversation"].Should().Be(
            3,
            "the legacy delete ran inside the failing pass — one conversation and two chunks");

        (await CountAsync(Owner)).Should().Be(
            before,
            "a partial erasure is the worst outcome available: the learner is told the deletion "
            + "failed while their conversations are already gone");

        (await CountAsync(Bystander)).Should().Be(
            new Counts(1, 2, 1),
            "another learner's rows are never in scope for this erasure");
    }

    /// <summary>The retry after that rollback has to finish the job.</summary>
    [PostgresFact]
    public async Task A_retry_after_the_rollback_completes_the_erasure()
    {
        await SeedAsync(Owner);

        await using (var failing = _harness.NewContext())
        {
            var report = await NewService(
                    failing,
                    [
                        NewLegacyContributor(),
                        NewCheckpointContributor(failing),
                        new OutsideObservingSaboteur(_harness.ConnectionString, Owner.UserProfileId)
                    ])
                .DeleteAllForOwnerAsync(Owner);

            report.Succeeded.Should().BeFalse();
        }

        await using var retry = _harness.NewContext();
        var second = await NewService(retry, [NewLegacyContributor(), NewCheckpointContributor(retry)])
            .DeleteAllForOwnerAsync(Owner);

        second.Succeeded.Should().BeTrue(second.FailureCode ?? "the retry must finish what the rollback undid");
        (await CountAsync(Owner)).Should().Be(new Counts(0, 0, 0));
    }

    [PostgresFact]
    public async Task A_successful_erasure_removes_every_owned_row_in_both_contexts_and_nothing_else()
    {
        await SeedAsync(Owner);
        await SeedAsync(Bystander);
        await SeedOwnerlessAsync();

        await using var coachDb = _harness.NewContext();
        var report = await NewService(coachDb, [NewLegacyContributor(), NewCheckpointContributor(coachDb)])
            .DeleteAllForOwnerAsync(Owner);

        report.Succeeded.Should().BeTrue(report.FailureCode ?? "deletion should succeed");
        report.DataWasRemoved.Should().BeTrue();
        report.DeletesByContributor.Keys.Should().Contain(["LegacyConversation", "CoachCheckpoint"]);

        (await CountAsync(Owner)).Should().Be(new Counts(0, 0, 0));
        (await CountAsync(Bystander)).Should().Be(new Counts(1, 2, 1));

        // Ownerless legacy rows predate owner scoping. Attributing them to whoever happens to be
        // leaving would be a guess, and the cost of guessing wrong is a stranger's data.
        (await CountOwnerlessAsync()).Should().Be(
            (1, 1),
            "rows nobody can be proven to own are never swept up by somebody else's erasure");
    }

    [PostgresFact]
    public async Task An_empty_owner_touches_nothing_in_either_context()
    {
        await SeedAsync(Owner);
        var before = await CountAsync(Owner);

        await using var coachDb = _harness.NewContext();
        var report = await NewService(coachDb, [NewLegacyContributor(), NewCheckpointContributor(coachDb)])
            .DeleteAllForOwnerAsync(default);

        report.Succeeded.Should().BeFalse();
        report.FailureCode.Should().Be("no_owner");
        report.DataWasRemoved.Should().BeFalse();

        (await CountAsync(Owner)).Should().Be(
            before,
            "an absent owner is the one input that could empty the table if it were treated as a wildcard");
    }

    // --- wiring ---------------------------------------------------------------

    private CoachDataDeletionService NewService(
        CoachDbContext coachDb,
        IEnumerable<ICoachDataDeletionContributor> contributors) =>
        new(
            coachDb,
            contributors,
            new SharedConnectionCoachDeletionEnlistment(
                _scope.ServiceProvider,
                NullLogger<SharedConnectionCoachDeletionEnlistment>.Instance),
            NullLogger<CoachDataDeletionService>.Instance);

    private LegacyConversationDeletionContributor NewLegacyContributor() =>
        new(
            new ConversationRepository(_root, NullLogger<ConversationRepository>.Instance),
            NullLogger<LegacyConversationDeletionContributor>.Instance);

    private static CoachCheckpointDeletionContributor NewCheckpointContributor(CoachDbContext coachDb) =>
        new(coachDb, NullLogger<CoachCheckpointDeletionContributor>.Instance);

    // --- seeding and counting -------------------------------------------------

    private async Task SeedAsync(CoachOwner owner)
    {
        await using (var coachDb = _harness.NewContext())
        {
            // Written through the production store rather than by hand: the coach session carries
            // jsonb columns and protected payloads, and a hand-built row is both invalid on
            // PostgreSQL and a poor stand-in for what a learner actually leaves behind.
            await _harness.NewSessionStore(coachDb)
                .CreateAsync(owner.UserProfileId, CoachPersistenceSamples.CreateRequest());
        }

        await using var app = _harness.NewApplicationContext();
        var conversationId = Guid.NewGuid().ToString();

        app.Conversations.Add(new Conversation
        {
            Id = conversationId,
            UserProfileId = owner.UserProfileId,
            CreatedAt = DateTime.UtcNow
        });

        for (var i = 0; i < 2; i++)
        {
            app.ConversationChunks.Add(new ConversationChunk
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                UserProfileId = owner.UserProfileId,
                SentTime = DateTime.UtcNow,
                Author = "learner",
                Text = "seeded"
            });
        }

        await app.SaveChangesAsync();
    }

    private async Task SeedOwnerlessAsync()
    {
        await using var app = _harness.NewApplicationContext();
        var conversationId = Guid.NewGuid().ToString();

        app.Conversations.Add(new Conversation
        {
            Id = conversationId,
            UserProfileId = null,
            CreatedAt = DateTime.UtcNow
        });

        app.ConversationChunks.Add(new ConversationChunk
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            UserProfileId = null,
            SentTime = DateTime.UtcNow,
            Author = "learner",
            Text = "seeded"
        });

        await app.SaveChangesAsync();
    }

    private async Task<Counts> CountAsync(CoachOwner owner)
    {
        var id = owner.UserProfileId;

        return new Counts(
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "Conversation" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "ConversationChunk" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "CoachSession" WHERE "UserProfileId" = '{id}'"""));
    }

    private async Task<(long Conversations, long Chunks)> CountOwnerlessAsync() =>
        (await _harness.ScalarAsync<long>("""SELECT count(*) FROM "Conversation" WHERE "UserProfileId" IS NULL"""),
         await _harness.ScalarAsync<long>("""SELECT count(*) FROM "ConversationChunk" WHERE "UserProfileId" IS NULL"""));

    private readonly record struct Counts(long Conversations, long Chunks, long CoachSessions);

    /// <summary>
    /// Reads the learner's legacy conversations from a connection of its own, then fails.
    /// </summary>
    /// <remarks>
    /// The read is the point. It runs while the coordinator's transaction is still open, so a
    /// non-zero count proves the legacy delete is sitting uncommitted inside that transaction
    /// rather than having been committed on a second connection. Readers never block on
    /// deleted-but-uncommitted rows under PostgreSQL's default isolation, so this cannot deadlock.
    /// </remarks>
    private sealed class OutsideObservingSaboteur : ICoachDataDeletionContributor
    {
        private readonly string _connectionString;
        private readonly string _userProfileId;

        public OutsideObservingSaboteur(string connectionString, string userProfileId)
        {
            _connectionString = connectionString;
            _userProfileId = userProfileId;
        }

        public string Name => "Exploding";

        public long ConversationsVisibleOutsideTransaction { get; private set; } = -1;

        public async Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
        {
            ConversationsVisibleOutsideTransaction = await CountAsync(cancellationToken);
            throw new InvalidOperationException("contributor failed after the legacy delete");
        }

        private async Task<long> CountAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT count(*) FROM "Conversation" WHERE "UserProfileId" = @owner""";
            command.Parameters.Add(new NpgsqlParameter("owner", _userProfileId));

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
    }
}
