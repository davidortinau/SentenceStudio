using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SentenceStudio.Api;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Startup;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Tests.Startup;

public sealed class StartupSessionTests
{
    private const string ExpectedMigrationId = "20260927000100_ReadinessTest";

    [Fact]
    public void Backend_continuity_requires_a_new_startup_contract_from_pre_guarded_APIs()
    {
        ApiApplicationStartupContract.Current.Should().Be("api-exclusive-application-startup-v3");
    }

    [Fact]
    public async Task Same_named_databases_with_identical_migration_history_on_distinct_clusters_have_different_identities()
    {
        using var clusterA = IdentityConnection("sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        using var clusterB = IdentityConnection("sentencestudio", "16384", "public", "200002", ExpectedMigrationId);

        var identityA = await PostgresDatabaseIdentity.ReadAsync(clusterA, ExpectedMigrationId);
        var identityB = await PostgresDatabaseIdentity.ReadAsync(clusterB, ExpectedMigrationId);

        identityA.Should().NotBe(identityB);
        PostgresDatabaseIdentity.IsCanonicalFingerprint(identityA).Should().BeTrue();
        PostgresDatabaseIdentity.IsCanonicalFingerprint(identityB).Should().BeTrue();
        clusterA.LastIdentitySql.Should().Contain("pg_catalog.pg_control_system()")
            .And.Contain("pg_catalog.pg_database")
            .And.Contain("MAX(\"MigrationId\")")
            .And.Contain("pg_catalog.current_schema()");
        clusterB.LastIdentitySql.Should().Be(clusterA.LastIdentitySql);
    }

    [Fact]
    public async Task Database_identity_is_stable_for_the_same_attested_backend_and_changes_when_database_or_schema_changes()
    {
        using var first = IdentityConnection("sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        using var same = IdentityConnection("sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        using var recreatedDatabase = IdentityConnection("sentencestudio", "16385", "public", "100001", ExpectedMigrationId);
        using var otherSchema = IdentityConnection("sentencestudio", "16384", "tenant", "100001", ExpectedMigrationId);

        var expected = await PostgresDatabaseIdentity.ReadAsync(first, ExpectedMigrationId);
        (await PostgresDatabaseIdentity.ReadAsync(same, ExpectedMigrationId)).Should().Be(expected);
        (await PostgresDatabaseIdentity.ReadAsync(recreatedDatabase, ExpectedMigrationId)).Should().NotBe(expected);
        (await PostgresDatabaseIdentity.ReadAsync(otherSchema, ExpectedMigrationId)).Should().NotBe(expected);
        expected.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task A_physical_clone_with_identical_control_data_and_history_has_the_same_fingerprint()
    {
        using var original = IdentityConnection("sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        using var clone = IdentityConnection("sentencestudio", "16384", "public", "100001", ExpectedMigrationId);

        // Hashing copied control data cannot attest distinct physical servers.
        (await PostgresDatabaseIdentity.ReadAsync(original, ExpectedMigrationId)).Should()
            .Be(await PostgresDatabaseIdentity.ReadAsync(clone, ExpectedMigrationId));
    }

    [Fact]
    public async Task Signed_sql_control_id_and_unsigned_controldata_id_hash_the_same_high_bit_value()
    {
        const ulong pgControlDataIdentifier = 0x8000000000001234UL;
        var signedSqlIdentifier = unchecked((long)pgControlDataIdentifier);
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public",
            signedSqlIdentifier.ToString(CultureInfo.InvariantCulture), ExpectedMigrationId);

        var identity = await PostgresDatabaseIdentity.ReadAsync(connection, ExpectedMigrationId);

        identity.Should().Be(PostgresDatabaseIdentity.ComputeFromAttestedValues(
            "sentencestudio", 16384, "public", signedSqlIdentifier,
            ExpectedMigrationId, ExpectedMigrationId));
        unchecked((ulong)signedSqlIdentifier).Should().Be(pgControlDataIdentifier);
    }

    [Fact]
    public async Task Readiness_rechecks_identity_after_history_on_the_same_backend()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        var pendingReads = 0;

        var readiness = await PostgresDatabaseIdentity.ReadReadinessAsync(
            connection, () => connection, () => connection.BackendPid,
            _ => Task.FromResult(++pendingReads - 1), ExpectedMigrationId);

        readiness.DatabaseIdentity.Should().Be(
            await PostgresDatabaseIdentity.ReadAsync(connection, ExpectedMigrationId));
        readiness.PendingMigrationCount.Should().Be(0);
        pendingReads.Should().Be(1);
        connection.IdentityReadCount.Should().Be(3);
    }

    [Fact]
    public async Task Backend_pid_is_session_local_and_not_part_of_the_cross_service_fingerprint()
    {
        using var api = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        using var worker = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        worker.BackendPid = api.BackendPid + 1;

        var apiReadiness = await PostgresDatabaseIdentity.ReadReadinessAsync(
            api, () => api, () => api.BackendPid, _ => Task.FromResult(0), ExpectedMigrationId);
        var workerReadiness = await PostgresDatabaseIdentity.ReadReadinessAsync(
            worker, () => worker, () => worker.BackendPid,
            _ => Task.FromResult(0), ExpectedMigrationId);

        apiReadiness.DatabaseIdentity.Should().Be(workerReadiness.DatabaseIdentity);
    }

    [Fact]
    public async Task An_open_connection_reporting_an_executing_state_keeps_its_backend()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);

        var readiness = await PostgresDatabaseIdentity.ReadReadinessAsync(
            connection, () => connection, () => connection.BackendPid,
            _ =>
            {
                connection.ReportState(ConnectionState.Open | ConnectionState.Executing);
                connection.ReportState(ConnectionState.Open);
                return Task.FromResult(0);
            },
            ExpectedMigrationId);

        readiness.PendingMigrationCount.Should().Be(0);
        connection.IdentityReadCount.Should().Be(2);
    }

    [Theory]
    [InlineData(ConnectionState.Broken)]
    [InlineData(ConnectionState.Open | ConnectionState.Broken)]
    [InlineData(ConnectionState.Connecting)]
    public async Task Transient_broken_or_connecting_state_latches_backend_loss(
        ConnectionState temporaryState)
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => connection, () => connection.BackendPid,
                _ =>
                {
                    connection.ReportState(temporaryState);
                    connection.ReportState(ConnectionState.Open);
                    return Task.FromResult(0);
                },
                ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.BackendChanged);
        connection.IdentityReadCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_rejects_a_reopened_backend_even_when_identity_and_history_are_unchanged(
        bool omitClosedNotification)
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => connection, () => connection.BackendPid,
                _ =>
                {
                    if (omitClosedNotification)
                    {
                        connection.ReportReopenWithoutCloseNotification();
                    }
                    else
                    {
                        connection.Close();
                        connection.Open();
                    }

                    return Task.FromResult(0);
                },
                ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.BackendChanged);
        connection.IdentityReadCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_rejects_backend_loss_during_either_identity_query(
        bool duringConfirmation)
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        connection.OnIdentityRead = read =>
        {
            if (read == (duringConfirmation ? 2 : 1))
            {
                connection.Close();
                connection.Open();
            }
        };
        var pendingReads = 0;

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => connection, () => connection.BackendPid,
                _ => Task.FromResult(++pendingReads - 1), ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.BackendChanged);
        pendingReads.Should().Be(duringConfirmation ? 1 : 0);
        connection.IdentityReadCount.Should().Be(duringConfirmation ? 2 : 1);
    }

    [Fact]
    public async Task Readiness_rejects_a_replaced_pid_even_when_identity_and_history_are_unchanged()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        var originalPid = connection.BackendPid;
        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => connection, () => connection.BackendPid,
                _ =>
                {
                    connection.BackendPid = originalPid + 1;
                    return Task.FromResult(0);
                },
                ExpectedMigrationId));

        connection.BackendPid = originalPid;
        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.BackendChanged);
        connection.IdentityReadCount.Should().Be(1);
    }

    [Fact]
    public async Task Readiness_rejects_an_EF_connection_replacement_even_with_the_same_pid_and_identity()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        using var replacement = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        DbConnection current = connection;

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => current, () => connection.BackendPid,
                _ =>
                {
                    current = replacement;
                    return Task.FromResult(0);
                },
                ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.BackendChanged);
        connection.IdentityReadCount.Should().Be(1);
        replacement.IdentityReadCount.Should().Be(0);
    }

    [Fact]
    public async Task Readiness_rejects_provenance_changed_after_history_without_a_disconnect()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => connection, () => connection.BackendPid,
                _ =>
                {
                    connection.IdentityRows!.Rows[0][3] = "200002";
                    return Task.FromResult(0);
                },
                ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.IdentityChanged);
        connection.IdentityReadCount.Should().Be(2);
        failure.Message.Should().NotContain("sentencestudio").And.NotContain("200002");
    }

    [Fact]
    public async Task Readiness_refuses_an_unavailable_backend_pid_before_querying_identity()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        connection.BackendPid = 0;
        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => connection, () => connection.BackendPid,
                _ => Task.FromResult(0), ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.BackendChanged);
        connection.IdentityReadCount.Should().Be(0);
    }

    [Fact]
    public async Task Backend_pid_failure_does_not_leak_connection_details()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", "100001", ExpectedMigrationId);

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadReadinessAsync(
                connection, () => connection,
                () => throw new InvalidOperationException("Host=hidden.example.invalid;******"),
                _ => Task.FromResult(0), ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.BackendChanged);
        failure.Message.Should().NotContain("hidden.example.invalid");
        failure.InnerException.Should().BeNull();
        connection.IdentityReadCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null, "100001", ExpectedMigrationId, PostgresDatabaseIdentityFailure.MissingDatabase)]
    [InlineData("16384", null, ExpectedMigrationId, PostgresDatabaseIdentityFailure.MissingServerInstance)]
    [InlineData("16384", "0", ExpectedMigrationId, PostgresDatabaseIdentityFailure.MissingServerInstance)]
    [InlineData("16384", "100001", null, PostgresDatabaseIdentityFailure.MissingSchemaMarker)]
    [InlineData("16384", "100001", "old_migration", PostgresDatabaseIdentityFailure.UnexpectedSchemaMarker)]
    public async Task Missing_or_incompatible_persisted_provenance_never_produces_an_identity(
        string? databaseOid, string? clusterSystemId, string? appliedMigration,
        PostgresDatabaseIdentityFailure expectedFailure)
    {
        using var connection = IdentityConnection(
            "sentencestudio", databaseOid, "public", clusterSystemId, appliedMigration);

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadAsync(connection, ExpectedMigrationId));

        failure.Reason.Should().Be(expectedFailure);
        failure.Message.Should().NotContain("sentencestudio").And.NotContain("100001");
    }

    [Fact]
    public async Task A_database_error_cannot_leak_connection_details_through_the_identity_exception()
    {
        using var connection = IdentityConnection("sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        connection.QueryError = new SimulatedIdentityQueryException(
            "Host=hidden.example.invalid;Password=do-not-log");

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadAsync(connection, ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.QueryUnavailable);
        failure.Message.Should().NotContain("hidden.example.invalid").And.NotContain("do-not-log");
        failure.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_history_table_reports_the_missing_marker_without_exposing_SQL()
    {
        using var connection = IdentityConnection("sentencestudio", "16384", "public", "100001", ExpectedMigrationId);
        connection.QueryError = new SimulatedIdentityQueryException(
            "table __EFMigrationsHistory at Host=hidden.example.invalid does not exist", "42P01");

        var failure = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
            PostgresDatabaseIdentity.ReadAsync(connection, ExpectedMigrationId));

        failure.Reason.Should().Be(PostgresDatabaseIdentityFailure.MissingSchemaMarker);
        failure.Message.Should().NotContain("hidden.example.invalid").And.NotContain("Host=");
        failure.InnerException.Should().BeNull();
    }

    [Fact]
    public void All_startup_contexts_share_the_caller_connection_without_EF_retries()
    {
        using var connection = new NpgsqlConnection(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
        var interceptors = Array.Empty<IInterceptor>();

        using var application = new ApplicationDbContext(
            StartupDbContextFactory.ApplicationOptions(connection, interceptors));
        using var coach = new CoachDbContext(
            StartupDbContextFactory.CoachOptions(connection, interceptors));
        using var feedback = new FeedbackDbContext(
            StartupDbContextFactory.FeedbackOptions(connection, interceptors));

        foreach (var context in new DbContext[] { application, coach, feedback })
        {
            context.Database.GetDbConnection().Should().BeSameAs(connection);
            context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeFalse();
        }
    }

    [Fact]
    public void Closing_and_reopening_the_same_backend_cannot_recover_a_lost_lock()
    {
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);
        fence.AssertActive();

        connection.Close();
        connection.Open();
        Action assertActive = () => fence.AssertActive();
        assertActive.Should().Throw<InvalidOperationException>()
            .WithMessage("*startup lock backend was lost*");
    }

    [Fact]
    public void Reopen_event_without_a_closed_notification_still_latches_the_lock_loss()
    {
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);

        connection.ReportReopenWithoutCloseNotification();

        Action assertActive = () => fence.AssertActive();
        assertActive.Should().Throw<InvalidOperationException>()
            .WithMessage("*startup lock backend was lost*");
    }

    [Fact]
    public void Open_connection_execution_state_does_not_latch_the_lock_loss()
    {
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);

        connection.ReportState(ConnectionState.Open | ConnectionState.Executing);
        connection.ReportState(ConnectionState.Open);

        fence.AssertActive();
    }

    [Fact]
    public void Startup_refuses_a_connection_without_a_backend_process_id()
    {
        using var connection = new StubConnection { BackendPid = 0 };
        connection.Open();

        Action createFence = () => new StartupPostgresSessionFence(
            connection, () => connection.BackendPid);
        createFence.Should().Throw<InvalidOperationException>()
            .WithMessage("*no valid PostgreSQL process ID*");
    }

    [Fact]
    public void Replacing_the_backend_pid_latches_even_if_the_old_pid_comes_back()
    {
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);

        connection.BackendPid++;
        Action assertActive = () => fence.AssertActive();
        assertActive.Should().Throw<InvalidOperationException>()
            .WithMessage("*backend changed*");
        connection.BackendPid--;
        assertActive.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_second_backend_command_is_rejected_and_fences_subsequent_commands()
    {
        using var owner = new StubConnection();
        using var other = new StubConnection();
        owner.Open();
        other.Open();
        using var fence = new StartupPostgresSessionFence(owner, () => owner.BackendPid);
        var interceptor = new StartupSessionCommandInterceptor(fence);
        using var ownCommand = owner.CreateCommand();
        using var otherCommand = other.CreateCommand();

        interceptor.NonQueryExecuting(ownCommand, null!, default);
        Action foreignWrite = () => interceptor.NonQueryExecuting(otherCommand, null!, default);
        foreignWrite.Should().Throw<InvalidOperationException>()
            .WithMessage("*other than the lock owner*");
        Action laterWrite = () => interceptor.NonQueryExecuting(ownCommand, null!, default);
        laterWrite.Should().Throw<InvalidOperationException>()
            .WithMessage("*startup lock backend was lost*");
    }

    [Fact]
    public void EF_is_forbidden_to_reopen_a_caller_owned_startup_connection()
    {
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);
        var interceptor = new StartupSessionConnectionInterceptor(fence);

        Action reopen = () => interceptor.ConnectionOpening(connection, null!, default);
        reopen.Should().Throw<InvalidOperationException>().WithMessage("*open a new startup*");
        Action laterWrite = () => fence.AssertActive();
        laterWrite.Should().Throw<InvalidOperationException>().WithMessage("*startup lock backend was lost*");
    }

    [Fact]
    public void EF_close_attempt_latches_the_lock_loss_even_if_the_backend_is_still_open()
    {
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);
        var interceptor = new StartupSessionConnectionInterceptor(fence);

        Action close = () => interceptor.ConnectionClosing(connection, null!, default);
        close.Should().Throw<InvalidOperationException>().WithMessage("*close the startup*");
        connection.State.Should().Be(ConnectionState.Open);
        Action laterWrite = () => fence.AssertActive();
        laterWrite.Should().Throw<InvalidOperationException>().WithMessage("*startup lock backend was lost*");
    }

    [Fact]
    public async Task EF_async_connection_lifecycle_attempts_latch_the_lock_loss()
    {
        using var connection = new StubConnection();
        connection.Open();
        using (var openingFence = new StartupPostgresSessionFence(connection, () => connection.BackendPid))
        {
            var interceptor = new StartupSessionConnectionInterceptor(openingFence);
            Func<Task> reopen = async () =>
            {
                await interceptor.ConnectionOpeningAsync(connection, null!, default);
            };

            await reopen.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*reopen the startup*");
            Action laterWrite = () => openingFence.AssertActive();
            laterWrite.Should().Throw<InvalidOperationException>()
                .WithMessage("*startup lock backend was lost*");
        }

        using var closingFence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);
        var closingInterceptor = new StartupSessionConnectionInterceptor(closingFence);
        Func<Task> close = async () =>
        {
            await closingInterceptor.ConnectionClosingAsync(connection, null!, default);
        };

        await close.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*close the startup*");
        Action subsequentWrite = () => closingFence.AssertActive();
        subsequentWrite.Should().Throw<InvalidOperationException>()
            .WithMessage("*startup lock backend was lost*");
    }

    [Fact]
    public async Task Reader_and_scalar_commands_cannot_target_a_different_backend()
    {
        using var owner = new StubConnection();
        using var other = new StubConnection();
        owner.Open();
        other.Open();

        using (var readerFence = new StartupPostgresSessionFence(owner, () => owner.BackendPid))
        {
            var interceptor = new StartupSessionCommandInterceptor(readerFence);
            using var ownCommand = owner.CreateCommand();
            using var foreignCommand = other.CreateCommand();

            interceptor.ReaderExecuting(ownCommand, null!, default);
            Action foreignRead = () => interceptor.ReaderExecuting(foreignCommand, null!, default);
            foreignRead.Should().Throw<InvalidOperationException>()
                .WithMessage("*other than the lock owner*");
        }

        using var scalarFence = new StartupPostgresSessionFence(owner, () => owner.BackendPid);
        var scalarInterceptor = new StartupSessionCommandInterceptor(scalarFence);
        using var ownScalar = owner.CreateCommand();
        using var foreignScalar = other.CreateCommand();

        await scalarInterceptor.ScalarExecutingAsync(ownScalar, null!, default);
        Func<Task> foreignReadAsync = async () =>
        {
            await scalarInterceptor.ScalarExecutingAsync(foreignScalar, null!, default);
        };
        await foreignReadAsync.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*other than the lock owner*");
    }

    [Fact]
    public void Host_shutdown_cancels_fence_checks_before_another_write()
    {
        using var shutdown = new CancellationTokenSource();
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(
            connection, () => connection.BackendPid, shutdown.Token);

        shutdown.Cancel();
        Action assertActive = () => fence.AssertActive();
        assertActive.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task Optional_schema_error_degrades_only_while_the_lock_is_still_held()
    {
        var failure = new InvalidOperationException("coach migration failed");
        Exception? degraded = null;
        var probes = 0;

        await OptionalStartupMigration.RunAsync(
            _ => Task.FromException(failure),
            _ => { probes++; return Task.CompletedTask; },
            ex => degraded = ex,
            CancellationToken.None);

        degraded.Should().BeSameAs(failure);
        probes.Should().Be(2);
    }

    [Fact]
    public async Task Optional_schema_error_cannot_swallow_a_lost_lock()
    {
        var failure = new InvalidOperationException("lock backend lost");
        var degraded = false;
        var probes = 0;

        Func<Task> run = () => OptionalStartupMigration.RunAsync(
            _ => Task.FromException(new InvalidOperationException("feedback migration failed")),
            _ =>
            {
                probes++;
                return probes == 1 ? Task.CompletedTask : Task.FromException(failure);
            },
            _ => degraded = true,
            CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>()).Which
            .Should().BeSameAs(failure);
        degraded.Should().BeFalse();
    }

    [Fact]
    public async Task Optional_schema_error_cannot_swallow_an_EF_close_attempt()
    {
        using var connection = new StubConnection();
        connection.Open();
        using var fence = new StartupPostgresSessionFence(connection, () => connection.BackendPid);
        var interceptor = new StartupSessionConnectionInterceptor(fence);
        var degraded = false;

        Func<Task> migrate = () => OptionalStartupMigration.RunAsync(
            _ =>
            {
                interceptor.ConnectionClosing(connection, null!, default);
                return Task.CompletedTask;
            },
            _ =>
            {
                fence.AssertActive();
                return Task.CompletedTask;
            },
            _ => degraded = true,
            CancellationToken.None);

        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*startup lock backend was lost*");
        degraded.Should().BeFalse();
    }

    [Fact]
    public async Task Successful_optional_migration_still_fails_closed_if_the_lock_disappears()
    {
        var degraded = false;
        var probes = 0;

        Func<Task> run = () => OptionalStartupMigration.RunAsync(
            _ => Task.CompletedTask,
            _ => ++probes == 1
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("lock lost")),
            _ => degraded = true,
            CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>();
        degraded.Should().BeFalse();
    }

    [Fact]
    public async Task Optional_migration_cancellation_is_not_a_feature_degrade()
    {
        var degraded = false;
        Func<Task> run = () => OptionalStartupMigration.RunAsync(
            _ => Task.FromCanceled(new CancellationToken(true)),
            _ => Task.CompletedTask,
            _ => degraded = true,
            CancellationToken.None);

        await run.Should().ThrowAsync<OperationCanceledException>();
        degraded.Should().BeFalse();
    }

    [Fact]
    public void Different_coach_or_feedback_databases_refuse_unfenced_startup()
    {
        foreach (var alternate in new[] { "coach", "feedback" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:sentencestudio"] = "Host=local;Database=main;Username=test",
                    [$"ConnectionStrings:{alternate}"] = "Host=local;Database=other;Username=test"
                }).Build();

            Action verify = () => StartupDbContextFactory.RequireSingleDatabase(configuration);
            verify.Should().Throw<InvalidOperationException>()
                .WithMessage($"*ConnectionStrings:{alternate}*")
                .And.Message.Should().NotContain("Host=");
        }
    }

    [Fact]
    public void Skipping_migrations_with_development_seeding_fails_before_database_access()
    {
        using var factory = new UnsafeSeedFactory();

        Action start = () => factory.CreateClient();
        start.Should().Throw<InvalidOperationException>()
            .WithMessage("*Database:SkipMigrateOnStartup*Auth:SeedDevTestAccounts*");
    }

    [Fact]
    public async Task Real_API_version_endpoint_returns_503_for_a_non_PostgreSQL_local_identity()
    {
        await using var app = await StartVersionEndpointAsync(postgresConnection: null);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/version/info");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("PostgreSQL database identity is unavailable")
            .And.NotContain("databaseIdentity");
    }

    [Fact]
    public async Task Real_API_version_endpoint_returns_503_for_an_invalid_backend_without_a_database()
    {
        using var connection = IdentityConnection(
            "sentencestudio", "16384", "public", null, ExpectedMigrationId);
        await using var app = await StartVersionEndpointAsync(connection);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/version/info");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("readiness backend changed")
            .And.NotContain("databaseIdentity").And.NotContain("sentencestudio");
        connection.IdentityReadCount.Should().Be(0);
    }

    private static async Task<WebApplication> StartVersionEndpointAsync(DbConnection? postgresConnection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Database:SkipMigrateOnStartup"] = "false";
        builder.Services.AddSingleton<ReleaseNotesService>();
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
        {
            if (postgresConnection is null)
            {
                options.UseSqlite("Data Source=:memory:");
            }
            else
            {
                options.UseNpgsql(postgresConnection);
            }
        });

        var app = builder.Build();
        app.MapVersionEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class UnsafeSeedFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Any attempt to open a database fails locally rather than connecting to one.
            builder.UseSetting("ConnectionStrings:sentencestudio", "not-a-connection-string");
            builder.UseSetting("Database:SkipMigrateOnStartup", "true");
            builder.UseSetting("Auth:SeedDevTestAccounts", "true");
            builder.UseSetting(TestApiHostConfigurator.AiEndpointConfigKey, "");
            builder.ConfigureServices((context, services) =>
                TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(
                    services, context.Configuration));
        }
    }

    private sealed class StubConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public int BackendPid { get; set; } = 101;
        public DataTable? IdentityRows { get; set; }
        public DbException? QueryError { get; set; }
        public string? LastIdentitySql { get; set; }
        public int IdentityReadCount { get; private set; }
        public Action<int>? OnIdentityRead { get; set; }
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "unused";
        public override string DataSource => "test";
        public override string ServerVersion => "test";
        public override ConnectionState State => _state;

        public override void Open() => SetState(ConnectionState.Open);
        public override void Close() => SetState(ConnectionState.Closed);
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new StubCommand(this);

        public void ReportReopenWithoutCloseNotification() =>
            OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));

        public void ReportState(ConnectionState next) => SetState(next);
        public void RecordIdentityRead()
        {
            IdentityReadCount++;
            OnIdentityRead?.Invoke(IdentityReadCount);
        }

        private void SetState(ConnectionState next)
        {
            var previous = _state;
            _state = next;
            OnStateChange(new StateChangeEventArgs(previous, next));
        }
    }

    private sealed class StubCommand(DbConnection connection) : DbCommand
    {
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        public override void Cancel() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var stub = (StubConnection)connection;
            stub.LastIdentitySql = CommandText;
            stub.RecordIdentityRead();
            if (stub.QueryError is not null)
            {
                throw stub.QueryError;
            }

            return stub.IdentityRows?.CreateDataReader() ?? throw new NotSupportedException();
        }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
    }

    private static StubConnection IdentityConnection(
        string? databaseName, string? databaseOid, string? schemaName,
        string? clusterSystemId, string? appliedMigration)
    {
        var rows = new DataTable();
        for (var index = 0; index < 5; index++)
        {
            rows.Columns.Add($"field{index}", typeof(string));
        }
        rows.Rows.Add(
            databaseName ?? (object)DBNull.Value,
            databaseOid ?? (object)DBNull.Value,
            schemaName ?? (object)DBNull.Value,
            clusterSystemId ?? (object)DBNull.Value,
            appliedMigration ?? (object)DBNull.Value);

        var connection = new StubConnection { IdentityRows = rows };
        connection.Open();
        return connection;
    }

    private sealed class SimulatedIdentityQueryException(string message, string? sqlState = null)
        : DbException(message)
    {
        public override string? SqlState => sqlState;
    }
}
