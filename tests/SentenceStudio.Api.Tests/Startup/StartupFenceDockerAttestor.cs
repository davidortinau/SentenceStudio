using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace SentenceStudio.Api.Tests.Startup;

internal sealed record StartupFencePostgresConfiguration(
    NpgsqlConnectionStringBuilder Connection, Guid RunId, string ClusterName,
    string VolumeName, string ContainerId);

internal sealed record StartupFenceDockerAttestation(
    string ContainerId, string SystemIdentifier, DateTimeOffset ClusterInitializedAt,
    DateTimeOffset VolumeCreatedAt, DateTimeOffset ContainerStartedAt,
    string ImageId, string VolumeName, int HostPort, string DatabaseName);

internal sealed record StartupFenceDockerPin(
    string Host, string DaemonId, string Home, string Config, string TemporaryDirectory)
{
    internal string SocketPath => Host["unix://".Length..];
}

/// <summary>
/// Uses only read-only Docker commands. No connection to PostgreSQL is made until the
/// pinned local Docker daemon, exact container, image, named PGDATA volume, and bootstrap
/// identifier pass inspection. Socket resolution and daemon ID bracket every Docker read
/// and are checked again before returning; observable drift refuses SQL. Separate CLI
/// processes cannot atomically rule out a switch-and-switch-back or a spoofed daemon ID.
/// Docker templates project only non-secret fields.
/// </summary>
internal static class StartupFenceDockerAttestor
{
    internal const string PgData = "/var/lib/postgresql/data";
    internal const string PinnedDaemonIdVariable = "STARTUP_FENCE_DOCKER_DAEMON_ID";
    private const string DockerCli = "/usr/local/bin/docker";
    private const string SocketChecker = "/Library/Frameworks/Python.framework/Versions/3.13/bin/python3";
    private const string TrustedPath =
        "/usr/local/share/dotnet:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
    private const string SocketCheck = """
        import os, stat, sys
        path = sys.argv[1]
        try:
            valid = (os.path.isabs(path) and os.path.realpath(path) == path
                     and stat.S_ISSOCK(os.stat(path).st_mode))
        except OSError:
            valid = False
        sys.exit(0 if valid else 1)
        """;
    private const string RunLabel = "com.sentencestudio.startup-fence.run-id";
    private const string ClusterLabel = "com.sentencestudio.startup-fence.cluster-name";
    private const string VolumeLabel = "com.sentencestudio.startup-fence.volume-name";
    private const string PurposeLabel = "com.sentencestudio.startup-fence.purpose";
    private const string Purpose = "isolated-fault-test";
    // The non-public catalog allowlist was reviewed against this exact PG 17.10 image.
    private const string ReviewedImageId =
        "sha256:0027bef26712baaee437a4ea48fdf3d2d2e2bc5f0d81615374408ca320f3c7e3";
    private const string ReviewedImageVersion = "PG_VERSION=17.10-1.pgdg13+1";
    private static readonly TimeSpan MaxSetupAge = TimeSpan.FromHours(1);

    private const string ContainerFormat = """
        {"Id":{{json .Id}},"Name":{{json .Name}},"Created":{{json .Created}},"Image":{{json .Image}},"ConfiguredImage":{{json .Config.Image}},"Labels":{{json .Config.Labels}},"Mounts":{{json .Mounts}},"PortBindings":{{json .HostConfig.PortBindings}},"Ports":{{json .NetworkSettings.Ports}},"NetworkMode":{{json .HostConfig.NetworkMode}},"Running":{{json .State.Running}},"Restarting":{{json .State.Restarting}},"StartedAt":{{json .State.StartedAt}},"RestartCount":{{json .RestartCount}}}
        """;
    private const string VolumeFormat = """
        {"Name":{{json .Name}},"CreatedAt":{{json .CreatedAt}},"Mountpoint":{{json .Mountpoint}},"Driver":{{json .Driver}},"Scope":{{json .Scope}},"Options":{{json .Options}},"Labels":{{json .Labels}}}
        """;
    private const string ImageFormat = """
        {"Id":{{json .Id}},"RepoTags":{{json .RepoTags}},"RepoDigests":{{json .RepoDigests}},"Env":{{json .Config.Env}}}
        """;

    internal static Task<StartupFenceDockerAttestation> AttestAsync(
        StartupFencePostgresConfiguration configuration)
        => AttestAsync(configuration, PinFromEnvironment(),
            ReadOnlyDockerAsync, IsLocalResolvedSocketAsync);

    internal static async Task<StartupFenceDockerAttestation> AttestAsync(
        StartupFencePostgresConfiguration configuration, StartupFenceDockerPin pin,
        Func<StartupFenceDockerPin, string[], Task<string>> readDocker,
        Func<StartupFenceDockerPin, Task<bool>> checkSocket)
    {
        pin = RequirePin(pin.Host, pin.DaemonId, pin.Home, pin.Config,
            pin.TemporaryDirectory);

        async Task CheckPinAsync()
        {
            if (!await checkSocket(pin))
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence SQL: pinned Docker Unix socket changed.");
            }
            string observed;
            try
            {
                observed = await readDocker(pin, ["info", "--format", "{{.ID}}"]);
            }
            finally
            {
                if (!await checkSocket(pin))
                {
                    throw new InvalidOperationException(
                        "Refusing startup-fence SQL: pinned Docker Unix socket changed.");
                }
            }
            if (observed.TrimEnd('\r', '\n') != pin.DaemonId)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence SQL: pinned Docker daemon or Unix socket changed.");
            }
        }

        async Task<string> ReadPinnedAsync(params string[] arguments)
        {
            await CheckPinAsync();
            try
            {
                return await readDocker(pin, arguments);
            }
            finally
            {
                await CheckPinAsync();
            }
        }

        var context = await ReadPinnedAsync(
            "context", "inspect", "--format", "{{json .Endpoints.docker.Host}}");
        ValidateLocalEndpoint(context, pin.Host);

        var container = await ReadPinnedAsync(
            "container", "inspect", "--format", ContainerFormat, configuration.ContainerId);
        var volume = await ReadPinnedAsync(
            "volume", "inspect", "--format", VolumeFormat, configuration.VolumeName);
        var image = await ReadPinnedAsync(
            "image", "inspect", "--format", ImageFormat, "postgres:17");
        var consumers = await ReadPinnedAsync(
            "container", "ls", "--all", "--quiet", "--no-trunc",
            "--filter", $"volume={configuration.VolumeName}");
        var control = await ReadPinnedAsync(
            "container", "exec", "--user", "postgres", configuration.ContainerId,
            "pg_controldata", "-D", PgData);
        if (container != await ReadPinnedAsync(
                "container", "inspect", "--format", ContainerFormat, configuration.ContainerId) ||
            volume != await ReadPinnedAsync(
                "volume", "inspect", "--format", VolumeFormat, configuration.VolumeName) ||
            image != await ReadPinnedAsync(
                "image", "inspect", "--format", ImageFormat, "postgres:17") ||
            consumers != await ReadPinnedAsync(
                "container", "ls", "--all", "--quiet", "--no-trunc",
                "--filter", $"volume={configuration.VolumeName}") ||
            control != await ReadPinnedAsync(
                "container", "exec", "--user", "postgres", configuration.ContainerId,
                "pg_controldata", "-D", PgData) ||
            context != await ReadPinnedAsync(
                "context", "inspect", "--format", "{{json .Endpoints.docker.Host}}"))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence SQL: Docker container, image, PGDATA, volume, or context " +
                "changed during read-only attestation.");
        }

        var attested = ValidateMetadata(
            configuration, container, volume, image, consumers, control, DateTimeOffset.UtcNow);
        await CheckPinAsync();
        return attested;
    }

    internal static StartupFenceDockerPin RequirePin(
        string? host, string? daemonId, string? home, string? config, string? temporaryDirectory)
    {
        static bool CanonicalAbsolute(string? path)
            => !string.IsNullOrEmpty(path) &&
                !path.Contains('\0') && !path.Contains('\n') && !path.Contains('\r') &&
                Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path;

        if (host is null || !host.StartsWith("unix:///", StringComparison.Ordinal) ||
            !CanonicalAbsolute(host["unix://".Length..]) ||
            daemonId is null || !Regex.IsMatch(daemonId, @"\A[A-Za-z0-9:_-]{1,128}\z") ||
            !CanonicalAbsolute(home) || !CanonicalAbsolute(config) ||
            !CanonicalAbsolute(temporaryDirectory) ||
            Path.GetFileName(home) != "home" ||
            Path.GetFileName(Path.GetDirectoryName(home)) != "private" ||
            config != Path.Combine(Path.GetDirectoryName(home)!, "docker-config") ||
            temporaryDirectory != Path.Combine(Path.GetDirectoryName(home)!, "tmp"))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence SQL: pinned Docker identity or private CLI paths are invalid.");
        }
        return new StartupFenceDockerPin(host, daemonId, home!, config!, temporaryDirectory!);
    }

    private static StartupFenceDockerPin PinFromEnvironment()
    {
        foreach (var name in new[] {
            "DOCKER_CONTEXT", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH",
            "DOCKER_API_VERSION", "DOCKER_CUSTOM_HEADERS"
        })
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence SQL: ambient Docker overrides are forbidden.");
            }
        }
        return RequirePin(
            Environment.GetEnvironmentVariable("DOCKER_HOST"),
            Environment.GetEnvironmentVariable(PinnedDaemonIdVariable),
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetEnvironmentVariable("DOCKER_CONFIG"),
            Environment.GetEnvironmentVariable("TMPDIR"));
    }

    internal static void ValidateLocalEndpoint(string contextJson, string? dockerHost)
    {
        string? endpoint;
        try
        {
            endpoint = JsonSerializer.Deserialize<string>(contextJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence SQL: Docker context endpoint is not trustworthy.");
        }
        if (endpoint is null || !endpoint.StartsWith("unix:///", StringComparison.Ordinal) ||
            dockerHost is null || !dockerHost.StartsWith("unix:///", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence SQL: Docker must use a local Unix-socket context.");
        }
    }

    internal static StartupFenceDockerAttestation ValidateMetadata(
        StartupFencePostgresConfiguration configuration, string containerJson,
        string volumeJson, string imageJson, string consumers, string controlData,
        DateTimeOffset now)
    {
        try
        {
            using var containerDoc = JsonDocument.Parse(containerJson);
            using var volumeDoc = JsonDocument.Parse(volumeJson);
            using var imageDoc = JsonDocument.Parse(imageJson);
            var container = containerDoc.RootElement;
            var volume = volumeDoc.RootElement;
            var image = imageDoc.RootElement;
            var id = GetString(container, "Id");

            Require(id == configuration.ContainerId &&
                    GetString(container, "Name") == $"/{configuration.ClusterName}" &&
                    GetString(container, "ConfiguredImage") == "postgres:17" &&
                    container.GetProperty("Running").GetBoolean() &&
                    !container.GetProperty("Restarting").GetBoolean() &&
                    container.GetProperty("RestartCount").GetInt32() == 0,
                "the exact unrestarted PostgreSQL 17 container must be running");
            Require(
                GetString(container, "Image") == GetString(image, "Id") &&
                GetString(image, "Id") == ReviewedImageId &&
                image.GetProperty("RepoTags").EnumerateArray()
                    .Any(tag => tag.GetString() == "postgres:17") &&
                image.GetProperty("RepoDigests").EnumerateArray()
                    .Any(digest => digest.GetString() == "postgres@" + ReviewedImageId) &&
                image.GetProperty("Env").EnumerateArray()
                    .Any(variable => variable.GetString() == ReviewedImageVersion),
                "the running image must match the reviewed PostgreSQL 17.10 image ID, digest, and version");
            VerifyLabels(container.GetProperty("Labels"), configuration);
            Require(GetString(container, "NetworkMode") is "default" or "bridge",
                "the container must use Docker bridge networking");
            VerifyPort(container.GetProperty("PortBindings"), configuration.Connection.Port,
                allowDynamicAllocation: true);
            VerifyPort(container.GetProperty("Ports"), configuration.Connection.Port);

            Require(GetString(volume, "Name") == configuration.VolumeName &&
                    GetString(volume, "Driver") == "local" &&
                    GetString(volume, "Scope") == "local" &&
                    (volume.GetProperty("Options").ValueKind is JsonValueKind.Null ||
                     volume.GetProperty("Options").EnumerateObject().Count() == 0),
                "the exact volume must be a local, unmodified named volume");
            VerifyLabels(volume.GetProperty("Labels"), configuration);
            var mountpoint = GetString(volume, "Mountpoint");
            Require(mountpoint.EndsWith($"/{configuration.VolumeName}/_data",
                        StringComparison.Ordinal),
                "the volume mountpoint does not match the dedicated Docker volume");

            var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
            Require(mounts.Length == 1 &&
                    GetString(mounts[0], "Type") == "volume" &&
                    GetString(mounts[0], "Name") == configuration.VolumeName &&
                    GetString(mounts[0], "Driver") == "local" &&
                    GetString(mounts[0], "Source") == mountpoint &&
                    GetString(mounts[0], "Destination") == PgData &&
                    mounts[0].GetProperty("RW").GetBoolean(),
                "only the exact fresh named volume may back PostgreSQL PGDATA");
            Require(consumers.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries) is [{ } consumer] &&
                    consumer == configuration.ContainerId,
                "another container has used or is using the named PGDATA volume");

            var volumeCreated = ReadTime(volume, "CreatedAt");
            var containerCreated = ReadTime(container, "Created");
            var started = ReadTime(container, "StartedAt");
            Require(volumeCreated >= now - MaxSetupAge &&
                    volumeCreated <= now.AddSeconds(2) &&
                    containerCreated >= volumeCreated &&
                    started >= containerCreated &&
                    started <= now.AddSeconds(2) &&
                    started - containerCreated <= TimeSpan.FromMinutes(10),
                "volume and container must have been newly created, in order, for this run");

            var matches = Regex.Matches(controlData,
                @"(?m)^Database system identifier:[ \t]*(?<id>[0-9]+)[ \t]*\r?$");
            ulong systemId = 0;
            Require(matches.Count == 1 &&
                    ulong.TryParse(matches[0].Groups["id"].Value,
                        NumberStyles.None, CultureInfo.InvariantCulture, out systemId),
                "pg_controldata did not identify exactly one PostgreSQL data directory");
            var initialized = DateTimeOffset.FromUnixTimeSeconds(checked((long)(systemId >> 32)));
            Require(initialized >= started.AddSeconds(-2) &&
                    initialized <= started.AddMinutes(10) &&
                    initialized <= now.AddSeconds(2),
                "the PostgreSQL system identifier predates this container's first start");
            Require(Regex.IsMatch(controlData,
                    @"(?m)^Database cluster state:[ \t]*in production[ \t]*\r?$"),
                "the inspected PostgreSQL data directory is not running");

            return new StartupFenceDockerAttestation(
                id, systemId.ToString(CultureInfo.InvariantCulture), initialized,
                volumeCreated, started, GetString(image, "Id"),
                configuration.VolumeName, configuration.Connection.Port,
                configuration.Connection.Database);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
                                   FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence SQL: Docker inspection was incomplete or malformed.", ex);
        }
    }

    private static void VerifyLabels(
        JsonElement labels, StartupFencePostgresConfiguration configuration)
    {
        Require(labels.ValueKind == JsonValueKind.Object &&
                labels.EnumerateObject().Count() == 4 &&
                GetString(labels, RunLabel) == configuration.RunId.ToString("N") &&
                GetString(labels, ClusterLabel) == configuration.ClusterName &&
                GetString(labels, VolumeLabel) == configuration.VolumeName &&
                GetString(labels, PurposeLabel) == Purpose,
            "container and volume labels must independently identify this one run");
    }

    private static void VerifyPort(
        JsonElement ports, int port, bool allowDynamicAllocation = false)
    {
        var expectedPort = port.ToString(CultureInfo.InvariantCulture);
        Require(ports.ValueKind == JsonValueKind.Object &&
                ports.EnumerateObject().Count() == 1 &&
                ports.TryGetProperty("5432/tcp", out var bindings) &&
                bindings.ValueKind == JsonValueKind.Array &&
                bindings.GetArrayLength() == 1 &&
                GetString(bindings[0], "HostIp") == "127.0.0.1" &&
                (GetString(bindings[0], "HostPort") == expectedPort ||
                 (allowDynamicAllocation && GetString(bindings[0], "HostPort") == "")),
            "PostgreSQL must publish only 5432/tcp to the selected nondefault loopback port");
    }

    private static string GetString(JsonElement element, string property)
        => element.GetProperty(property).GetString()
            ?? throw new InvalidOperationException("Missing required Docker metadata.");

    private static DateTimeOffset ReadTime(JsonElement element, string property)
        => DateTimeOffset.Parse(GetString(element, property),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Refusing startup-fence SQL: {reason}.");
        }
    }

    internal static ProcessStartInfo CreateDockerStartInfo(
        StartupFenceDockerPin pin, params string[] arguments)
    {
        var start = new ProcessStartInfo(DockerCli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.Environment["PATH"] = TrustedPath;
        start.Environment["HOME"] = pin.Home;
        start.Environment["TMPDIR"] = pin.TemporaryDirectory;
        start.Environment["DOCKER_CONFIG"] = pin.Config;
        start.Environment["DOCKER_HOST"] = pin.Host;
        start.Environment["LANG"] = "en_US.UTF-8";
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(pin.Config);
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(pin.Host);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static async Task<bool> IsLocalResolvedSocketAsync(StartupFenceDockerPin pin)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(SocketChecker)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = TrustedPath;
        process.StartInfo.Environment["HOME"] = pin.Home;
        process.StartInfo.Environment["TMPDIR"] = pin.TemporaryDirectory;
        foreach (var argument in new[] { "-I", "-B", "-c", SocketCheck, pin.SocketPath })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
            return false;
        }
    }

    private static async Task<string> ReadOnlyDockerAsync(
        StartupFenceDockerPin pin, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = CreateDockerStartInfo(pin, arguments)
        };

        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                }
                throw new InvalidOperationException(
                    "Refusing startup-fence SQL: read-only Docker inspection timed out.");
            }

            await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing startup-fence SQL: read-only Docker inspection failed " +
                    $"(exit {process.ExitCode}); Docker stderr withheld.");
            }
            return await output;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence SQL: Docker CLI is unavailable.");
        }
    }
}
