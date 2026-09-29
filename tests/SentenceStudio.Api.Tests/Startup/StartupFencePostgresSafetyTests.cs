using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Startup;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Startup;

public sealed class StartupFencePostgresSafetyTests
{
    private const string RunId = "0123456789ab4def8123456789abcdef";
    private const string Cluster = "sfence_0123456789ab4def8123456789abcdef";
    private const string Actor = "sf_0123456789ab4def8123456789abcdef_migration_a";
    private const string Volume = "sfence_pgdata_0123456789ab4def8123456789abcdef";
    private const string ReviewedCatalogSha256 =
        "776683e75782321241d096c2303caa6076ff1fe936ededd039ef8de1cfe0b02f";
    private static readonly string ContainerId = new('a', 64);

    [Theory]
    [InlineData("connection")]
    [InlineData("run-id")]
    [InlineData("cluster-name")]
    [InlineData("volume-name")]
    [InlineData("container-id")]
    public void Opted_in_missing_fields_fail_instead_of_skipping(string missing)
    {
        var connection = ApprovedConnection().ConnectionString;
        Action configure = () => StartupFencePostgresFixture.RequireConfiguration(
            "1", missing == "connection" ? null : connection,
            missing == "run-id" ? null : RunId,
            missing == "cluster-name" ? null : Cluster,
            missing == "volume-name" ? null : Volume,
            missing == "container-id" ? null : ContainerId);

        configure.Should().Throw<InvalidOperationException>();
        StartupFencePostgresFixture.SkipWhenNotOptedIn("1").Should().BeFalse(
            "no missing opt-in variable is a valid xUnit skip condition");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("invalid", false)]
    public void Only_no_opt_in_skips_fault_test(string? enabled, bool shouldSkip)
    {
        StartupFencePostgresFixture.SkipWhenNotOptedIn(enabled).Should().Be(shouldSkip);
    }

    [Fact]
    public void All_fault_scenarios_and_precedence_probes_share_one_attested_fact()
    {
        typeof(StartupReplicaFencePostgresTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType == typeof(StartupFencePostgresFactAttribute)))
            .Should().ContainSingle(
                "a second database fact could accidentally depend on the first one's order");
    }

    [Fact]
    public void Replica_host_isolates_all_database_writers_before_open_even_with_feedback_retention_enabled()
    {
        StartupReplicaFencePostgresTests.VerifyOfflineHostedWriterIsolation();
    }

    [Fact]
    public void Development_seed_host_preserves_preopen_attestation_and_isolates_hosted_writers()
    {
        StartupReplicaFencePostgresTests.VerifyOfflineDevelopmentSeedIsolation();
    }

    [Theory]
    [InlineData("baseline", "Baseline")]
    [InlineData("phrase", "Phrase")]
    [InlineData("coach", "Coach")]
    [InlineData("feedback", "Feedback")]
    [InlineData("devseed", "DevSeed")]
    public void Only_reviewed_scenarios_can_select_a_fault_on_a_new_volume(
        string raw, string expected)
    {
        var scenario = StartupFencePostgresFixture.RequireScenario(raw);
        scenario.ToString().Should().Be(expected);
        var phases = StartupReplicaFencePostgresTests.PhasesForScenario(scenario);
        phases.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
        if (scenario == StartupFencePostgresScenario.Baseline)
        {
            phases.Should().Equal("migration", "numberseed", "backfill", "coresync");
        }
        else if (scenario == StartupFencePostgresScenario.Phrase)
        {
            phases.Should().Equal("migration", "phrase");
        }
        else
        {
            phases.Should().ContainSingle().Which.Should().Be(raw);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy")]
    [InlineData("BASELINE")]
    [InlineData("dev-seed")]
    [InlineData("all")]
    public void Missing_or_unknown_opted_in_scenario_fails_before_docker_or_sql(string? raw)
    {
        Action select = () => StartupFencePostgresFixture.RequireScenario(raw);

        select.Should().Throw<InvalidOperationException>()
            .WithMessage("*SCENARIO*No Docker or SQL was attempted*");
    }

    [Fact]
    public void Undefined_scenario_cannot_fall_back_to_a_different_fault()
    {
        Action select = () => StartupReplicaFencePostgresTests.PhasesForScenario(
            (StartupFencePostgresScenario)999);
        select.Should().Throw<InvalidOperationException>()
            .WithMessage("*undefined startup-fence fault scenario*");
    }

    [Theory]
    [InlineData("backfill", null)]
    [InlineData("phrase", "")]
    [InlineData("numberseed", " ")]
    [InlineData("devseed", "unapproved@test.local")]
    [InlineData("coach", "not-null")]
    [InlineData("unexpected", "any")]
    public void Server_gate_requires_a_known_phase_and_its_exact_write_target(
        string phase, string? target)
    {
        Action arm = () => StartupFencePostgresFixture.RequirePhaseTarget(phase, target);
        arm.Should().Throw<InvalidOperationException>()
            .WithMessage("*server-side write target*");
    }

    [Fact]
    public void Development_gate_targets_only_the_first_builtin_identity_insert()
    {
        var first = DevTestAccountSeeder.Accounts[0].Email.ToUpperInvariant();
        Action arm = () => StartupFencePostgresFixture.RequirePhaseTarget("devseed", first);
        arm.Should().NotThrow();
        DevTestAccountSeeder.Accounts.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("migration", false)]
    [InlineData("numberseed", false)]
    [InlineData("backfill", false)]
    [InlineData("phrase", false)]
    [InlineData("coresync", false)]
    [InlineData("coach", true)]
    [InlineData("feedback", true)]
    [InlineData("devseed", true)]
    public void Late_writer_scenarios_require_an_earlier_server_side_B_takeover_checkpoint(
        string phase, bool expected)
    {
        StartupFencePostgresFixture.RequiresCoreSyncTakeoverCheckpoint(phase)
            .Should().Be(expected);
    }

    [Fact]
    public void Unknown_writer_cannot_silently_skip_the_earlier_takeover_checkpoint()
    {
        Action validate = () =>
            StartupFencePostgresFixture.RequiresCoreSyncTakeoverCheckpoint("unknown");
        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown startup writer*");
    }

    [Fact]
    public void Phrase_seed_is_selected_by_the_real_user_progress_and_token_matching_rules()
    {
        var seed = StartupFencePostgresFixture.CreatePhraseSeed();
        var words = new[] { seed.Phrase }.Concat(seed.ConstituentWords).ToArray();
        Guid.TryParse(seed.UserId, out var parsedUser).Should().BeTrue();
        parsedUser.Should().NotBe(Guid.Empty);
        seed.Progress.Should().HaveCount(3).And.OnlyContain(
            progress => progress.UserId == seed.UserId &&
                        words.Any(word => word.Id == progress.VocabularyWordId &&
                                          ReferenceEquals(word, progress.VocabularyWord)));
        seed.Progress.Select(progress => progress.VocabularyWordId)
            .Should().OnlyHaveUniqueItems();
        seed.Phrase.LexicalUnitType.Should().Be(LexicalUnitType.Phrase);
        VocabularyClassificationBackfillService.ClassifyHeuristic(
            seed.Phrase.TargetLanguageTerm!, null).Should().Be(LexicalUnitType.Phrase);
        seed.ConstituentWords.Should().OnlyContain(word =>
            word.LexicalUnitType == LexicalUnitType.Word &&
            VocabularyClassificationBackfillService.ClassifyHeuristic(
                word.TargetLanguageTerm!, null) == LexicalUnitType.Word);
        VocabularyClassificationBackfillService.TokenizePhrase(
                seed.Phrase.TargetLanguageTerm!, seed.Phrase.Language!)
            .Should().Equal(seed.ConstituentWords.Select(word => word.TargetLanguageTerm));
    }

    [Theory]
    [InlineData("pg_catalog")]
    [InlineData("pg_toast")]
    [InlineData("information_schema")]
    public void A_preexisting_object_in_a_permitted_nonpublic_schema_fails_preflight(
        string schema)
    {
        var baseline = FreshSystemCatalogSample();
        var modified = baseline.Concat(new[]
        {
            new StartupFenceCatalogObject("routine", schema, 14000, "unknown_target")
        }).ToArray();

        Action validate = () => StartupFencePostgresFixture.ValidateSystemCatalogSnapshot(
            baseline, modified);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*permitted non-public schemas*");
    }

    [Fact]
    public void Source_derived_nonpublic_catalog_manifest_accepts_only_its_complete_inventory()
    {
        var baseline = FreshSystemCatalogSample();

        Action validate = () => StartupFencePostgresFixture.ValidateSystemCatalogSnapshot(
            baseline, baseline);

        validate.Should().NotThrow();
        StartupFencePostgresFixture.ReviewedInformationSchemaViews.Should().HaveCount(65);
        StartupFencePostgresFixture.ReviewedInformationSchemaTables.Should().HaveCount(4);
        StartupFencePostgresFixture.ReviewedInformationSchemaFunctions.Should().HaveCount(11);
        StartupFencePostgresFixture.ReviewedInformationSchemaDomains.Should().HaveCount(5);
    }

    [Fact]
    public void A_uniformly_cloned_information_schema_object_cannot_be_the_trusted_baseline()
    {
        var preexisting = FreshSystemCatalogSample()
            .Append(new StartupFenceCatalogObject(
                "routine", "information_schema", 15000, "unknown_target"))
            .ToArray();

        Action validate = () => StartupFencePostgresFixture.ValidateSystemCatalogSnapshot(
            preexisting, preexisting);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*permitted non-public schemas*");
    }

    [Fact]
    public void A_user_oid_in_system_schemas_fails_even_if_all_database_snapshots_match()
    {
        var preexisting = FreshSystemCatalogSample()
            .Append(new StartupFenceCatalogObject(
                "routine", "pg_catalog", 17000, "unknown_target"))
            .ToArray();

        Action validate = () => StartupFencePostgresFixture.ValidateSystemCatalogSnapshot(
            preexisting, preexisting);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*permitted non-public schemas*");
    }

    [Theory]
    [InlineData("startup_fence_0123456789ab4def8123456789abcdef")]
    [InlineData("postgres")]
    [InlineData("template1")]
    public void Independently_reviewed_catalog_digest_is_accepted_for_each_connectable_database(
        string database)
    {
        Action validate = () => StartupFencePostgresFixture.ValidateVirginCatalogDigest(
            database, 10510, ReviewedCatalogSha256);

        validate.Should().NotThrow();
    }

    [Fact]
    public void Definition_digest_covers_function_and_view_bodies_instead_of_only_names()
    {
        var sql = StartupFencePostgresFixture.VirginCatalogDigestSql;

        sql.Should().Contain("to_jsonb(p)")
            .And.Contain("FROM pg_proc p")
            .And.Contain("to_jsonb(r)")
            .And.Contain("FROM pg_rewrite r")
            .And.Contain("to_jsonb(a)")
            .And.Contain("FROM pg_attribute a")
            .And.Contain("ORDER BY schema_name, kind, definition::text");
        sql.Should().NotContain("BEGIN READ ONLY").And.NotContain("COMMIT");
    }

    [Fact]
    public void Catalog_digest_query_matches_the_exact_reviewed_select()
    {
        var query = StartupFencePostgresFixture.VirginCatalogDigestSql
            .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var querySha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();

        querySha256.Should().Be(
            "34205c1114bfae3e127609812d69669f1a2d83080c4c93495cd3c60b788a8cc5");
    }

    [Fact]
    public void Changed_definition_digest_fails_even_with_unchanged_object_names_and_oids()
    {
        var namesAndOids = FreshSystemCatalogSample();
        var replacementNamesAndOids = namesAndOids.ToArray();
        StartupFencePostgresFixture.ValidateSystemCatalogSnapshot(
            namesAndOids, replacementNamesAndOids);
        var changedDefinitionDigest = "0" + ReviewedCatalogSha256[1..];

        Action validate = () => StartupFencePostgresFixture.ValidateVirginCatalogDigest(
            "template1", 10510, changedDefinitionDigest);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*template1 catalog definition SHA-256*");
    }

    [Theory]
    [InlineData(10509)]
    [InlineData(10511)]
    public void Wrong_catalog_object_count_fails_before_any_marker_write(long count)
    {
        Action validate = () => StartupFencePostgresFixture.ValidateVirginCatalogDigest(
            "postgres", count, ReviewedCatalogSha256);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage($"*postgres catalog has {count} objects*requires 10510*");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("nonhex")]
    [InlineData("uppercase")]
    public void Malformed_catalog_digest_fails_closed(string malformed)
    {
        string? digest = malformed switch
        {
            "null" => null,
            "empty" => "",
            "short" => "1234",
            "nonhex" => new string('g', 64),
            "uppercase" => ReviewedCatalogSha256.ToUpperInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(malformed))
        };
        Action validate = () => StartupFencePostgresFixture.ValidateVirginCatalogDigest(
            "startup_fence_scratch", 10510, digest);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*startup_fence_scratch catalog SHA-256 is not 64 lowercase*");
    }

    private static StartupFenceCatalogObject[] FreshSystemCatalogSample()
    {
        var result = new List<StartupFenceCatalogObject>();
        long oid = 100;
        void Add(string kind, string schema, string name)
            => result.Add(new StartupFenceCatalogObject(kind, schema, oid++, name));

        Add("relation", "pg_catalog", "pg_class:r");
        Add("relation", "pg_toast", "pg_toast_1:t");
        foreach (var view in StartupFencePostgresFixture.ReviewedInformationSchemaViews)
        {
            Add("relation", "information_schema", view + ":v");
            Add("rewrite", "information_schema", "_RETURN");
            Add("type", "information_schema", view);
            Add("type", "information_schema", "_" + view);
        }
        foreach (var table in StartupFencePostgresFixture.ReviewedInformationSchemaTables)
        {
            Add("relation", "information_schema", table + ":r");
            Add("type", "information_schema", table);
            Add("type", "information_schema", "_" + table);
        }
        foreach (var function in StartupFencePostgresFixture.ReviewedInformationSchemaFunctions)
        {
            Add("routine", "information_schema", function);
        }
        foreach (var domain in StartupFencePostgresFixture.ReviewedInformationSchemaDomains)
        {
            Add("type", "information_schema", domain);
            Add("type", "information_schema", "_" + domain);
        }
        Add("constraint", "information_schema", "cardinal_number_domain_check");
        Add("constraint", "information_schema", "yes_or_no_check");
        return result.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Oid)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void Rejects_reused_or_ambiguous_docker_identity_without_opening_a_socket()
    {
        var connection = ApprovedConnection().ConnectionString;
        Action reusedVolume = () => StartupFencePostgresFixture.RequireConfiguration(
            "1", connection, RunId, Cluster, "old_pgdata", ContainerId);
        Action shortContainerId = () => StartupFencePostgresFixture.RequireConfiguration(
            "1", connection, RunId, Cluster, Volume, ContainerId[..12]);
        Action invalidFlag = () => StartupFencePostgresFixture.RequireConfiguration(
            "maybe", connection, RunId, Cluster, Volume, ContainerId);

        reusedVolume.Should().Throw<InvalidOperationException>();
        shortContainerId.Should().Throw<InvalidOperationException>();
        invalidFlag.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Host=example.invalid;Port=55479;Database=startup_fence_0123456789ab4def8123456789abcdef;Username=startup_fence_owner")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=startup_fence_0123456789ab4def8123456789abcdef;Username=startup_fence_owner")]
    [InlineData("Host=127.0.0.1;Port=55479;Database=sentencestudio;Username=startup_fence_owner")]
    [InlineData("Host=127.0.0.1;Port=55479;Database=startup_fence_0123456789ab4def8123456789abcdef;Username=dbadmin")]
    public void Rejects_remote_default_port_or_nondedicated_database_and_role(string connection)
    {
        Action validate = () => StartupFencePostgresFixture.ValidateConfiguration(
            connection, RunId, Cluster, out _);

        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Requires_the_exact_per_run_cluster_name_and_marker_identifier()
    {
        const string connection = "Host=127.0.0.1;Port=55479;" +
            "Database=startup_fence_0123456789ab4def8123456789abcdef;Username=startup_fence_owner";
        Action wrongCluster = () => StartupFencePostgresFixture.ValidateConfiguration(
            connection, RunId, "another-cluster", out _);
        Action wrongRun = () => StartupFencePostgresFixture.ValidateConfiguration(
            connection, "not-a-guid", Cluster, out _);

        wrongCluster.Should().Throw<InvalidOperationException>();
        wrongRun.Should().Throw<InvalidOperationException>();

        var safe = StartupFencePostgresFixture.ValidateConfiguration(
            connection, RunId, Cluster, out var parsed);
        parsed.ToString("N").Should().Be(RunId);
        safe.Host.Should().Be("127.0.0.1");
        safe.Pooling.Should().BeFalse();
        safe.Multiplexing.Should().BeFalse();
    }

    [Fact]
    public void Signed_SQL_high_bit_identifier_matches_only_the_same_unsigned_Docker_bits()
    {
        const ulong systemIdentifier = 0x8000_0000_0000_0042UL;
        var signedSql = unchecked((long)systemIdentifier).ToString(CultureInfo.InvariantCulture);
        var unsignedDocker = systemIdentifier.ToString(CultureInfo.InvariantCulture);

        signedSql.Should().StartWith("-");
        StartupFencePostgresFixture.MatchesDockerSystemIdentifier(signedSql, unsignedDocker)
            .Should().BeTrue();
        StartupFencePostgresFixture.MatchesDockerSystemIdentifier(
                signedSql, (systemIdentifier + 1).ToString(CultureInfo.InvariantCulture))
            .Should().BeFalse("different clusters must never attest as the same cluster");
        StartupFencePostgresFixture.MatchesDockerSystemIdentifier(
                unchecked((long)(systemIdentifier + 1)).ToString(CultureInfo.InvariantCulture),
                unsignedDocker)
            .Should().BeFalse();
        StartupFencePostgresFixture.MatchesDockerSystemIdentifier("42", "42")
            .Should().BeTrue("low-bit cluster identifiers must still be accepted");
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("", "1")]
    [InlineData("0", "0")]
    [InlineData("0", "1")]
    [InlineData("1", null)]
    [InlineData("1", "")]
    [InlineData("1", "0")]
    [InlineData("invalid", "1")]
    [InlineData("1", "invalid")]
    [InlineData("9223372036854775808", "9223372036854775808")]
    [InlineData("-9223372036854775809", "9223372036854775807")]
    [InlineData("-1", "18446744073709551616")]
    [InlineData(" -1", "18446744073709551615")]
    [InlineData("-01", "18446744073709551615")]
    [InlineData("-1", "018446744073709551615")]
    [InlineData("+1", "1")]
    [InlineData("1", "-1")]
    public void Missing_zero_or_malformed_system_identifiers_fail_closed(
        string? signedSql, string? unsignedDocker)
    {
        StartupFencePostgresFixture.MatchesDockerSystemIdentifier(signedSql, unsignedDocker)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("database")]
    [InlineData("role")]
    [InlineData("actor")]
    [InlineData("pooling")]
    [InlineData("search_path")]
    public void Actual_replica_target_must_match_the_validated_monitor_before_open(string mutation)
    {
        var approved = ApprovedConnection();
        var target = new NpgsqlConnectionStringBuilder(approved.ConnectionString)
        {
            ApplicationName = Actor
        };
        switch (mutation)
        {
            case "host": target.Host = "example.invalid"; break;
            case "port": target.Port++; break;
            case "database": target.Database = "different_scratch_database"; break;
            case "role": target.Username = "different_role"; break;
            case "actor": target.ApplicationName = "different_actor"; break;
            case "pooling": target.Pooling = true; break;
            case "search_path": target.SearchPath = "different_schema"; break;
        }

        using var replica = new NpgsqlConnection(target.ConnectionString);
        Action check = () => StartupFencePostgresFixture.ValidateReplicaTarget(
            replica, approved, Actor);
        check.Should().Throw<InvalidOperationException>()
            .WithMessage("*actual replica target*");
    }

    [Fact]
    public void Matching_replica_target_can_pass_the_preopen_check_without_a_database()
    {
        var approved = ApprovedConnection();
        using var replica = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(approved.ConnectionString)
            {
                ApplicationName = Actor
            }.ConnectionString);

        Action check = () => StartupFencePostgresFixture.ValidateReplicaTarget(
            replica, approved, Actor);
        check.Should().NotThrow();
        replica.State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public void Configuration_precedence_sentinel_cannot_reuse_the_attested_postgres_port()
    {
        Action distinct = () => StartupReplicaFencePostgresTests
            .RequireSentinelPortDistinctFromPostgres(55480, 55479);
        Action identical = () => StartupReplicaFencePostgresTests
            .RequireSentinelPortDistinctFromPostgres(55479, 55479);

        distinct.Should().NotThrow();
        identical.Should().Throw<InvalidOperationException>()
            .WithMessage("*sentinel port matches the attested PostgreSQL host port*");
    }

    [Fact]
    public async Task Configuration_precedence_late_successful_accept_fails_and_closes_the_socket()
    {
        var accepted = new TaskCompletionSource<TcpClient>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = StartupReplicaFencePostgresTests
            .AssertNoSentinelSocketAcceptedAsync(accepted.Task);
        cleanup.IsCompleted.Should().BeFalse();

        using var client = new TcpClient();
        var socket = client.Client;
        accepted.SetResult(client);

        Func<Task> finish = () => cleanup;
        await finish.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pre-audit configuration-precedence probe reached its TCP sentinel*");
        socket.SafeHandle.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task Configuration_precedence_accept_cleanup_ignores_only_expected_shutdown_failures()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await StartupReplicaFencePostgresTests.AssertNoSentinelSocketAcceptedAsync(
            Task.FromCanceled<TcpClient>(cancellation.Token));
        await StartupReplicaFencePostgresTests.AssertNoSentinelSocketAcceptedAsync(
            Task.FromException<TcpClient>(new ObjectDisposedException(nameof(TcpListener))));
        await StartupReplicaFencePostgresTests.AssertNoSentinelSocketAcceptedAsync(
            Task.FromException<TcpClient>(
                new SocketException((int)SocketError.OperationAborted)));

        Func<Task> unexpected = () =>
            StartupReplicaFencePostgresTests.AssertNoSentinelSocketAcceptedAsync(
                Task.FromException<TcpClient>(
                    new SocketException((int)SocketError.ConnectionReset)));
        await unexpected.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task The_lock_calls_the_attestor_on_its_actual_connection_before_open()
    {
        var guard = new RejectingAttestor();
        Func<Task> acquire = async () =>
        {
            await PostgresApplicationStartupLock.AcquireAsync(
                "Host=127.0.0.1;Port=1;Database=invalid;Username=invalid;Timeout=1",
                attestor: guard);
        };

        await acquire.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*guard rejected before open*");
        guard.ValidatedState.Should().Be(ConnectionState.Closed);
        guard.OpenAttestationCalled.Should().BeFalse();
    }

    [Fact]
    public void The_exact_provider_history_schema_can_be_generated_without_connecting()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=invalid")
            .Options;
        using var context = new ApplicationDbContext(options);

        var script = context.GetService<IHistoryRepository>().GetCreateScript();

        script.Should().Contain("\"__EFMigrationsHistory\"");
        script.Should().Contain("\"MigrationId\"");
        script.Should().Contain("\"ProductVersion\"");
    }

    [Theory]
    [InlineData("startup_fence_dml")]
    [InlineData("startup_fence_truncate")]
    public void Ddl_end_audit_only_enables_triggers_not_already_always(string triggerName)
    {
        var sql = Regex.Replace(
            StartupFencePostgresFixture.AttachDmlAuditFunctionSql, @"\s+", " ");
        var enable = $"ALTER TABLE %I.%I ENABLE ALWAYS TRIGGER {triggerName}";
        var guard = "IF EXISTS ( SELECT 1 FROM pg_trigger " +
            "WHERE tgrelid = format('%I.%I', target.nspname, target.relname)::regclass " +
            $"AND tgname = '{triggerName}' AND tgenabled <> 'A' ) THEN " +
            $"EXECUTE format('{enable}', target.nspname, target.relname); END IF;";

        sql.Should().Contain("FROM pg_event_trigger_ddl_commands() ddl")
            .And.Contain(guard);
        Regex.Matches(sql,
            $@"ALTER\s+TABLE\s+%I\.%I\s+ENABLE\s+ALWAYS\s+TRIGGER\s+{triggerName}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Count.Should().Be(1,
            "an extra unconditional ALTER TABLE would re-enter the ddl_command_end handler");
        var creation = sql.IndexOf($"'CREATE TRIGGER {triggerName} ", StringComparison.Ordinal);
        creation.Should().BeGreaterThan(-1);
        sql.IndexOf(enable, StringComparison.Ordinal).Should().BeGreaterThan(creation);
    }

    [Fact]
    public void Faults_stall_only_after_real_DDL_or_inside_the_first_matching_row_write()
    {
        var ddl = StartupFencePostgresFixture.DdlEndGateFunctionSql;
        ddl.Should().Contain("pg_event_trigger_ddl_commands()")
            .And.Contain("to_regclass(")
            .And.Contain("ddl.objid <> to_regclass('public.\"__EFMigrationsHistory\"')::oid")
            .And.Contain("__CoachMigrationsHistory")
            .And.Contain("__FeedbackMigrationsHistory")
            .And.Contain("SELECT 1 FROM pg_trigger trigger_row")
            .And.Contain("CREATE FUNCTION")
            .And.Contain("CREATE TRIGGER")
            .And.Contain("position('_ct_' in trigger_row.tgname)")
            .And.Contain("gate_row.takeover_lock_key")
            .And.NotContain("current_query()")
            .And.NotContain("__core_sync_ct");
        var drop = StartupFencePostgresFixture.SqlDropGateFunctionSql;
        drop.Should().Contain("pg_event_trigger_dropped_objects()")
            .And.Contain("dropped.original")
            .And.Contain("dropped.object_type = 'trigger'")
            .And.Contain("position('_ct_' in dropped.object_identity)")
            .And.Contain("gate_row.takeover_lock_key");
        StartupFencePostgresFixture.SqlDropAuditFunctionSql
            .Should().Contain("pg_event_trigger_dropped_objects()")
            .And.Contain("'DROP ' || dropped.object_type");
        StartupFencePostgresFixture.DdlGateTriggerSql
            .Should().Contain("ON ddl_command_end")
            .And.NotContain("ddl_command_start");
        StartupFencePostgresFixture.DropGateTriggerSql
            .Should().Contain("ON sql_drop");
        StartupFencePostgresFixture.DropAuditTriggerSql
            .Should().Contain("ON sql_drop");
        string.CompareOrdinal(StartupFencePostgresFixture.DdlGateTriggerName,
            StartupFencePostgresFixture.AttachDmlTriggerName).Should().BeNegative(
            "the genuine DDL must block before autoattached test triggers write");
        string.CompareOrdinal(StartupFencePostgresFixture.DdlGateTriggerName,
            StartupFencePostgresFixture.DdlAuditTriggerName).Should().BeNegative(
            "B's first DDL audit event must be allocated after the takeover");
        string.CompareOrdinal(StartupFencePostgresFixture.DropGateTriggerName,
            StartupFencePostgresFixture.DropAuditTriggerName).Should().BeNegative(
            "B's dropped-trigger event must be allocated after the takeover");

        var attach = StartupFencePostgresFixture.AttachDmlAuditFunctionSql;
        attach.Should().Contain("startup_fence_row_gate BEFORE INSERT OR UPDATE")
            .And.Contain("ENABLE ALWAYS TRIGGER startup_fence_row_gate");
        foreach (var table in new[]
            {
                "VocabularyWord", "NumberContext", "PhraseConstituent", "AspNetUsers"
            })
        {
            attach.Should().Contain($"'{table}'");
        }

        var rowGate = StartupFencePostgresFixture.RowGateFunctionSql;
        foreach (var phase in new[] { "numberseed", "backfill", "phrase", "devseed" })
        {
            rowGate.Should().Contain($"gate_row.phase = '{phase}'");
        }
        rowGate.Should().Contain("row_data->>'PhraseWordId' = gate_row.target_id")
            .And.Contain("row_data->>'NormalizedUserName' = gate_row.target_id")
            .And.Contain("pg_advisory_lock(gate_row.lock_key)")
            .And.Contain("RETURN NEW;");
        typeof(StartupReplicaFencePostgresTests)
            .GetNestedType("StartupSaveChangesGate", BindingFlags.NonPublic)
            .Should().BeNull("client SaveChangesStarting cannot prove a server write was in progress");
    }

    [Fact]
    public void Development_configuration_contains_no_ambient_endpoint_or_secret()
    {
        var settings = StartupReplicaFencePostgresTests.IsolatedReplicaSettings(
            ApprovedConnection().ConnectionString,
            skipDatabaseInitialization: false,
            seedDevelopmentAccounts: true,
            retainFeedback: false);
        settings["Auth:SeedDevTestAccounts"].Should().Be("true");
        settings.Should().NotContainKey("AI:OpenAI:Endpoint")
            .And.NotContainKey("AzureMonitor:ConnectionString")
            .And.NotContainKey("ElevenLabsKey");
        var safe = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Action accept = () => StartupReplicaFencePostgresTests
            .RequireIsolatedReplicaConfiguration(safe, settings, seedDevelopmentAccounts: true);
        accept.Should().NotThrow();

        var injectedEndpoint = settings.ToDictionary(
            setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
        injectedEndpoint["AI:OpenAI:Endpoint"] = "https://example.invalid";
        var endpoint = new ConfigurationBuilder().AddInMemoryCollection(injectedEndpoint).Build();
        Action rejectEndpoint = () => StartupReplicaFencePostgresTests
            .RequireIsolatedReplicaConfiguration(endpoint, settings, seedDevelopmentAccounts: true);
        rejectEndpoint.Should().Throw<InvalidOperationException>()
            .WithMessage("*unapproved setting*");

        var differentDatabase = settings.ToDictionary(
            setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
        differentDatabase["ConnectionStrings:feedback"] = "Host=127.0.0.1;Database=not_scratch";
        var mismatched = new ConfigurationBuilder().AddInMemoryCollection(differentDatabase).Build();
        Action rejectDatabase = () => StartupReplicaFencePostgresTests
            .RequireIsolatedReplicaConfiguration(mismatched, settings, seedDevelopmentAccounts: true);
        rejectDatabase.Should().Throw<InvalidOperationException>()
            .WithMessage("*replaced an isolated test-only setting*");

        Action bypass = () => StartupReplicaFencePostgresTests.IsolatedReplicaSettings(
            ApprovedConnection().ConnectionString,
            skipDatabaseInitialization: true,
            seedDevelopmentAccounts: true,
            retainFeedback: false);
        bypass.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot bypass the startup lock*");
    }

    [Fact]
    public void A_server_takeover_requires_distinct_backends_and_B_write_after_recorded_release()
    {
        var a = new StartupFenceBackend(201, EpochMicroseconds(1));
        var b = new StartupFenceBackend(202, EpochMicroseconds(2));
        var monitor = new StartupFenceBackend(100, EpochMicroseconds(3));
        var aActor = $"sf_{RunId}_phrase_a";
        var bActor = $"sf_{RunId}_phrase_b";
        StartupFenceAuditRow[] rows =
        [
            AuditRow(1, "phrase", "login", aActor, a),
            AuditRow(2, "phrase", "login", bActor, b),
            AuditRow(4, "phrase", "takeover", "startup-fence-monitor", monitor)
                with { SubjectPid = b.Pid },
            AuditRow(5, "phrase", "dml", bActor, b)
        ];

        Action validate = () => StartupFencePostgresFixture.ValidateTakeoverOrdering(
            rows, "phrase", aActor, bActor, a, b, takeoverId: 4);
        validate.Should().NotThrow();

        var before = rows.Append(AuditRow(3, "phrase", "dml", bActor, b))
            .OrderBy(row => row.EventId).ToArray();
        Action bBefore = () => StartupFencePostgresFixture.ValidateTakeoverOrdering(
            before, "phrase", aActor, bActor, a, b, takeoverId: 4);
        bBefore.Should().Throw<InvalidOperationException>()
            .WithMessage("*server takeover audit ordering violation*");

        var after = rows.Append(AuditRow(6, "phrase", "dml", aActor, a)).ToArray();
        Action aAfter = () => StartupFencePostgresFixture.ValidateTakeoverOrdering(
            after, "phrase", aActor, bActor, a, b, takeoverId: 4);
        aAfter.Should().Throw<InvalidOperationException>()
            .WithMessage("*server takeover audit ordering violation*");

        var wrongBackend = rows[..^1].Append(
            AuditRow(5, "phrase", "dml", bActor,
                b with { Pid = b.Pid + 1 })).ToArray();
        Action bWrongPid = () => StartupFencePostgresFixture.ValidateTakeoverOrdering(
            wrongBackend, "phrase", aActor, bActor, a, b, takeoverId: 4);
        bWrongPid.Should().Throw<InvalidOperationException>()
            .WithMessage("*server takeover audit ordering violation*");

        Action reusedPid = () => StartupFencePostgresFixture.ValidateTakeoverOrdering(
            rows, "phrase", aActor, bActor, a, b with { Pid = a.Pid }, takeoverId: 4);
        reusedPid.Should().Throw<InvalidOperationException>()
            .WithMessage("*server takeover audit ordering violation*");

        Action noWrite = () => StartupFencePostgresFixture.ValidateTakeoverOrdering(
            rows[..^1], "phrase", aActor, bActor, a, b, takeoverId: 4);
        noWrite.Should().Throw<InvalidOperationException>()
            .WithMessage("*server takeover audit ordering violation*");
    }

    [Theory]
    [InlineData("startup-fence-monitor", "login")]
    [InlineData("startup-fence-monitor", "ddl")]
    [InlineData("startup-fence-monitor", "dml")]
    [InlineData("startup-fence-gate", "login")]
    [InlineData("startup-fence-gate", "ddl")]
    [InlineData("startup-fence-gate", "dml")]
    [InlineData("startup-fence-killer", "login")]
    [InlineData("startup-fence-killer", "ddl")]
    [InlineData("startup-fence-killer", "dml")]
    public void Fixture_looking_actor_on_unattested_backend_fails_proof(
        string actor, string kind)
    {
        var spoof = new StartupFenceBackend(303, EpochMicroseconds(3));
        var rows = new[]
        {
            AuditRow(1, "unattributed", kind, actor, spoof)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, AttestedFixtureBackends(), ExpectedReplicaBackends());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*unattested or extra backend*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Additional_B_login_fails_even_if_the_actor_matches_or_the_pid_is_reused(
        bool reusePid)
    {
        var expectedB = new StartupFenceBackend(202, EpochMicroseconds(2));
        var extraB = reusePid
            ? expectedB with
            {
                BackendStartMicroseconds = expectedB.BackendStartMicroseconds + 1
            }
            : expectedB with { Pid = 203 };
        var rows = new[]
        {
            AuditRow(1, "migration", "login", $"sf_{RunId}_migration_b", expectedB),
            AuditRow(2, "migration", "login", $"sf_{RunId}_migration_b", extraB)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, AttestedFixtureBackends(), ExpectedReplicaBackends());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*unattested or extra backend*");
    }

    [Fact]
    public void Previous_A_actor_on_a_second_backend_fails_whole_run_provenance()
    {
        var previousA = new StartupFenceBackend(204, EpochMicroseconds(4));
        var rows = new[]
        {
            AuditRow(1, "migration", "login", Actor, ExpectedReplicaBackends()[0].Backend),
            AuditRow(2, "migration", "login", Actor, previousA)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, AttestedFixtureBackends(), ExpectedReplicaBackends());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*unattested or extra backend*");
    }

    [Fact]
    public void Runtime_B_backend_cannot_be_reclassified_as_a_startup_writer()
    {
        var runtimeB = new StartupFenceBackend(205, EpochMicroseconds(5));
        var expected = ExpectedReplicaBackends()
            .Append(new StartupFenceExpectedReplicaBackend(
                $"sf_{RunId}_migration_b", "migration", runtimeB, LoginOnly: true))
            .ToArray();
        var rows = new[]
        {
            AuditRow(1, "migration", "login", expected[2].Actor, runtimeB),
            AuditRow(2, "migration", "dml", expected[2].Actor, runtimeB)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, AttestedFixtureBackends(), expected);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*unattested or extra backend*");
    }

    [Fact]
    public void Attested_fixture_backends_can_log_their_exact_expected_events()
    {
        var fixture = AttestedFixtureBackends();
        var monitor = fixture.Single(entry => entry.Value == "startup-fence-monitor").Key;
        var gate = fixture.Single(entry => entry.Value == "startup-fence-gate").Key;
        var killer = fixture.Single(entry => entry.Value == "startup-fence-killer").Key;
        var replicas = ExpectedReplicaBackends();
        var rows = new[]
        {
            AuditRow(1, "unattributed", "ddl", "startup-fence-monitor", monitor),
            AuditRow(2, "unattributed", "login", "startup-fence-gate", gate),
            AuditRow(3, "unattributed", "login", "startup-fence-killer", killer),
            AuditRow(4, "migration", "login", Actor, replicas[0].Backend),
            AuditRow(5, "migration", "login", replicas[1].Actor, replicas[1].Backend),
            AuditRow(6, "migration", "takeover", "startup-fence-monitor", monitor) with
                { SubjectPid = replicas[1].Backend.Pid },
            AuditRow(7, "migration", "dml", replicas[1].Actor, replicas[1].Backend)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, fixture, replicas);

        validate.Should().NotThrow();
        StartupFencePostgresFixture.ValidateFixtureLogins(rows, fixture);
    }

    [Theory]
    [InlineData("migration")]
    [InlineData("numberseed")]
    [InlineData("backfill")]
    [InlineData("coresync")]
    [InlineData("phrase")]
    [InlineData("coach")]
    [InlineData("feedback")]
    [InlineData("devseed")]
    public void Each_reviewed_phase_accepts_an_attested_monitor_takeover(string phase)
    {
        var (rows, fixture, replicas) = ValidTakeoverAudit(phase);

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, fixture, replicas);

        validate.Should().NotThrow();
    }

    [Theory]
    [InlineData("unknown", "startup-fence-monitor")]
    [InlineData("PHRASE", "startup-fence-monitor")]
    [InlineData("phrase", "startup-fence-monitor")]
    [InlineData("phrase", "startup-fence-gate")]
    [InlineData("devseed", "spoofed-monitor")]
    public void Unknown_phase_or_spoofed_monitor_actor_cannot_claim_a_takeover(
        string phase, string actor)
    {
        var (rows, fixture, replicas) = ValidTakeoverAudit("migration");
        rows[3] = rows[3] with { Phase = phase, Actor = actor };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, fixture, replicas);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*attested fixture backend changed actor*");
    }

    [Fact]
    public void Monitor_takeover_requires_the_attested_backend_start_and_a_subject_pid()
    {
        var (rows, fixture, replicas) = ValidTakeoverAudit("feedback");
        var reusedPid = rows.ToArray();
        reusedPid[3] = reusedPid[3] with
        {
            BackendStartMicroseconds = reusedPid[3].BackendStartMicroseconds + 1
        };
        Action validateReusedPid = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            reusedPid, fixture, replicas);
        validateReusedPid.Should().Throw<InvalidOperationException>()
            .WithMessage("*unattested or extra backend*");

        var missingSubject = rows.ToArray();
        missingSubject[3] = missingSubject[3] with { SubjectPid = null };
        Action validateMissingSubject = () =>
            StartupFencePostgresFixture.ValidateAuditProvenance(
                missingSubject, fixture, replicas);
        validateMissingSubject.Should().Throw<InvalidOperationException>()
            .WithMessage("*attested fixture backend changed actor*");
    }

    [Fact]
    public void Auxiliary_attestation_requires_its_own_audited_login_at_final_proof()
    {
        var fixture = AttestedFixtureBackends();
        var gate = fixture.Single(entry => entry.Value == "startup-fence-gate").Key;
        var rows = new[]
        {
            AuditRow(1, "unattributed", "login", "startup-fence-gate", gate)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateFixtureLogins(
            rows, fixture);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*auxiliary login is missing or duplicated*");
    }

    [Fact]
    public void Audit_event_ids_must_remain_monotonic_across_fixture_events()
    {
        var monitor = AttestedFixtureBackends()
            .Single(entry => entry.Value == "startup-fence-monitor").Key;
        var rows = new[]
        {
            AuditRow(2, "unattributed", "ddl", "startup-fence-monitor", monitor),
            AuditRow(1, "unattributed", "dml", "startup-fence-monitor", monitor)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, AttestedFixtureBackends(), ExpectedReplicaBackends());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*event order or server identity is invalid*");
    }

    [Fact]
    public void A_fixture_backend_changing_its_application_name_fails_proof()
    {
        var monitor = AttestedFixtureBackends()
            .Single(entry => entry.Value == "startup-fence-monitor").Key;
        var rows = new[]
        {
            AuditRow(1, "unattributed", "ddl", "startup-fence-killer", monitor)
        };

        Action validate = () => StartupFencePostgresFixture.ValidateAuditProvenance(
            rows, AttestedFixtureBackends(), ExpectedReplicaBackends());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*attested fixture backend changed actor*");
    }

    private static StartupFenceAuditRow AuditRow(
        long id, string phase, string kind, string actor, StartupFenceBackend backend)
        => new(id, phase, kind, actor, backend.Pid, backend.BackendStartMicroseconds,
            null, kind == "login" ? "session opened" : "operation", id);

    private static long EpochMicroseconds(int minutes) => minutes * 60_000_000L;

    private static (
        StartupFenceAuditRow[] Rows,
        Dictionary<StartupFenceBackend, string> Fixture,
        StartupFenceExpectedReplicaBackend[] Replicas)
        ValidTakeoverAudit(string phase)
    {
        var fixture = AttestedFixtureBackends();
        var monitor = fixture.Single(entry => entry.Value == "startup-fence-monitor").Key;
        var a = new StartupFenceBackend(201, EpochMicroseconds(1));
        var b = new StartupFenceBackend(202, EpochMicroseconds(2));
        var aActor = $"sf_{RunId}_{phase}_a";
        var bActor = $"sf_{RunId}_{phase}_b";
        StartupFenceExpectedReplicaBackend[] replicas =
        [
            new(aActor, phase, a),
            new(bActor, phase, b)
        ];
        StartupFenceAuditRow[] rows =
        [
            AuditRow(1, "unattributed", "ddl", "startup-fence-monitor", monitor),
            AuditRow(2, phase, "login", aActor, a),
            AuditRow(3, phase, "login", bActor, b),
            AuditRow(4, phase, "takeover", "startup-fence-monitor", monitor) with
                { SubjectPid = b.Pid },
            AuditRow(5, phase, "dml", bActor, b)
        ];

        return (rows, fixture, replicas);
    }

    private static Dictionary<StartupFenceBackend, string> AttestedFixtureBackends()
        => new()
        {
            [new StartupFenceBackend(100, EpochMicroseconds(1))] =
                "startup-fence-monitor",
            [new StartupFenceBackend(101, EpochMicroseconds(2))] =
                "startup-fence-gate",
            [new StartupFenceBackend(102, EpochMicroseconds(3))] =
                "startup-fence-killer"
        };

    private static StartupFenceExpectedReplicaBackend[] ExpectedReplicaBackends()
        =>
        [
            new(Actor, "migration",
                new StartupFenceBackend(201, EpochMicroseconds(1))),
            new($"sf_{RunId}_migration_b", "migration",
                new StartupFenceBackend(202, EpochMicroseconds(2)))
        ];

    private static NpgsqlConnectionStringBuilder ApprovedConnection()
        => StartupFencePostgresFixture.ValidateConfiguration(
            $"Host=127.0.0.1;Port=55479;Database=startup_fence_{RunId};" +
            "Username=startup_fence_owner",
            RunId, Cluster, out _);

    private sealed class RejectingAttestor : IStartupConnectionAttestor
    {
        internal ConnectionState? ValidatedState { get; private set; }
        internal bool OpenAttestationCalled { get; private set; }

        public void ValidateBeforeOpen(NpgsqlConnection connection)
        {
            ValidatedState = connection.State;
            throw new InvalidOperationException("guard rejected before open");
        }

        public Task AttestOpenAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            OpenAttestationCalled = true;
            throw new InvalidOperationException("open attestation must never be reached");
        }
    }
}
