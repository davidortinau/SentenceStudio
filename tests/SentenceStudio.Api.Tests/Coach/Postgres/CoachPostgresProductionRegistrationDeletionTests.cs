using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Deletion;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Account erasure across two contexts, with the application context registered the way the API
/// host registers it rather than the way a test finds convenient.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second cross-context class.</b> <see cref="CoachPostgresCrossContextDeletionTests"/>
/// registers <c>ApplicationDbContext</c> with a plain <c>AddDbContext</c> + <c>UseNpgsql</c>. That
/// proves the enlistment shares a connection and a transaction, but it cannot prove the part the
/// production registration adds: <c>builder.AddNpgsqlDbContext</c> pools the context and turns on
/// <c>NpgsqlRetryingExecutionStrategy</c>, and EF refuses <em>any</em> operation on a retrying
/// strategy while a user-initiated transaction is current. Replaying a statement inside an
/// already-aborted transaction cannot mean anything, so EF throws rather than guess.
/// </para>
/// <para>
/// That is why <c>SharedConnectionCoachDeletionEnlistment</c> swaps in
/// <c>NonRetryingExecutionStrategy</c> before it enlists. Under a plain <c>AddDbContext</c> that
/// swap is dead code — the strategy was never retrying — so the only registration that can fail
/// when the swap is removed is this one. Delete the <c>ReplaceService</c> call and every erasure
/// test in this class fails; delete it with only the plain-registration class present and
/// everything stays green while production breaks on the first account deletion.
/// </para>
/// <para>
/// The failure is quiet, too, which is the other reason to pin it here.
/// <c>ConversationRepository.DeleteOwnedAsync</c> catches its own exceptions and reports zero rows,
/// so a strategy rejection does not surface as a strategy rejection: it surfaces as a legacy delete
/// that silently did nothing, caught one step later by the contributor's own read-back guard.
/// </para>
/// </remarks>
public sealed class CoachPostgresProductionRegistrationDeletionTests : IAsyncLifetime
{
    /// <summary>The Aspire connection name the API host resolves the application database by.</summary>
    private const string ConnectionName = "sentencestudio";

    private static readonly CoachOwner Owner = CoachOwner.ForUser("prodshape-owner");
    private static readonly CoachOwner Bystander = CoachOwner.ForUser("prodshape-bystander");

    private CoachPostgresHarness _harness = null!;
    private IHost _host = null!;
    private IServiceScope _scope = null!;

    /// <summary>
    /// The lifetime the registration gave <c>DbContextOptions&lt;ApplicationDbContext&gt;</c>.
    /// Singleton means the context is pooled; scoped means it is not.
    /// </summary>
    private ServiceLifetime _optionsLifetime;

    private bool _registeredAContextPool;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("prodshape", withApplicationSchema: true);

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{ConnectionName}"] = _harness.ConnectionString
        });

        // The API host's own call, verbatim apart from the host-level concerns below. Reaching for
        // AddDbContextPool + EnableRetryOnFailure by hand would be a guess at what this does; using
        // it means the test tracks the registration rather than a memory of it.
        builder.AddNpgsqlDbContext<ApplicationDbContext>(
            ConnectionName,
            configureSettings: settings =>
            {
                // Health checks, tracing and metrics are host concerns with nothing to say about
                // erasure. Retry is deliberately left at its default of enabled: the retrying
                // execution strategy is the production condition this whole class exists for.
                settings.DisableHealthChecks = true;
                settings.DisableTracing = true;
                settings.DisableMetrics = true;
            },
            configureDbContextOptions: options =>
                options.ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        // Read off the registration before it is built, so the fidelity test below can assert the
        // shape rather than assume it.
        _optionsLifetime = builder.Services
            .First(descriptor => descriptor.ServiceType == typeof(DbContextOptions<ApplicationDbContext>))
            .Lifetime;

        _registeredAContextPool = builder.Services.Any(descriptor =>
            descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.Name.StartsWith("IDbContextPool", StringComparison.Ordinal));

        _host = builder.Build();
        _scope = _host.Services.CreateScope();
    }

    public async Task DisposeAsync()
    {
        _scope?.Dispose();
        _host?.Dispose();

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// The registration this class runs against really is the production one.
    /// </summary>
    /// <remarks>
    /// Without this, a future change that quietly turned retries off — or dropped pooling — would
    /// make every other test here pass for the wrong reason, and the strategy replacement would go
    /// back to being untested while looking covered.
    /// </remarks>
    [PostgresFact]
    public void The_registration_under_test_is_the_production_shape()
    {
        _registeredAContextPool.Should().BeTrue(
            "AddNpgsqlDbContext pools the context, and a pooled context resolves its options from a "
            + "different lifetime than the enlistment would see under AddDbContext");

        _optionsLifetime.Should().Be(
            ServiceLifetime.Singleton,
            "pooling registers DbContextOptions as a singleton — the enlistment resolves that "
            + "service, so the lifetime is part of what is under test");

        using var scope = _host.Services.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        application.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue(
            "the retrying strategy is what makes a user-initiated transaction illegal, and it is "
            + "the only reason the enlistment has to replace anything");
    }

    /// <summary>
    /// The enlistment itself, under the production registration: same connection, same
    /// transaction, and a write that is actually allowed to run.
    /// </summary>
    /// <remarks>
    /// The write is the assertion that matters. A retrying strategy throws
    /// <c>InvalidOperationException</c> on the first operation once a transaction is current, so a
    /// context that had been enlisted but not re-strategised would fail here rather than delete.
    /// </remarks>
    [PostgresFact]
    public async Task The_enlistment_joins_the_coach_transaction_and_can_write_in_it()
    {
        await SeedAsync(Owner);

        await using var coachDb = _harness.NewContext();
        await using var transaction = await coachDb.Database.BeginTransactionAsync();

        await using var enlistment = await NewEnlistment().EnlistAsync(coachDb, transaction);

        enlistment.IsActive.Should().BeTrue(
            "both contexts address the same database, so one transaction is available");

        using (enlistment.Activate())
        {
            var application = AmbientApplicationDbContext.Current;
            application.Should().NotBeNull("the enlisted context is what owner-scoped repositories join");

            application!.Database.GetDbConnection().Should().BeSameAs(
                coachDb.Database.GetDbConnection(),
                "PostgreSQL will not span two connections without two-phase commit, so sharing the "
                + "connection is what makes one transaction genuinely available");
            application.Database.CurrentTransaction.Should().NotBeNull(
                "the context has to be inside the coordinator's transaction, not merely beside it");

            application.Conversations.RemoveRange(
                await application.Conversations
                    .Where(c => c.UserProfileId == Owner.UserProfileId)
                    .ToListAsync());

            // Throws under a retrying strategy. That it does not throw here is the whole point.
            (await application.SaveChangesAsync()).Should().BeGreaterThan(0);
        }

        (await CountAsync(Owner)).Conversations.Should().Be(
            1,
            "a connection outside the transaction must not see the delete before it commits");

        await transaction.RollbackAsync();

        (await CountAsync(Owner)).Conversations.Should().Be(
            1, "the rollback covers the application context's work as well as the coach context's");
    }

    /// <summary>A whole erasure, through the coordinator, under the production registration.</summary>
    [PostgresFact]
    public async Task A_successful_erasure_removes_every_owned_row_and_leaves_other_owners_alone()
    {
        await SeedAsync(Owner);
        await SeedAsync(Bystander);

        await using var coachDb = _harness.NewContext();
        var report = await NewService(coachDb, [NewLegacyContributor(), NewCheckpointContributor(coachDb)])
            .DeleteAllForOwnerAsync(Owner);

        report.Succeeded.Should().BeTrue(
            report.FailureCode
            ?? "the erasure has to run to completion with the application context enlisted");
        report.DataWasRemoved.Should().BeTrue();
        report.DeletesByContributor.Keys.Should().Contain(["LegacyConversation", "CoachCheckpoint"]);

        (await CountAsync(Owner)).Should().Be(new Counts(0, 0, 0));
        (await CountAsync(Bystander)).Should().Be(
            new Counts(1, 2, 1), "another learner's rows are never in scope for this erasure");
    }

    /// <summary>
    /// The regression, under the production registration: a failure after the legacy delete has to
    /// put the legacy rows back, because the caller is about to say nothing was removed.
    /// </summary>
    [PostgresFact]
    public async Task A_failure_after_the_legacy_delete_rolls_the_legacy_rows_back_too()
    {
        await SeedAsync(Owner);
        await SeedAsync(Bystander);

        var before = await CountAsync(Owner);
        before.Should().Be(new Counts(1, 2, 1));

        await using var coachDb = _harness.NewContext();
        var saboteur = new OutsideObservingSaboteur(_harness.ConnectionString, Owner.UserProfileId);

        var report = await NewService(coachDb, [NewLegacyContributor(), NewCheckpointContributor(coachDb), saboteur])
            .DeleteAllForOwnerAsync(Owner);

        report.Succeeded.Should().BeFalse();
        report.FailureCode.Should().Be("deletion_failed");
        report.DataWasRemoved.Should().BeFalse(
            "the rollback restored every row, so telling the learner nothing was removed is honest");

        saboteur.ConversationsVisibleOutsideTransaction.Should().BeGreaterThan(
            0,
            "a connection outside the transaction must still see the learner's conversations while "
            + "the erasure is in flight — if it sees zero, the legacy delete committed on a "
            + "connection of its own and no rollback can bring those rows back");

        report.DeletesByContributor.Should().ContainKey("LegacyConversation");
        report.DeletesByContributor["LegacyConversation"].Should().Be(
            3,
            "the legacy delete ran inside the failing pass — one conversation and two chunks — so "
            + "the rollback is what put them back, not a deferral that never touched them");

        (await CountAsync(Owner)).Should().Be(before);
        (await CountAsync(Bystander)).Should().Be(new Counts(1, 2, 1));
    }

    // --- wiring ---------------------------------------------------------------

    private SharedConnectionCoachDeletionEnlistment NewEnlistment() =>
        new(_scope.ServiceProvider, NullLogger<SharedConnectionCoachDeletionEnlistment>.Instance);

    private CoachDataDeletionService NewService(
        CoachDbContext coachDb,
        IEnumerable<ICoachDataDeletionContributor> contributors) =>
        new(coachDb, contributors, NewEnlistment(), NullLogger<CoachDataDeletionService>.Instance);

    private LegacyConversationDeletionContributor NewLegacyContributor() =>
        new(
            new ConversationRepository(_host.Services, NullLogger<ConversationRepository>.Instance),
            NullLogger<LegacyConversationDeletionContributor>.Instance);

    private static CoachCheckpointDeletionContributor NewCheckpointContributor(CoachDbContext coachDb) =>
        new(coachDb, NullLogger<CoachCheckpointDeletionContributor>.Instance);

    // --- seeding and counting -------------------------------------------------

    private async Task SeedAsync(CoachOwner owner)
    {
        await using (var coachDb = _harness.NewContext())
        {
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

    private async Task<Counts> CountAsync(CoachOwner owner)
    {
        var id = owner.UserProfileId;

        return new Counts(
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "Conversation" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "ConversationChunk" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "CoachSession" WHERE "UserProfileId" = '{id}'"""));
    }

    private readonly record struct Counts(long Conversations, long Chunks, long CoachSessions);

    /// <summary>
    /// Reads the learner's legacy conversations from a connection of its own, then fails.
    /// </summary>
    /// <remarks>
    /// The read is the point: a non-zero count while the coordinator's transaction is still open
    /// proves the legacy delete is sitting uncommitted inside that transaction rather than having
    /// been committed on a second connection. Readers never block on deleted-but-uncommitted rows
    /// under PostgreSQL's default isolation, so this cannot deadlock.
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
