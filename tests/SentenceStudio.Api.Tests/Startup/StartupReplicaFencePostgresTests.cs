using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Coach.Persistence.Cleanup;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Startup;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using Xunit.Abstractions;

namespace SentenceStudio.Api.Tests.Startup;

/// <summary>
/// One selected fault scenario per fresh, attested cluster. Every replica stalls in a
/// server-side DDL-end or BEFORE-row gate while it owns the application startup lock.
/// The scratch database and audit are never removed or reused.
/// </summary>
public sealed class StartupReplicaFencePostgresTests(ITestOutputHelper output)
{
    [StartupFencePostgresFact]
    public async Task A_dead_lock_backend_cannot_write_after_the_waiting_replica_takes_over()
    {
        await using var fixture = await StartupFencePostgresFixture.OpenAsync();
        output.WriteLine(
            $"UTC {DateTimeOffset.UtcNow:O}: attested container={fixture.Docker.ContainerId} " +
            $"image={fixture.Docker.ImageId} volume={fixture.Docker.VolumeName} " +
            $"host=127.0.0.1:{fixture.Docker.HostPort} database={fixture.Docker.DatabaseName} " +
            $"system_identifier={fixture.Docker.SystemIdentifier} scenario={fixture.Scenario}");
        await AssertConfigurationPrecedenceAsync(fixture);
        await fixture.InstallAuditAsync();

        var proofs = new List<StartupFencePhaseProof>();
        switch (fixture.Scenario)
        {
            case StartupFencePostgresScenario.Baseline:
                proofs.Add(await RunPhaseAsync(fixture, "migration"));
                (await fixture.CountMigrationsAsync()).Should().BeGreaterThan(0);
                await fixture.InstallDmlAuditAsync();

                var numberSeed = await fixture.StageNumberReseedAsync();
                proofs.Add(await RunPhaseAsync(
                    fixture, "numberseed", numberSeed.Id, numberSeed.OriginalName));

                var wordId = Guid.NewGuid().ToString();
                await fixture.SeedUnknownWordAsync(wordId);
                proofs.Add(await RunPhaseAsync(fixture, "backfill", wordId));
                (await fixture.ReadWordClassificationAsync(wordId))
                    .Should().Be((int)LexicalUnitType.Phrase);

                proofs.Add(await RunPhaseAsync(fixture, "coresync"));
                (await fixture.CountCoreSyncTriggersAsync()).Should().Be(3);
                break;

            case StartupFencePostgresScenario.Phrase:
                proofs.Add(await RunPhaseAsync(fixture, "migration"));
                await fixture.InstallDmlAuditAsync();
                var phraseSeed = await fixture.SeedPhraseAsync();
                proofs.Add(await RunPhaseAsync(
                    fixture, "phrase", phraseSeed.Phrase.Id, phraseSeed: phraseSeed));
                break;

            case StartupFencePostgresScenario.Coach:
                await fixture.AssertMigrationHistoryAbsentAsync("__CoachMigrationsHistory");
                proofs.Add(await RunPhaseAsync(fixture, "coach"));
                await fixture.InstallDmlAuditAsync();
                break;

            case StartupFencePostgresScenario.Feedback:
                await fixture.AssertMigrationHistoryAbsentAsync("__FeedbackMigrationsHistory");
                proofs.Add(await RunPhaseAsync(fixture, "feedback"));
                await fixture.InstallDmlAuditAsync();
                break;

            case StartupFencePostgresScenario.DevSeed:
                proofs.Add(await RunPhaseAsync(fixture, "devseed",
                    DevTestAccountSeeder.Accounts[0].Email.ToUpperInvariant()));
                await fixture.InstallDmlAuditAsync();
                break;

            default:
                throw new InvalidOperationException(
                    "No verified startup-fence scenario was selected.");
        }

        await AssertWholeRunEvidenceAsync(fixture, proofs);
        output.WriteLine(
            $"UTC {DateTimeOffset.UtcNow:O}: scenario={fixture.Scenario} " +
            $"{proofs.Count} server-gated takeover phases passed.");
    }

    internal static IReadOnlyList<string> PhasesForScenario(StartupFencePostgresScenario scenario)
        => scenario switch
        {
            StartupFencePostgresScenario.Baseline =>
                ["migration", "numberseed", "backfill", "coresync"],
            StartupFencePostgresScenario.Phrase => ["migration", "phrase"],
            StartupFencePostgresScenario.Coach => ["coach"],
            StartupFencePostgresScenario.Feedback => ["feedback"],
            StartupFencePostgresScenario.DevSeed => ["devseed"],
            _ => throw new InvalidOperationException(
                "Refusing an undefined startup-fence fault scenario.")
        };

    internal static void VerifyOfflineHostedWriterIsolation()
    {
        using (var preopen = new StartupReplicaFactory(skipDatabaseInitialization: false))
        {
            var stopped = Record.Exception(() => preopen.CreateClient().Dispose());
            stopped.Should().NotBeNull();
            stopped!.ToString().Should().Contain("Offline replica blocked before PostgreSQL open");
            preopen.HostedServicesChecked.Should().BeTrue();
            preopen.SafeConfigurationChecked.Should().BeTrue();
            preopen.OfflinePreopenValidated.Should().BeTrue(
                "test-service isolation must run before startup validates its still-closed connection");
        }

        using var runtime = new StartupReplicaFactory(skipDatabaseInitialization: true);
        using var client = runtime.CreateClient();
        runtime.HostedServicesChecked.Should().BeTrue();
        runtime.SafeConfigurationChecked.Should().BeTrue();
        var configuration = runtime.Services.GetRequiredService<IConfiguration>();
        configuration.GetValue<bool>("Feedback:Enabled").Should().BeFalse();
        configuration["AI:OpenAI:Endpoint"].Should().BeNull();
        configuration["ElevenLabsKey"].Should().BeNull();
        configuration["AzureMonitor:ConnectionString"].Should().BeNull();
        runtime.Services.GetRequiredService<IOptions<FeedbackOptions>>().Value
            .RetentionSweepEnabled.Should().BeTrue(
                "the writer must be absent even when feedback retention is enabled");
        var hosted = runtime.Services.GetServices<IHostedService>().ToArray();
        hosted.Select(service => service.GetType()).Should()
            .Contain(typeof(CoachToolRegistryStartupValidator))
            .And.NotContain(typeof(FeedbackRetentionBackgroundService))
            .And.NotContain(typeof(CoachCleanupBackgroundService))
            .And.NotContain(typeof(DevTestAccountSeeder));
        hosted.Where(service => service.GetType().Assembly == typeof(Program).Assembly)
            .Select(service => service.GetType())
            .Should().Equal(typeof(CoachToolRegistryStartupValidator));

        using var scope = runtime.Services.CreateScope();
        using var feedback = scope.ServiceProvider.GetRequiredService<FeedbackDbContext>();
        scope.ServiceProvider.GetRequiredService<FeedbackRetentionSweep>().Should().NotBeNull();
        feedback.Database.GetMigrations().Should().NotBeEmpty(
            "isolating the timer must not remove feedback migrations or its runtime context");
        feedback.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
    }

    internal static void VerifyOfflineDevelopmentSeedIsolation()
    {
        using var preopen = new StartupReplicaFactory(
            skipDatabaseInitialization: false, seedDevelopmentAccounts: true);
        var stopped = Record.Exception(() => preopen.CreateClient().Dispose());
        stopped.Should().NotBeNull();
        stopped!.ToString().Should().Contain("Offline replica blocked before PostgreSQL open");
        preopen.SafeConfigurationChecked.Should().BeTrue(
            "the Development host cannot load ambient secrets or external endpoints");
        preopen.HostedServicesChecked.Should().BeTrue(
            "the built-in seeder must be registered only as a pre-listen singleton");
        preopen.OfflinePreopenValidated.Should().BeTrue(
            "the Development host must still stop on a closed PostgreSQL connection");
    }

    private static async Task AssertConfigurationPrecedenceAsync(StartupFencePostgresFixture fixture)
    {
        var sentinel = new TcpListener(IPAddress.Loopback, 0);
        using var cancellation = new CancellationTokenSource();
        Task<TcpClient>? socketAttempt = null;
        try
        {
            sentinel.Start();
            var sentinelPort = ((IPEndPoint)sentinel.LocalEndpoint).Port;
            RequireSentinelPortDistinctFromPostgres(sentinelPort, fixture.Docker.HostPort);
            socketAttempt = sentinel.AcceptTcpClientAsync(cancellation.Token).AsTask();
            var actor = fixture.Actor("precedence", "a");
            var invalidEndpoint = new NpgsqlConnectionStringBuilder(fixture.ConnectionFor(actor))
            {
                Host = IPAddress.Loopback.ToString(),
                Port = sentinelPort,
                Timeout = 2
            }.ConnectionString;
            using var replica = new StartupReplicaFactory(fixture, actor, invalidEndpoint);

            var failure = Record.Exception(() => replica.CreateClient().Dispose());

            failure.Should().NotBeNull("normal pre-open validation must reject the misrouted API endpoint");
            failure!.ToString().Should().Contain("actual replica target",
                "the real pre-open guard, not a substitute exception or socket timeout, must reject it");
            replica.RejectedBeforeOpen.IsCompletedSuccessfully.Should().BeTrue();
            replica.RejectedBeforeOpen.Result.Should().BeTrue(
                "the later provider must override UseSetting and reach normal validation");
            socketAttempt.IsCompleted.Should().BeFalse(
                "without the pre-open validation the sentinel would observe an attempted socket");
            replica.AttestedBackend.IsCompleted.Should().BeFalse();
        }
        finally
        {
            cancellation.Cancel();
            sentinel.Stop();
            if (socketAttempt is not null)
            {
                await AssertNoSentinelSocketAcceptedAsync(socketAttempt);
            }
        }
    }

    internal static void RequireSentinelPortDistinctFromPostgres(
        int sentinelPort, int attestedHostPort)
    {
        if (sentinelPort == attestedHostPort)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence precedence probe: sentinel port matches " +
                "the attested PostgreSQL host port.");
        }
    }

    internal static async Task AssertNoSentinelSocketAcceptedAsync(Task<TcpClient> socketAttempt)
    {
        TcpClient socket;
        try
        {
            socket = await socketAttempt;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SocketException ex)
            when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        socket.Dispose();
        throw new InvalidOperationException(
            "The pre-audit configuration-precedence probe reached its TCP sentinel.");
    }

    private async Task<StartupFencePhaseProof> RunPhaseAsync(
        StartupFencePostgresFixture fixture, string phase, string? targetId = null,
        string? expectedNumberName = null, StartupFencePhraseSeed? phraseSeed = null)
    {
        var actorA = fixture.Actor(phase, "a");
        var actorB = fixture.Actor(phase, "b");
        var keys = await fixture.ArmAsync(phase, actorA, actorB, targetId);
        await using var gate = await fixture.HoldGatesAsync(keys);
        using var replicaA = new StartupReplicaFactory(fixture, actorA);
        using var replicaB = new StartupReplicaFactory(fixture, actorB);
        Task<HttpClient>? startupA = null;
        Task<HttpClient>? startupB = null;
        var auditStart = await fixture.LastAuditIdAsync();

        try
        {
            startupA = Task.Run(() => replicaA.CreateClient());
            var oldBackend = await WaitForServerGateAsync(
                fixture, actorA, keys.A, startupA, phase);
            (await replicaA.AttestedBackend.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().Be(oldBackend);

            startupB = Task.Run(() => replicaB.CreateClient());
            var waitingBackend = await WaitForSessionAsync(fixture, actorB, startupB, granted: false);
            (await replicaB.AttestedBackend.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().Be(waitingBackend);
            oldBackend.Pid.Should().NotBe(waitingBackend.Pid);

            await fixture.TerminateVerifiedOwnerAsync(
                actorA, oldBackend, actorB, waitingBackend);

            StartupFenceBackend newBackend;
            long takeover;
            if (keys.Takeover is long checkpointKey)
            {
                // Coach, Feedback, and Development seeding run after CoreSync. B must
                // first stop at its earliest real CoreSync write, or that write would
                // precede a marker recorded at the later selected phase.
                newBackend = await WaitForServerGateAsync(
                    fixture, actorB, checkpointKey, startupB, "CoreSync takeover checkpoint");
                newBackend.Should().Be(waitingBackend);
                takeover = await fixture.RecordTakeoverAsync(
                    phase, actorA, actorB, oldBackend, newBackend);
                await gate.ReleaseTakeoverAsync();
                var phaseBackend = await WaitForServerGateAsync(
                    fixture, actorB, keys.B, startupB, phase);
                phaseBackend.Should().Be(newBackend,
                    "B must reach the selected real writer on its original lock backend");
            }
            else
            {
                newBackend = await WaitForServerGateAsync(
                    fixture, actorB, keys.B, startupB, phase);
                newBackend.Should().Be(waitingBackend);
                takeover = await fixture.RecordTakeoverAsync(
                    phase, actorA, actorB, oldBackend, newBackend);
            }

            await gate.ReleaseBAsync();

            using var clientB = await startupB.WaitAsync(TimeSpan.FromMinutes(4));
            var oldFailure = await AssertOldStartupFailedAsync(startupA);
            await AssertServerEvidenceAsync(
                fixture, phase, actorA, actorB, oldBackend, newBackend, takeover,
                auditStart, targetId, expectedNumberName, phraseSeed);
            var runtimeBackend = await AssertRuntimeContextAsync(replicaB, fixture, actorB);
            var rows = await fixture.ReadAuditAsync(phase);
            runtimeBackend.Should().NotBe(newBackend,
                "the ordinary runtime context must open a separate backend after startup");
            rows.Where(row => row.Actor == actorB && row.Kind == "login")
                .Select(row => new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds))
                .Should().Equal(new[] { newBackend, runtimeBackend },
                    "the only additional B backend is the attested SELECT-only runtime DI check, whose login is audited");
            rows.Should().NotContain(row =>
                row.Actor == actorB &&
                new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds) == runtimeBackend &&
                (row.Kind == "ddl" || row.Kind == "dml"),
                "the runtime DI check issues no application DDL/DML and cannot masquerade as a startup writer");
            output.WriteLine(
                $"UTC {DateTimeOffset.UtcNow:O}: phase={phase} old_pid={oldBackend.Pid} " +
                $"new_pid={newBackend.Pid} runtime_pid={runtimeBackend.Pid} " +
                $"takeover_sequence={takeover} " +
                $"old_write_rows={rows.Count(row => row.Actor == actorA && row.Kind is "ddl" or "dml")} " +
                $"new_write_rows={rows.Count(row => row.Actor == actorB && row.Kind is "ddl" or "dml")} " +
                $"old_failure={oldFailure.GetType().Name}");
            return new StartupFencePhaseProof(
                phase, actorA, actorB, oldBackend, newBackend, runtimeBackend, takeover);
        }
        finally
        {
            await gate.ReleaseTakeoverIfHeldAsync();
            await gate.ReleaseBAsync();
            await gate.ReleaseAAsync();

            // If an assertion failed while a host was still starting, releasing both test
            // gates lets its server-side command finish. Never reset the database to recover.
            if (startupA is { IsCompletedSuccessfully: true })
            {
                startupA.Result.Dispose();
            }
            if (startupB is { IsCompletedSuccessfully: true })
            {
                startupB.Result.Dispose();
            }
        }
    }

    private static async Task<StartupFenceBackend> WaitForServerGateAsync(
        StartupFencePostgresFixture fixture, string actor, long gateKey,
        Task startup, string phase)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(40))
        {
            var backend = await fixture.FindLockedAsync(actor, granted: true);
            if (backend is not null &&
                await fixture.IsWaitingOnGateAsync(actor, backend.Pid, gateKey))
            {
                return backend;
            }

            if (startup.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"The real API startup exited before its {phase} DDL-end/BEFORE-row " +
                    "server gate blocked its lock-owning backend.");
            }
            await Task.Delay(75);
        }
        throw new TimeoutException(
            $"The real API startup never reached the {phase} server-side write gate " +
            "on its lock-owning backend.");
    }

    private static async Task<StartupFenceBackend> WaitForSessionAsync(
        StartupFencePostgresFixture fixture, string actor, Task startup, bool granted)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(40))
        {
            var backend = await fixture.FindLockedAsync(actor, granted);
            if (backend is not null)
            {
                return backend;
            }

            if (startup.IsCompleted)
            {
                throw new InvalidOperationException(
                    "The real API startup exited before reaching the expected advisory-lock state.");
            }
            await Task.Delay(75);
        }
        throw new TimeoutException(
            "The expected replica never reached the application advisory-lock state.");
    }

    private static async Task<Exception> AssertOldStartupFailedAsync(Task<HttpClient> startup)
    {
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var unexpectedlyStarted = await startup.WaitAsync(TimeSpan.FromSeconds(40));
        });
        if (failure is TimeoutException)
        {
            throw new InvalidOperationException(
                "The old replica remained active after its locked PostgreSQL backend terminated.",
                failure);
        }
        if (failure is null)
        {
            throw new InvalidOperationException(
                "The old replica started successfully after its locked PostgreSQL backend terminated.");
        }
        var detail = failure.ToString();
        if (!new[] { "backend", "connection", "lock", "terminat", "stream", "socket" }
                .Any(term => detail.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The old replica failed for a reason unrelated to its terminated backend.",
                failure);
        }
        return failure;
    }

    private static async Task AssertServerEvidenceAsync(
        StartupFencePostgresFixture fixture, string phase,
        string actorA, string actorB, StartupFenceBackend oldBackend,
        StartupFenceBackend newBackend, long takeoverId, long auditStart,
        string? targetId, string? expectedNumberName, StartupFencePhraseSeed? phraseSeed)
    {
        var newRows = await fixture.ReadAuditAsync(afterEventId: auditStart);
        var expected = new[]
        {
            new StartupFenceExpectedReplicaBackend(actorA, phase, oldBackend),
            new StartupFenceExpectedReplicaBackend(actorB, phase, newBackend)
        };
        fixture.AssertAuditProvenance(newRows, expected);
        var rows = await fixture.ReadAuditAsync(phase);
        fixture.AssertAuditProvenance(rows, expected);
        StartupFencePostgresFixture.ValidateTakeoverOrdering(
            rows, phase, actorA, actorB, oldBackend, newBackend, takeoverId);
        var takeover = rows.Should().ContainSingle(
            row => row.EventId == takeoverId && row.Kind == "takeover").Which;
        takeover.SubjectPid.Should().Be(newBackend.Pid);
        takeover.BackendPid.Should().NotBe(oldBackend.Pid);
        takeover.BackendPid.Should().NotBe(newBackend.Pid);
        takeover.TransactionId.Should().BeGreaterThan(0);

        var oldLogin = rows.Should().ContainSingle(
            row => row.Actor == actorA && row.Kind == "login").Which;
        new StartupFenceBackend(oldLogin.BackendPid, oldLogin.BackendStartMicroseconds)
            .Should().Be(oldBackend);
        oldLogin.EventId.Should().BeLessThan(takeoverId);
        rows.Should().NotContain(row => row.Actor == actorA &&
            (row.Kind == "ddl" || row.Kind == "dml") &&
            new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds) != oldBackend);
        rows.Should().NotContain(row => row.Actor == actorA && row.EventId >= takeoverId,
            "no audited old connection may retry, commit DDL/DML, or log back in after B takes the lock");
        (await fixture.CountLiveSessionsAsync(actorA)).Should().Be(0,
            "an independently running old writer would still be visible to PostgreSQL");

        rows.Should().Contain(row => row.Actor == actorB && row.Kind == "login" &&
            new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds) == newBackend &&
            row.EventId < takeoverId);
        rows.Where(row => row.Actor == actorB && row.Kind == "login")
            .Should().ContainSingle(
                "before the runtime DI check there must be exactly one B login/backend");
        var auditedWrites = rows.Where(row =>
            row.Actor == actorB && row.Kind is ("ddl" or "dml")).ToArray();
        auditedWrites.Should().NotBeEmpty("B must finish actual startup database writes");
        auditedWrites.Should().OnlyContain(row =>
            new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds) == newBackend &&
            row.EventId > takeoverId &&
            row.TransactionId > 0,
            "all committed audited startup writes must use B's locked backend after takeover");

        if (phase is "migration" or "coach" or "feedback" or "devseed")
        {
            await AssertFirstStartupHistoryAndNumberSeedsAsync(fixture, rows, phase, actorB);
        }

        if (phase == "migration")
        {
            auditedWrites.Should().Contain(row =>
                row.Kind == "ddl" && row.Detail.StartsWith("CREATE TABLE ", StringComparison.Ordinal));
        }
        else if (phase == "numberseed")
        {
            auditedWrites.Should().Contain(row =>
                row.Kind == "dml" && row.Detail == $"NumberContext:UPDATE:{targetId}");
            (await fixture.ReadNumberContextNameAsync(targetId!))
                .Should().Be(expectedNumberName);
        }
        else if (phase == "backfill")
        {
            auditedWrites.Should().Contain(row =>
                row.Kind == "dml" && row.Detail == $"VocabularyWord:UPDATE:{targetId}");
            (await fixture.ReadWordClassificationAsync(targetId!))
                .Should().Be((int)LexicalUnitType.Phrase);
        }
        else if (phase == "phrase")
        {
            phraseSeed.Should().NotBeNull();
            targetId.Should().Be(phraseSeed!.Phrase.Id);
            var wordIds = phraseSeed.ConstituentWords
                .Select(word => word.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            (await fixture.ReadPhraseConstituentsAsync(targetId!))
                .Should().Equal(wordIds,
                    "the actual startup phrase backfill must insert both user-specific constituents");
            var prefix = $"PhraseConstituent:INSERT:{targetId}:";
            auditedWrites.Where(row =>
                    row.Kind == "dml" && row.Detail.StartsWith(prefix, StringComparison.Ordinal))
                .Select(row => row.Detail[prefix.Length..])
                .OrderBy(id => id, StringComparer.Ordinal)
                .Should().Equal(wordIds,
                    "both committed constituents must be audited on B's lock-owning backend");
            rows.Should().NotContain(row =>
                row.Actor == actorA && row.Detail.StartsWith(prefix, StringComparison.Ordinal));
        }
        else if (phase is "coach" or "feedback")
        {
            var history = phase == "coach"
                ? "__CoachMigrationsHistory"
                : "__FeedbackMigrationsHistory";
            auditedWrites.Should().Contain(row =>
                row.Kind == "ddl" &&
                row.Detail.StartsWith("CREATE TABLE ", StringComparison.Ordinal) &&
                row.Detail.Contains(history, StringComparison.Ordinal),
                "the gate must observe the first real optional history-table DDL");
        }
        else if (phase == "devseed")
        {
            var accounts = await fixture.ReadSeededDevAccountsAsync();
            auditedWrites.Where(row =>
                    row.Kind == "dml" &&
                    row.Detail.StartsWith("AspNetUsers:INSERT:", StringComparison.Ordinal))
                .Select(row => row.Detail["AspNetUsers:INSERT:".Length..])
                .OrderBy(id => id, StringComparer.Ordinal)
                .Should().Equal(
                    accounts.Select(account => account.UserId)
                        .OrderBy(id => id, StringComparer.Ordinal),
                    "the built-in Development users must be inserted by the locked B backend");
            auditedWrites.Where(row =>
                    row.Kind == "dml" &&
                    row.Detail.StartsWith("UserProfile:INSERT:", StringComparison.Ordinal))
                .Select(row => row.Detail["UserProfile:INSERT:".Length..])
                .OrderBy(id => id, StringComparer.Ordinal)
                .Should().Equal(
                    accounts.Select(account => account.ProfileId)
                        .OrderBy(id => id, StringComparer.Ordinal),
                    "the built-in Development profiles must be linked on the locked B backend");
            rows.Should().NotContain(row => row.Actor == actorA &&
                row.Detail.StartsWith("AspNetUsers:INSERT:", StringComparison.Ordinal));
        }
        else if (phase == "coresync")
        {
            auditedWrites.Should().Contain(row =>
                row.Kind == "ddl" &&
                row.Detail.StartsWith("DROP trigger ", StringComparison.OrdinalIgnoreCase) &&
                row.Detail.Contains("_ct_", StringComparison.Ordinal),
                "CoreSync must replace an existing tracking trigger, not gate a no-op CREATE TABLE");
            foreach (var operation in new[] { "insert", "update", "delete" })
            {
                auditedWrites.Should().Contain(row =>
                    row.Kind == "ddl" &&
                    row.Detail.StartsWith("CREATE TRIGGER ", StringComparison.Ordinal) &&
                    row.Detail.Contains(
                        $"__vocabularyword_ct_{operation}__", StringComparison.Ordinal));
            }
            (await fixture.CountCoreSyncTriggersAsync()).Should().Be(3);
        }
        else
        {
            throw new InvalidOperationException("Unexpected audited startup writer phase.");
        }
    }

    private static async Task AssertFirstStartupHistoryAndNumberSeedsAsync(
        StartupFencePostgresFixture fixture, IReadOnlyList<StartupFenceAuditRow> rows,
        string phase, string actorB)
    {
        var writes = rows.Where(row => row.Kind == "dml").ToArray();
        foreach (var historyTable in new[]
            {
                "__EFMigrationsHistory", "__CoachMigrationsHistory",
                "__FeedbackMigrationsHistory"
            })
        {
            var prefix = $"{historyTable}:INSERT:";
            var migrationIds = await fixture.ReadMigrationIdsAsync(historyTable);
            migrationIds.Should().NotBeEmpty($"{historyTable} must complete on the attested cluster");
            writes.Where(row => row.Detail.StartsWith(prefix, StringComparison.Ordinal))
                .Select(row => row.Detail[prefix.Length..])
                .OrderBy(id => id, StringComparer.Ordinal)
                .Should().Equal(migrationIds,
                    $"all committed {historyTable} migrations must have a server-side audit");
            if (phase == "migration" ||
                (phase == "coach" && historyTable == "__CoachMigrationsHistory") ||
                (phase == "feedback" && historyTable == "__FeedbackMigrationsHistory"))
            {
                writes.Where(row => row.Detail.StartsWith(prefix, StringComparison.Ordinal))
                    .Should().OnlyContain(row => row.Actor == actorB,
                        $"the {historyTable} first migration must be owned by replica B");
            }
        }

        foreach (var numberTable in new[] { "NumberContext", "NumberSubMode", "NumberCounter" })
        {
            var prefix = $"{numberTable}:INSERT:";
            var seededRows = await fixture.CountSeededNumbersAsync(numberTable);
            seededRows.Should().BeGreaterThan(0,
                $"{numberTable} must be seeded in the first application startup");
            writes.LongCount(row => row.Detail.StartsWith(prefix, StringComparison.Ordinal))
                .Should().Be(seededRows,
                    $"each committed {numberTable} first seed must have a server-side audit");
            if (phase == "migration")
            {
                writes.Where(row => row.Detail.StartsWith(prefix, StringComparison.Ordinal))
                    .Should().OnlyContain(row => row.Actor == actorB,
                        $"all initial {numberTable} rows must be seeded by replica B");
            }
        }
    }

    private static async Task<StartupFenceBackend> AssertRuntimeContextAsync(
        StartupReplicaFactory replica, StartupFencePostgresFixture fixture, string actor)
    {
        using var scope = replica.Services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (db.Database.GetDbConnection() is not NpgsqlConnection connection)
        {
            throw new InvalidOperationException("Runtime DI did not supply a PostgreSQL connection.");
        }
        replica.LockedConnection.Should().NotBeNull();
        ReferenceEquals(connection, replica.LockedConnection).Should().BeFalse(
            "runtime DI must not keep the disposed startup-only lock connection");
        fixture.ValidateReplicaBeforeOpen(connection, actor);
        await db.Database.OpenConnectionAsync();
        try
        {
            var runtimePid = await fixture.AttestReplicaAsync(
                connection, actor, CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_database()";
            (await command.ExecuteScalarAsync()).Should().Be(fixture.Docker.DatabaseName);
            connection.ProcessID.Should().Be(runtimePid);
            var backend = await fixture.ReadOwnBackendAsync(connection, actor);
            backend.Pid.Should().Be(runtimePid);
            return backend;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task AssertWholeRunEvidenceAsync(
        StartupFencePostgresFixture fixture, IReadOnlyList<StartupFencePhaseProof> proofs)
    {
        proofs.Select(proof => proof.Phase)
            .Should().Equal(PhasesForScenario(fixture.Scenario));
        await fixture.InstallDmlAuditAsync();
        var all = await fixture.ReadAuditAsync();
        fixture.AssertAuditProvenance(all, proofs.SelectMany(proof =>
            new[]
            {
                new StartupFenceExpectedReplicaBackend(
                    proof.ActorA, proof.Phase, proof.OldBackend),
                new StartupFenceExpectedReplicaBackend(
                    proof.ActorB, proof.Phase, proof.NewBackend),
                new StartupFenceExpectedReplicaBackend(
                    proof.ActorB, proof.Phase, proof.RuntimeBackend, LoginOnly: true)
            }).ToArray());
        fixture.AssertFixtureLogins(all);
        foreach (var proof in proofs)
        {
            all.Should().NotContain(row =>
                row.Actor == proof.ActorA && row.EventId >= proof.TakeoverSequence,
                "no old replica may reappear or write after its server-recorded takeover");
            all.Where(row => row.Actor == proof.ActorA && row.Kind == "login")
                .Select(row => new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds))
                .Should().Equal(proof.OldBackend);
            all.Where(row => row.Actor == proof.ActorB && row.Kind == "login")
                .Select(row => new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds))
                .Should().Equal(proof.NewBackend, proof.RuntimeBackend);
            all.Should().NotContain(row =>
                (row.Actor == proof.ActorA || row.Actor == proof.ActorB) &&
                row.Phase != proof.Phase);
            all.Where(row => row.Actor == proof.ActorB && row.Kind is ("ddl" or "dml"))
                .Should().OnlyContain(row =>
                    new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds) ==
                        proof.NewBackend && row.EventId > proof.TakeoverSequence);
            (await fixture.CountLiveSessionsAsync(proof.ActorA)).Should().Be(0);
        }
    }

    private sealed record StartupFencePhaseProof(
        string Phase, string ActorA, string ActorB, StartupFenceBackend OldBackend,
        StartupFenceBackend NewBackend, StartupFenceBackend RuntimeBackend,
        long TakeoverSequence);

    internal static IReadOnlyDictionary<string, string?> IsolatedReplicaSettings(
        string connectionString, bool skipDatabaseInitialization,
        bool seedDevelopmentAccounts, bool retainFeedback)
    {
        if (seedDevelopmentAccounts && skipDatabaseInitialization)
        {
            throw new InvalidOperationException(
                "Development account seeding cannot bypass the startup lock.");
        }
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:sentencestudio"] = connectionString,
            ["ConnectionStrings:coach"] = connectionString,
            ["ConnectionStrings:feedback"] = connectionString,
            ["Database:SkipMigrateOnStartup"] = skipDatabaseInitialization ? "true" : "false",
            ["Auth:SeedDevTestAccounts"] = seedDevelopmentAccounts ? "true" : "false",
            ["Auth:EnableDevAuthFallback"] = "false",
            ["Jwt:SigningKey"] = new string('s', 64),
            ["Coach:Enabled"] = "false",
            ["Coach:Cleanup:Enabled"] = "false",
            ["Feedback:Enabled"] = "false",
            ["Feedback:RetentionSweepEnabled"] = retainFeedback ? "true" : "false"
        };
    }

    internal static void RequireIsolatedReplicaConfiguration(
        IConfiguration actual, IReadOnlyDictionary<string, string?> expected,
        bool seedDevelopmentAccounts)
    {
        var hostKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "applicationName", "contentRoot", "environment", "webRoot",
            "hostingStartupAssemblies", "hostBuilder:reloadConfigOnChange"
        };
        foreach (var (key, value) in actual.AsEnumerable())
        {
            if (value is null)
            {
                continue;
            }
            if (expected.TryGetValue(key, out var safeValue))
            {
                if (!string.Equals(value, safeValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Replica host configuration replaced an isolated test-only setting.");
                }
            }
            else if (!hostKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"Replica host configuration contains an unapproved setting: {key}.");
            }
        }

        foreach (var (key, safeValue) in expected)
        {
            if (actual[key] != safeValue)
            {
                throw new InvalidOperationException(
                    "Replica host configuration is missing an isolated test-only setting.");
            }
        }
        if (actual.GetConnectionString("sentencestudio") !=
                actual.GetConnectionString("coach") ||
            actual.GetConnectionString("sentencestudio") !=
                actual.GetConnectionString("feedback") ||
            actual.GetValue<bool>("Auth:SeedDevTestAccounts") != seedDevelopmentAccounts ||
            (actual["environment"] is { } environment &&
             environment != (seedDevelopmentAccounts ? "Development" : "Testing")))
        {
            throw new InvalidOperationException(
                "Replica host must use only the single isolated database and selected environment.");
        }
    }

    private sealed class StartupReplicaFactory : WebApplicationFactory<Program>
    {
        private const string OfflineConnectionString =
            "Host=127.0.0.1;Port=1;Database=offline_no_database;Username=offline;Pooling=false;Timeout=1";

        private readonly StartupFencePostgresFixture? _fixture;
        private readonly string _actor;
        private readonly string? _invalidEndpoint;
        private readonly ReplicaConnectionAttestor? _attestor;
        private readonly OfflinePreopenStopAttestor? _offlineAttestor;
        private readonly bool _skipDatabaseInitialization;
        private readonly bool _seedDevelopmentAccounts;
        private bool _hostedServicesChecked;
        private bool _safeConfigurationChecked;

        internal StartupReplicaFactory(
            StartupFencePostgresFixture fixture, string actor, string? invalidEndpoint = null)
        {
            _fixture = fixture;
            _actor = actor;
            _invalidEndpoint = invalidEndpoint;
            _seedDevelopmentAccounts =
                fixture.Scenario == StartupFencePostgresScenario.DevSeed;
            _attestor = new ReplicaConnectionAttestor(
                fixture, actor, invalidEndpoint,
                () => _safeConfigurationChecked && _hostedServicesChecked);
        }

        internal StartupReplicaFactory(
            bool skipDatabaseInitialization, bool seedDevelopmentAccounts = false)
        {
            _actor = "sf_offline_writer_probe";
            _skipDatabaseInitialization = skipDatabaseInitialization;
            _seedDevelopmentAccounts = seedDevelopmentAccounts;
            _offlineAttestor = new OfflinePreopenStopAttestor(
                () => _safeConfigurationChecked && _hostedServicesChecked);
        }

        internal Task<StartupFenceBackend> AttestedBackend => _attestor!.AttestedBackend;
        internal Task<bool> RejectedBeforeOpen => _attestor!.RejectedBeforeOpen;
        internal NpgsqlConnection? LockedConnection => _attestor!.LockedConnection;
        internal bool HostedServicesChecked => _hostedServicesChecked;
        internal bool SafeConfigurationChecked => _safeConfigurationChecked;
        internal bool OfflinePreopenValidated => _offlineAttestor?.ValidatedBeforeOpen == true;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Both precedence providers are fake, socket-owned targets. If the later
            // provider is ignored, the host still cannot fall back to the scratch DB.
            var earlyConnectionString = _offlineAttestor is not null
                ? OfflineConnectionString
                : _invalidEndpoint is null
                    ? _fixture!.ConnectionFor(_actor)
                    : new NpgsqlConnectionStringBuilder(_invalidEndpoint)
                    {
                        Database = "unapproved_configuration_baseline"
                    }.ConnectionString;
            var lateConnectionString = _invalidEndpoint ?? earlyConnectionString;
            var retainFeedback = _offlineAttestor is not null;
            var safeSettings = IsolatedReplicaSettings(
                lateConnectionString, _skipDatabaseInitialization,
                _seedDevelopmentAccounts, retainFeedback);

            builder.UseEnvironment(_seedDevelopmentAccounts ? "Development" : "Testing");
            builder.UseSetting("ConnectionStrings:sentencestudio", earlyConnectionString);
            builder.UseSetting("ConnectionStrings:coach", earlyConnectionString);
            builder.UseSetting("ConnectionStrings:feedback", earlyConnectionString);
            builder.UseSetting("Database:SkipMigrateOnStartup",
                _skipDatabaseInitialization ? "true" : "false");
            builder.UseSetting("Auth:SeedDevTestAccounts",
                _seedDevelopmentAccounts ? "true" : "false");
            builder.UseSetting("Auth:EnableDevAuthFallback", "false");
            builder.UseSetting("Jwt:SigningKey", new string('s', 64));
            builder.UseSetting("Coach:Enabled", "false");
            builder.UseSetting("Coach:Cleanup:Enabled", "false");
            builder.UseSetting("Feedback:Enabled", "false");
            builder.UseSetting("Feedback:RetentionSweepEnabled",
                retainFeedback ? "true" : "false");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(safeSettings);
            });

            builder.ConfigureServices((context, services) =>
            {
                if (context.HostingEnvironment.IsDevelopment() != _seedDevelopmentAccounts)
                {
                    throw new InvalidOperationException(
                        "Replica host environment does not match the selected fault scenario.");
                }
                RequireIsolatedReplicaConfiguration(
                    context.Configuration, safeSettings, _seedDevelopmentAccounts);
                if (_offlineAttestor is null &&
                    context.Configuration.GetValue("Feedback:RetentionSweepEnabled", true))
                {
                    throw new InvalidOperationException(
                        "Replica feedback retention must be disabled before PostgreSQL startup.");
                }

                _safeConfigurationChecked = true;
                services.AddSingleton<IStartupConnectionAttestor>(
                    _offlineAttestor ?? (IStartupConnectionAttestor)_attestor!);
                TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(
                    services, context.Configuration);
            });
            builder.ConfigureTestServices(services =>
            {
                if (!_safeConfigurationChecked)
                {
                    throw new InvalidOperationException(
                        "Replica hosted-writer isolation cannot precede configuration isolation.");
                }
                RemoveUnattestedHostedWriters(services, _seedDevelopmentAccounts);
                _hostedServicesChecked = true;
            });
        }

        private static void RemoveUnattestedHostedWriters(
            IServiceCollection services, bool seedDevelopmentAccounts)
        {
            var hosted = services.Where(service => service.ServiceType == typeof(IHostedService))
                .ToArray();
            var feedback = hosted.Where(service =>
                service.ImplementationType == typeof(FeedbackRetentionBackgroundService)).ToArray();
            var validator = hosted.Where(service =>
                service.ImplementationType == typeof(CoachToolRegistryStartupValidator)).ToArray();
            if (feedback.Length != 1 || validator.Length != 1)
            {
                throw new InvalidOperationException(
                    "Replica prewrite gate requires exactly one feedback retention writer and " +
                    "one read-only coach registry validator before removing the writer.");
            }

            var seederDescriptors = services.Where(service =>
                service.ServiceType == typeof(DevTestAccountSeeder)).ToArray();
            if (hosted.Any(service =>
                    service.ImplementationType == typeof(CoachCleanupBackgroundService) ||
                    service.ImplementationType == typeof(DevTestAccountSeeder)) ||
                seederDescriptors.Length != (seedDevelopmentAccounts ? 1 : 0) ||
                (seedDevelopmentAccounts &&
                 (seederDescriptors[0].ImplementationType != typeof(DevTestAccountSeeder) ||
                  seederDescriptors[0].Lifetime != ServiceLifetime.Singleton)))
            {
                throw new InvalidOperationException(
                    "Replica prewrite gate found an unfenced hosted writer or missing/extra " +
                    "pre-listen development account seeder.");
            }

            var apiAssembly = typeof(Program).Assembly;
            var unexpected = hosted.Where(service =>
                (service.ImplementationType?.Assembly == apiAssembly ||
                 service.ImplementationInstance?.GetType().Assembly == apiAssembly ||
                 service.ImplementationFactory?.Method.DeclaringType?.Assembly == apiAssembly) &&
                service.ImplementationType != typeof(FeedbackRetentionBackgroundService) &&
                service.ImplementationType != typeof(CoachToolRegistryStartupValidator))
                .ToArray();
            if (unexpected.Length != 0)
            {
                throw new InvalidOperationException(
                    "Replica prewrite gate found unreviewed API hosted services: " +
                    string.Join(", ", unexpected.Select(service =>
                        service.ImplementationType?.FullName ?? "instance/factory")));
            }

            if (!services.Remove(feedback[0]))
            {
                throw new InvalidOperationException(
                    "Replica prewrite gate could not remove the feedback retention writer.");
            }
        }

        private sealed class OfflinePreopenStopAttestor(Func<bool> isolated)
            : IStartupConnectionAttestor
        {
            internal bool ValidatedBeforeOpen { get; private set; }

            public void ValidateBeforeOpen(NpgsqlConnection connection)
            {
                if (connection.State != System.Data.ConnectionState.Closed || !isolated())
                {
                    throw new InvalidOperationException(
                        "Replica configuration/hosted-writer isolation ran too late " +
                        "for PostgreSQL pre-open validation.");
                }

                ValidatedBeforeOpen = true;
                throw new InvalidOperationException(
                    "Offline replica blocked before PostgreSQL open.");
            }

            public Task AttestOpenAsync(
                NpgsqlConnection connection, CancellationToken cancellationToken)
                => throw new InvalidOperationException(
                    "Offline replica must never attest an open PostgreSQL connection.");
        }
    }

    private sealed class ReplicaConnectionAttestor(
        StartupFencePostgresFixture fixture, string actor, string? invalidEndpoint,
        Func<bool> isolated)
        : IStartupConnectionAttestor
    {
        private readonly TaskCompletionSource<StartupFenceBackend> _attested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _rejected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<StartupFenceBackend> AttestedBackend => _attested.Task;
        internal Task<bool> RejectedBeforeOpen => _rejected.Task;
        internal NpgsqlConnection? LockedConnection { get; private set; }

        public void ValidateBeforeOpen(NpgsqlConnection connection)
        {
            if (!isolated())
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence pre-open: replica configuration or hosted " +
                    "writers were not isolated.");
            }
            try
            {
                fixture.ValidateReplicaBeforeOpen(connection, actor);
            }
            catch (InvalidOperationException) when (invalidEndpoint is not null)
            {
                var configured = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
                var injected = new NpgsqlConnectionStringBuilder(invalidEndpoint);
                _rejected.TrySetResult(
                    connection.State == System.Data.ConnectionState.Closed &&
                    configured.Host == injected.Host && configured.Port == injected.Port &&
                    configured.Database == injected.Database &&
                    configured.Username == injected.Username &&
                    configured.ApplicationName == actor);
                throw;
            }

            if (invalidEndpoint is not null)
            {
                // Only reachable if the normal validator was removed or weakened.
                // Permit a socket only to our sentinel, never to a fallback app DB.
                var actual = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
                var fake = new NpgsqlConnectionStringBuilder(invalidEndpoint);
                if (connection.State != System.Data.ConnectionState.Closed ||
                    actual.Host != fake.Host || actual.Port != fake.Port)
                {
                    throw new InvalidOperationException(
                        "Refusing startup-fence writes: precedence escaped the owned fake socket.");
                }
            }
        }

        public async Task AttestOpenAsync(
            NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            var pid = await fixture.AttestReplicaAsync(connection, actor, cancellationToken);
            var backend = await fixture.ReadOwnBackendAsync(connection, actor);
            if (backend.Pid != pid)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence proof: the attested replica backend changed.");
            }
            LockedConnection = connection;
            _attested.TrySetResult(backend);
        }
    }

}
