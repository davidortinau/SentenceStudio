using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace SentenceStudio.Api.Tests.Startup;

public sealed class StartupFenceDockerAttestorTests(ITestOutputHelper output)
{
    private const string RunId = "0123456789ab4def8123456789abcdef";
    private const string Cluster = "sfence_" + RunId;
    private const string Volume = "sfence_pgdata_" + RunId;
    private static readonly string ContainerId = new('a', 64);
    private const string ImageId =
        "sha256:0027bef26712baaee437a4ea48fdf3d2d2e2bc5f0d81615374408ca320f3c7e3";

    [StartupFencePostgresFact]
    public async Task Docker_only_preflight_attests_the_exact_target_without_postgresql_sql()
    {
        var configuration = StartupFencePostgresFixture.RequireConfiguration(
            Environment.GetEnvironmentVariable(StartupFencePostgresFixture.EnabledVariable),
            Environment.GetEnvironmentVariable(StartupFencePostgresFixture.ConnectionVariable),
            Environment.GetEnvironmentVariable(StartupFencePostgresFixture.RunIdVariable),
            Environment.GetEnvironmentVariable(StartupFencePostgresFixture.ClusterVariable),
            Environment.GetEnvironmentVariable(StartupFencePostgresFixture.VolumeVariable),
            Environment.GetEnvironmentVariable(StartupFencePostgresFixture.ContainerVariable));

        var attested = await StartupFenceDockerAttestor.AttestAsync(configuration);

        output.WriteLine(
            $"UTC {DateTimeOffset.UtcNow:O}: Docker-only preflight " +
            $"container={attested.ContainerId} image={attested.ImageId} " +
            $"volume={attested.VolumeName} port={attested.HostPort} " +
            $"database={attested.DatabaseName} system_identifier={attested.SystemIdentifier}");
    }

    [Theory]
    [InlineData("\"ssh://example.invalid/docker\"", null)]
    [InlineData("\"tcp://127.0.0.1:2375\"", null)]
    [InlineData("\"unix:///var/run/docker.sock\"", "ssh://example.invalid/docker")]
    public void A_remote_Docker_daemon_is_not_a_scratch_cluster(
        string contextJson, string? dockerHost)
    {
        Action validate = () => StartupFenceDockerAttestor.ValidateLocalEndpoint(
            contextJson, dockerHost);

        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_missing_or_remote_daemon_pin_is_refused_before_Docker()
    {
        var pin = OfflinePin();
        Action withoutIdentity = () => StartupFenceDockerAttestor.RequirePin(
            pin.Host, null, pin.Home, pin.Config, pin.TemporaryDirectory);
        Action remoteEndpoint = () => StartupFenceDockerAttestor.RequirePin(
            "ssh://example.invalid/docker", pin.DaemonId, pin.Home, pin.Config,
            pin.TemporaryDirectory);

        withoutIdentity.Should().Throw<InvalidOperationException>()
            .WithMessage("*Refusing startup-fence SQL*");
        remoteEndpoint.Should().Throw<InvalidOperationException>()
            .WithMessage("*Refusing startup-fence SQL*");
    }

    [Fact]
    public async Task A_changed_daemon_is_refused_before_the_first_Docker_inspection_or_SQL()
    {
        var scenario = Fresh();
        var observedOperations = new List<string>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupFenceDockerAttestor.AttestAsync(
                scenario.Configuration, OfflinePin(),
                (_, command) =>
                {
                    observedOperations.Add(command[0]);
                    return Task.FromResult("different-daemon\n");
                },
                _ => Task.FromResult(true)));

        failure.Message.Should().Contain("Refusing startup-fence SQL");
        observedOperations.Should().Equal("info");
    }

    [Fact]
    public async Task A_changed_daemon_after_an_inspection_is_refused_before_the_next_read_or_SQL()
    {
        var scenario = Fresh();
        var observedOperations = new List<string>();
        var infoReads = 0;
        var pin = OfflinePin();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupFenceDockerAttestor.AttestAsync(
                scenario.Configuration, pin,
                (currentPin, command) =>
                {
                    observedOperations.Add(command[0]);
                    if (command[0] == "info" && ++infoReads == 4)
                    {
                        return Task.FromResult("different-daemon\n");
                    }
                    return scenario.ReadDockerAsync(currentPin, command);
                },
                _ => Task.FromResult(true)));

        failure.Message.Should().Contain("Refusing startup-fence SQL");
        observedOperations.Should().Equal("info", "context", "info", "info",
            "container", "info");
    }

    [Fact]
    public async Task A_changed_socket_is_refused_before_Docker_inspection_or_SQL()
    {
        var scenario = Fresh();
        var observedOperations = new List<string>();
        var socketChecks = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupFenceDockerAttestor.AttestAsync(
                scenario.Configuration, OfflinePin(),
                (pin, command) =>
                {
                    observedOperations.Add(command[0]);
                    return scenario.ReadDockerAsync(pin, command);
                },
                _ => Task.FromResult(++socketChecks == 1)));

        failure.Message.Should().Contain("Refusing startup-fence SQL");
        observedOperations.Should().Equal("info");
    }

    [Fact]
    public async Task A_daemon_rebound_after_metadata_is_refused_before_SQL()
    {
        var scenario = Fresh();
        var infoReads = 0;
        var metadataReads = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupFenceDockerAttestor.AttestAsync(
                scenario.Configuration, OfflinePin(),
                (pin, command) =>
                {
                    if (command[0] == "info" && ++infoReads == 25)
                    {
                        return Task.FromResult("different-daemon\n");
                    }
                    if (command[0] != "info")
                    {
                        metadataReads++;
                    }
                    return scenario.ReadDockerAsync(pin, command);
                },
                _ => Task.FromResult(true)));

        failure.Message.Should().Contain("Refusing startup-fence SQL");
        metadataReads.Should().Be(12);
        infoReads.Should().Be(25);
    }

    [Fact]
    public async Task An_unchanged_pinned_daemon_allows_complete_read_only_attestation()
    {
        var scenario = Fresh();
        var observedOperations = new List<string>();

        var attested = await StartupFenceDockerAttestor.AttestAsync(
            scenario.Configuration, OfflinePin(),
            (pin, command) =>
            {
                observedOperations.Add(command[0]);
                return scenario.ReadDockerAsync(pin, command);
            },
            _ => Task.FromResult(true));

        attested.ContainerId.Should().Be(ContainerId);
        attested.VolumeName.Should().Be(Volume);
        observedOperations.Should().ContainInOrder("info", "context", "info",
            "info", "container", "info");
        observedOperations.Last().Should().Be("info");
        observedOperations.Count(operation => operation == "info")
            .Should().Be(2 * observedOperations.Count(operation => operation != "info") + 1);
    }

    [Fact]
    public void Every_Docker_CLI_call_uses_the_pinned_host_and_no_scratch_or_user_secrets()
    {
        var pin = OfflinePin();

        var start = StartupFenceDockerAttestor.CreateDockerStartInfo(
            pin, "info", "--format", "{{.ID}}");

        start.FileName.Should().Be("/usr/local/bin/docker");
        start.ArgumentList.Should().Equal("--config", pin.Config, "--host", pin.Host,
            "info", "--format", "{{.ID}}");
        start.Environment.Keys.Should().BeEquivalentTo(
            ["PATH", "HOME", "TMPDIR", "DOCKER_CONFIG", "DOCKER_HOST", "LANG"]);
        start.Environment["HOME"].Should().Be(pin.Home);
        start.Environment["DOCKER_HOST"].Should().Be(pin.Host);
        start.Environment.Should().NotContainKey("STARTUP_FENCE_PG_TEST_CONNECTION");
        start.Environment.Should().NotContainKey("AZURE_CLIENT_SECRET");
        start.Environment.Should().NotContainKey(StartupFenceDockerAttestor.PinnedDaemonIdVariable);
    }

    [Fact]
    public void A_new_labeled_container_and_volume_can_be_attested_without_postgres()
    {
        var scenario = Fresh();

        var result = scenario.Validate();

        result.ContainerId.Should().Be(ContainerId);
        result.ImageId.Should().Be(ImageId);
        result.VolumeName.Should().Be(Volume);
        result.HostPort.Should().Be(55479);
        result.DatabaseName.Should().Be($"startup_fence_{RunId}");
        result.SystemIdentifier.Should().NotBeNullOrEmpty();
        result.VolumeCreatedAt.Should().BeAfter(scenario.Now.AddHours(-1));
    }

    [Fact]
    public void Docker_allocated_port_is_attested_from_the_actual_binding()
    {
        var scenario = Fresh();
        scenario.Container["PortBindings"]!["5432/tcp"]![0]!["HostPort"] = "";

        var result = scenario.Validate();

        result.HostPort.Should().Be(55479);
    }

    [Theory]
    [InlineData("reused-volume")]
    [InlineData("recreated-container")]
    [InlineData("restarted-container")]
    [InlineData("different-image")]
    [InlineData("different-image-version")]
    [InlineData("different-image-digest")]
    [InlineData("wrong-label")]
    [InlineData("wrong-mount")]
    [InlineData("extra-mount")]
    [InlineData("wide-port")]
    [InlineData("wrong-port")]
    [InlineData("second-consumer")]
    [InlineData("old-postgres-system")]
    [InlineData("stopped-postgres")]
    public void Docker_inspection_rejects_unproven_freshness_and_nonisolated_targets(
        string mutation)
    {
        var scenario = Fresh();
        switch (mutation)
        {
            case "reused-volume":
                scenario.Volume["CreatedAt"] = scenario.Now.AddHours(-2).ToString("O");
                break;
            case "recreated-container":
                scenario.Container["Created"] = scenario.Now.AddMinutes(-2).ToString("O");
                scenario.Container["StartedAt"] = scenario.Now.AddMinutes(-1).ToString("O");
                break;
            case "restarted-container":
                scenario.Container["RestartCount"] = 1;
                break;
            case "different-image":
                scenario.Container["Image"] = "sha256:" + new string('c', 64);
                break;
            case "different-image-version":
                scenario.Image["Env"]![0] = "PG_VERSION=17.9-1.pgdg13+1";
                break;
            case "different-image-digest":
                scenario.Image["RepoDigests"]![0] = "postgres@sha256:" + new string('c', 64);
                break;
            case "wrong-label":
                scenario.Volume["Labels"]!["com.sentencestudio.startup-fence.run-id"] =
                    Guid.NewGuid().ToString("N");
                break;
            case "wrong-mount":
                scenario.Container["Mounts"]![0]!["Destination"] = "/app/db";
                break;
            case "extra-mount":
                scenario.Container["Mounts"]!.AsArray().Add(
                    JsonNode.Parse("""{"Type":"bind","Source":"/host/data","Destination":"/app"}"""));
                break;
            case "wide-port":
                scenario.Container["Ports"]!["5432/tcp"]![0]!["HostIp"] = "0.0.0.0";
                break;
            case "wrong-port":
                scenario.Container["PortBindings"]!["5432/tcp"]![0]!["HostPort"] = "5432";
                break;
            case "second-consumer":
                scenario.Consumers += "\n" + new string('c', 64);
                break;
            case "old-postgres-system":
                scenario.ControlData = ControlData(scenario.Now.AddDays(-1));
                break;
            case "stopped-postgres":
                scenario.ControlData = scenario.ControlData.Replace(
                    "in production", "shut down", StringComparison.Ordinal);
                break;
        }

        Action attest = () => scenario.Validate();

        attest.Should().Throw<InvalidOperationException>()
            .WithMessage("*Refusing startup-fence SQL*");
    }

    private static Scenario Fresh()
    {
        var now = DateTimeOffset.UtcNow;
        var connection = StartupFencePostgresFixture.RequireConfiguration(
            "1", $"Host=127.0.0.1;Port=55479;Database=startup_fence_{RunId};" +
                 "Username=startup_fence_owner",
            RunId, Cluster, Volume, ContainerId);
        var labels = new Dictionary<string, string>
        {
            ["com.sentencestudio.startup-fence.run-id"] = RunId,
            ["com.sentencestudio.startup-fence.cluster-name"] = Cluster,
            ["com.sentencestudio.startup-fence.volume-name"] = Volume,
            ["com.sentencestudio.startup-fence.purpose"] = "isolated-fault-test"
        };
        var mountpoint = $"/var/lib/docker/volumes/{Volume}/_data";
        var port = new[] { new { HostIp = "127.0.0.1", HostPort = "55479" } };
        var container = new
        {
            Id = ContainerId, Name = "/" + Cluster,
            Created = now.AddMinutes(-7), Image = ImageId,
            ConfiguredImage = "postgres:17", Labels = labels,
            Mounts = new[]
            {
                new
                {
                    Type = "volume", Name = Volume, Driver = "local",
                    Source = mountpoint, Destination = StartupFenceDockerAttestor.PgData,
                    RW = true
                }
            },
            PortBindings = new Dictionary<string, object> { ["5432/tcp"] = port },
            Ports = new Dictionary<string, object> { ["5432/tcp"] = port },
            NetworkMode = "default", Running = true, Restarting = false,
            StartedAt = now.AddMinutes(-6), RestartCount = 0
        };
        var volume = new
        {
            Name = Volume, CreatedAt = now.AddMinutes(-8), Mountpoint = mountpoint,
            Driver = "local", Scope = "local",
            Options = new Dictionary<string, string>(), Labels = labels
        };

        return new Scenario(
            connection, now,
            JsonSerializer.SerializeToNode(container)!.AsObject(),
            JsonSerializer.SerializeToNode(volume)!.AsObject(),
            JsonSerializer.SerializeToNode(new
            {
                Id = ImageId, RepoTags = new[] { "postgres:17" },
                RepoDigests = new[] { "postgres@" + ImageId },
                Env = new[] { "PG_VERSION=17.10-1.pgdg13+1" }
            })!.AsObject(),
            ContainerId, ControlData(now.AddMinutes(-5)));
    }

    private static string ControlData(DateTimeOffset initialized)
    {
        var identifier = ((ulong)initialized.ToUnixTimeSeconds() << 32) | 12345678u;
        return $"Database system identifier: {identifier}\nDatabase cluster state: in production\n";
    }

    private static StartupFenceDockerPin OfflinePin()
        => StartupFenceDockerAttestor.RequirePin(
            "unix:///offline-mock.sock", "offline-daemon-A",
            "/offline/private/home", "/offline/private/docker-config", "/offline/private/tmp");

    private sealed class Scenario(
        StartupFencePostgresConfiguration configuration, DateTimeOffset now,
        JsonObject container, JsonObject volume, JsonObject image,
        string consumers, string controlData)
    {
        internal StartupFencePostgresConfiguration Configuration { get; } = configuration;
        internal DateTimeOffset Now { get; } = now;
        internal JsonObject Container { get; } = container;
        internal JsonObject Volume { get; } = volume;
        internal JsonObject Image { get; } = image;
        internal string Consumers { get; set; } = consumers;
        internal string ControlData { get; set; } = controlData;

        internal Task<string> ReadDockerAsync(StartupFenceDockerPin pin, string[] command)
            => Task.FromResult(command switch
            {
                ["info", "--format", "{{.ID}}"] => pin.DaemonId + "\n",
                ["context", "inspect", "--format", _] => JsonSerializer.Serialize(pin.Host),
                ["container", "inspect", "--format", _, _] => Container.ToJsonString(),
                ["volume", "inspect", "--format", _, _] => Volume.ToJsonString(),
                ["image", "inspect", "--format", _, "postgres:17"] => Image.ToJsonString(),
                ["container", "ls", ..] => Consumers,
                ["container", "exec", ..] => ControlData,
                _ => throw new InvalidOperationException("Unexpected mocked Docker command.")
            });

        internal StartupFenceDockerAttestation Validate()
            => StartupFenceDockerAttestor.ValidateMetadata(
                Configuration, Container.ToJsonString(), Volume.ToJsonString(),
                Image.ToJsonString(), Consumers, ControlData, Now);
    }
}
