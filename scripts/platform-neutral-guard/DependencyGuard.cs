using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Versioning;

public sealed class DependencyGuardLogger : ILogger
{
    private const string Marker = "SentenceStudio.DependencyGuard:";
    private readonly object _writeLock = new();
    private StreamWriter? _writer;

    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;
    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        if (string.IsNullOrWhiteSpace(Parameters))
            throw new InvalidOperationException("Dependency guard trace path is required.");
        if (eventSource is not IEventSource4 evaluations)
            throw new InvalidOperationException("MSBuild must expose evaluated project properties.");
        evaluations.IncludeEvaluationPropertiesAndItems();

        _writer = new StreamWriter(new FileStream(
            Parameters, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        eventSource.AnyEventRaised += (_, e) =>
        {
            if (e is ProjectEvaluationFinishedEventArgs finished)
                Write(new
                {
                    kind = "evaluation",
                    evaluation = finished.BuildEventContext?.EvaluationId,
                    project = finished.ProjectFile,
                    properties = ReadProperties(finished.Properties)
                        .Where(property => EvaluatedPropertyNames.Contains(property.Name))
                        .ToDictionary(property => property.Name, property => property.Value,
                            StringComparer.OrdinalIgnoreCase),
                });
            else if (e is TaskParameterEventArgs parameter &&
                     parameter.Kind == TaskParameterMessageKind.TaskInput &&
                     string.Equals(parameter.ParameterName, "ProjectAssetsFile",
                         StringComparison.OrdinalIgnoreCase))
            {
                var values = parameter.Items?.Cast<object>()
                    .Select(item => item is ITaskItem taskItem ? taskItem.ItemSpec
                        : item as string).ToArray();
                string? snapshot = null;
                if (values is [var file] && file is not null &&
                    Path.IsPathFullyQualified(file))
                {
                    var entryDir = Path.GetDirectoryName(Parameters)!;
                    var resolved = Path.GetFullPath(file);
                    if (resolved.StartsWith(entryDir + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal) && File.Exists(resolved))
                    {
                        Directory.CreateDirectory(Path.Combine(entryDir, "snapshots"));
                        snapshot = Path.Combine(entryDir, "snapshots", $"{Guid.NewGuid():N}.json");
                        File.Copy(resolved, snapshot);
                    }
                }
                Write(new
                {
                    kind = "asset-input",
                    context = Context(parameter.BuildEventContext),
                    task = parameter.BuildEventContext?.TaskId,
                    values, snapshot,
                });
            }
            else if (e is ProjectImportedEventArgs imported &&
                     imported.ImportedProjectFile?.EndsWith("Observe.targets", StringComparison.Ordinal) == true)
                Write(new
                {
                    kind = "import",
                    evaluation = imported.BuildEventContext?.EvaluationId,
                    path = imported.ImportedProjectFile,
                    ignored = imported.ImportIgnored,
                });
        };
        eventSource.ProjectStarted += (_, e) => Write(new
        {
            kind = "start",
            context = Context(e.BuildEventContext),
            evaluation = e.BuildEventContext?.EvaluationId,
            instance = e.BuildEventContext?.ProjectInstanceId,
            project = e.ProjectFile,
            targets = e.TargetNames,
            globals = e.GlobalProperties?.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        });
        eventSource.ProjectFinished += (_, e) => Write(new
        {
            kind = "finish",
            context = Context(e.BuildEventContext),
            succeeded = e.Succeeded,
        });
        eventSource.TargetStarted += (_, e) =>
        {
            if (e.TargetName is "Build" or "ResolvePackageAssets")
                Write(new
                {
                    kind = "target",
                    context = Context(e.BuildEventContext),
                    name = e.TargetName,
                });
        };
        eventSource.TaskStarted += (_, e) =>
        {
            if (e.TaskName == "ResolvePackageAssets")
                Write(new
                {
                    kind = "asset-task",
                    context = Context(e.BuildEventContext),
                    task = e.BuildEventContext?.TaskId,
                    file = e.TaskFile,
                });
        };
        eventSource.MessageRaised += (_, e) =>
        {
            if (e.Message?.StartsWith(Marker, StringComparison.Ordinal) == true)
                Write(new
                {
                    kind = "record",
                    context = Context(e.BuildEventContext),
                    evaluation = e.BuildEventContext?.EvaluationId,
                    instance = e.BuildEventContext?.ProjectInstanceId,
                    data = e.Message[Marker.Length..],
                });
        };
        eventSource.WarningRaised += (_, e) => WriteDiagnostic("warning", e);
        eventSource.ErrorRaised += (_, e) => WriteDiagnostic("error", e);
        eventSource.BuildFinished += (_, e) => Write(new { kind = "build-finished", succeeded = e.Succeeded });
    }

    private static string Context(BuildEventContext? context) =>
        context is null ? "" : $"{context.NodeId}:{context.ProjectContextId}";

    private static readonly HashSet<string> EvaluatedPropertyNames = new(
        [
            "ProjectAssetsFile", "MSBuildProjectExtensionsPath", "BaseIntermediateOutputPath",
            "RestoreOutputPath", "RestorePackagesPath", "NuGetPackageRoot",
            "RestoreFallbackFolders", "RestoreAdditionalProjectFallbackFolders",
            "RestoreAdditionalProjectSources", "TargetFramework", "TargetFrameworks",
            "RuntimeIdentifier", "Configuration", "Platform", "SelfContained",
            "CustomAfterMicrosoftCommonTargets", "CustomAfterMicrosoftCommonCrossTargetingTargets",
        ], StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string Name, string Value)> ReadProperties(IEnumerable? properties)
    {
        if (properties is null)
            yield break;
        foreach (var property in properties)
        {
            switch (property)
            {
                case ProjectPropertyInstance value:
                    yield return (value.Name, value.EvaluatedValue);
                    break;
                case DictionaryEntry { Key: string name, Value: string value }:
                    yield return (name, value);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"MSBuild returned an unsupported evaluated property type: {property?.GetType()}");
            }
        }
    }

    private void WriteDiagnostic(string kind, BuildWarningEventArgs e) =>
        Write(new { kind, code = e.Code, message = e.Message });

    private void WriteDiagnostic(string kind, BuildErrorEventArgs e) =>
        Write(new { kind, code = e.Code, message = e.Message });

    private void Write<T>(T value)
    {
        lock (_writeLock)
        {
            _writer!.WriteLine(JsonSerializer.Serialize(value));
            _writer.Flush();
        }
    }

    public void Shutdown()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal static string RecordMarker => Marker;
}

public sealed class DependencyGuardRecord : Microsoft.Build.Utilities.Task
{
    [Required] public string Phase { get; set; } = "";
    [Required] public string Token { get; set; } = "";
    [Required] public string Project { get; set; } = "";
    [Required] public string Scratch { get; set; } = "";
    public string AssetsFile { get; set; } = "";
    public string BaseIntermediateOutputPath { get; set; } = "";
    public string MSBuildProjectExtensionsPath { get; set; } = "";
    public string RestoreOutputPath { get; set; } = "";
    public string BaseOutputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string OutDir { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string IntermediateOutputPath { get; set; } = "";
    public string RestorePackagesPath { get; set; } = "";
    public string NuGetPackageRoot { get; set; } = "";
    public string RestoreConfigFile { get; set; } = "";
    public string RestoreFallbackFolders { get; set; } = "";
    public string RestoreAdditionalProjectFallbackFolders { get; set; } = "";
    public string RestoreAdditionalProjectSources { get; set; } = "";
    public string MSBuildToolsPath { get; set; } = "";
    public string RestoreForce { get; set; } = "";
    public string RestoreNoCache { get; set; } = "";
    public string RestoreRecursive { get; set; } = "";
    public string BuildProjectReferences { get; set; } = "";
    public string NoWarn { get; set; } = "";
    public string TargetFramework { get; set; } = "";
    public string TargetFrameworks { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public string SelfContained { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string Platform { get; set; } = "";
    public string IsCrossTargetingBuild { get; set; } = "";
    public ITaskItem[] PackageReferences { get; set; } = [];
    public ITaskItem[] PackageDownloads { get; set; } = [];
    public ITaskItem[] ProjectReferences { get; set; } = [];

    public override bool Execute()
    {
        if (HasSuppressedNuGetWarning(NoWarn))
        {
            Log.LogError($"Dependency guard: {Project} suppresses NU1603/NU1605 in NoWarn.");
            return false;
        }
        foreach (var (kind, items) in new[]
        {
            ("PackageReference", PackageReferences),
            ("PackageDownload", PackageDownloads),
            ("ProjectReference", ProjectReferences),
        })
        {
            foreach (var item in items)
            {
                if (HasSuppressedNuGetWarning(item.GetMetadata("NoWarn")))
                {
                    Log.LogError(
                        $"Dependency guard: {Project} suppresses NU1603/NU1605 on " +
                        $"{kind} {item.ItemSpec}."
                    );
                    return false;
                }
            }
        }
        foreach (var download in PackageDownloads)
        {
            if (IsForbiddenPackage(download.ItemSpec))
            {
                Log.LogError(
                    $"Dependency guard: {Project} requests forbidden PackageDownload "
                    + download.ItemSpec
                );
                return false;
            }
        }
        if (!Path.IsPathFullyQualified(Scratch))
        {
            Log.LogError($"Dependency guard: {Project} has no isolated scratch directory.");
            return false;
        }
        var projectDirectory = Path.GetDirectoryName(Project)!;
        var sharedScratch = Directory.GetParent(Path.GetFullPath(Scratch))!.FullName;
        foreach (var (name, value, expected) in new[]
        {
            (nameof(RestorePackagesPath), RestorePackagesPath,
                Path.Combine(sharedScratch, "packages")),
            (nameof(NuGetPackageRoot), NuGetPackageRoot,
                Path.Combine(sharedScratch, "packages")),
            (nameof(RestoreConfigFile), RestoreConfigFile,
                Path.Combine(sharedScratch, "nuget.guard.config")),
        })
        {
            if (!Path.IsPathFullyQualified(value) ||
                !string.Equals(Path.GetFullPath(value), expected, StringComparison.Ordinal))
            {
                Log.LogError($"Dependency guard: {Project} has unsafe {name}={value}");
                return false;
            }
        }
        if (!string.Equals(RestoreForce, "true", StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError($"Dependency guard: {Project} removed RestoreForce=true.");
            return false;
        }
        if (!string.Equals(RestoreNoCache, "true", StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError($"Dependency guard: {Project} removed RestoreNoCache=true.");
            return false;
        }
        foreach (var (name, value) in new[]
        {
            (nameof(AssetsFile), AssetsFile),
            (nameof(BaseIntermediateOutputPath), BaseIntermediateOutputPath),
            (nameof(MSBuildProjectExtensionsPath), MSBuildProjectExtensionsPath),
            (nameof(RestoreOutputPath), RestoreOutputPath),
            (nameof(BaseOutputPath), BaseOutputPath),
            (nameof(OutputPath), OutputPath),
            (nameof(OutDir), OutDir),
            (nameof(TargetDir), TargetDir),
            (nameof(IntermediateOutputPath), IntermediateOutputPath),
        })
        {
            if (value.Length == 0)
                continue;
            var resolved = Path.GetFullPath(value, projectDirectory);
            if (!Path.IsPathFullyQualified(Scratch) ||
                !resolved.StartsWith(Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                Log.LogError($"Dependency guard: {Project} has unsafe {name}={value}");
                return false;
            }
        }

        foreach (var (name, value) in new[]
        {
            (nameof(RestoreFallbackFolders), RestoreFallbackFolders),
            (nameof(RestoreAdditionalProjectFallbackFolders), RestoreAdditionalProjectFallbackFolders),
        })
        {
            if (value.Length != 0)
            {
                Log.LogError($"Dependency guard: {Project} has unsafe {name}={value}");
                return false;
            }
        }

        var sdkPacks = Path.GetFullPath(Path.Combine(MSBuildToolsPath, "..", "..", "library-packs"));
        foreach (var source in RestoreAdditionalProjectSources.Split(
            ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(sdkPacks) ||
                !string.Equals(Path.GetFullPath(source, projectDirectory), sdkPacks,
                    StringComparison.Ordinal))
            {
                Log.LogError($"Dependency guard: {Project} has unsafe RestoreAdditionalProjectSources={source}");
                return false;
            }
        }

        string? snapshot = null;
        if (Phase == "assets" && File.Exists(AssetsFile))
        {
            if (!VerifyRequestedPackageRanges(AssetsFile, TargetFramework, RuntimeIdentifier))
                return false;
            Directory.CreateDirectory(Path.Combine(Scratch, "snapshots"));
            snapshot = Path.Combine(Scratch, "snapshots", $"{Guid.NewGuid():N}.json");
            File.Copy(AssetsFile, snapshot);
        }

        var record = new
        {
            phase = Phase, token = Token, project = Project, scratch = Scratch,
            assetsFile = AssetsFile, snapshot,
            baseIntermediateOutputPath = BaseIntermediateOutputPath,
            msbuildProjectExtensionsPath = MSBuildProjectExtensionsPath,
            restoreOutputPath = RestoreOutputPath, baseOutputPath = BaseOutputPath,
            outputPath = OutputPath, outDir = OutDir, targetDir = TargetDir,
            intermediateOutputPath = IntermediateOutputPath,
            restorePackagesPath = RestorePackagesPath, nugetPackageRoot = NuGetPackageRoot,
            restoreConfigFile = RestoreConfigFile,
            restoreFallbackFolders = RestoreFallbackFolders,
            restoreAdditionalProjectFallbackFolders = RestoreAdditionalProjectFallbackFolders,
            restoreAdditionalProjectSources = RestoreAdditionalProjectSources,
            msbuildToolsPath = MSBuildToolsPath,
            restoreForce = RestoreForce, restoreNoCache = RestoreNoCache,
            restoreRecursive = RestoreRecursive,
            buildProjectReferences = BuildProjectReferences, noWarn = NoWarn,
            cacheEnvironment = new Dictionary<string, string?>
            {
                ["NUGET_PACKAGES"] = Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
                ["NUGET_HTTP_CACHE_PATH"] = Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH"),
                ["NUGET_PLUGINS_CACHE_PATH"] = Environment.GetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH"),
                ["NUGET_SCRATCH"] = Environment.GetEnvironmentVariable("NUGET_SCRATCH"),
                ["TMPDIR"] = Environment.GetEnvironmentVariable("TMPDIR"),
                ["DOTNET_CLI_HOME"] = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME"),
                ["DOTNET_SHARED_MEMORY_APPLICATION_GROUP_ID"] =
                    Environment.GetEnvironmentVariable("DOTNET_SHARED_MEMORY_APPLICATION_GROUP_ID"),
            },
            targetFramework = TargetFramework, targetFrameworks = TargetFrameworks,
            runtimeIdentifier = RuntimeIdentifier, selfContained = SelfContained,
            configuration = Configuration, platform = Platform,
            isCrossTargetingBuild = IsCrossTargetingBuild,
            packageReferences = PackageReferences.Select(item => new
            {
                id = item.ItemSpec,
                version = item.GetMetadata("Version"),
                versionOverride = item.GetMetadata("VersionOverride"),
                noWarn = item.GetMetadata("NoWarn"),
                includeAssets = item.GetMetadata("IncludeAssets"),
                excludeAssets = item.GetMetadata("ExcludeAssets"),
                privateAssets = item.GetMetadata("PrivateAssets"),
            }).ToArray(),
            packageDownloads = PackageDownloads.Select(item => new
            {
                id = item.ItemSpec,
                version = item.GetMetadata("Version"),
                noWarn = item.GetMetadata("NoWarn"),
            }).ToArray(),
            projectReferences = ProjectReferences.Select(item => new
            {
                id = item.ItemSpec,
                noWarn = item.GetMetadata("NoWarn"),
            }).ToArray(),
        };
        BuildEngine.LogMessageEvent(new BuildMessageEventArgs(
            DependencyGuardLogger.RecordMarker + JsonSerializer.Serialize(record),
            null, nameof(DependencyGuardRecord), MessageImportance.High));
        return true;
    }

    private static bool HasSuppressedNuGetWarning(string value) =>
        value.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(code => code.Equals("NU1603", StringComparison.OrdinalIgnoreCase) ||
                         code.Equals("NU1605", StringComparison.OrdinalIgnoreCase));

    private static bool IsForbiddenPackage(string id) =>
        Regex.IsMatch(id, @"(^|[._-])(maui|comet)([._-]|$)|^mauireactor",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private bool VerifyRequestedPackageRanges(string file, string framework, string rid)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(file));
        if (!assets.RootElement.TryGetProperty("targets", out var targets))
        {
            Log.LogError($"Dependency guard: {Project} has no NuGet target graph for range checks.");
            return false;
        }
        var selected = rid.Length == 0 ? framework : $"{framework}/{rid}";
        foreach (var target in targets.EnumerateObject())
        {
            if (framework.Length != 0 && !target.Name.Equals(selected, StringComparison.Ordinal))
                continue;
            var resolved = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            var projectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var type))
                    continue;
                var parts = library.Name.Split('/', 2);
                if (type.GetString()?.Equals("project", StringComparison.OrdinalIgnoreCase) == true)
                {
                    projectIds.Add(parts[0]);
                    continue;
                }
                if (!type.GetString()!.Equals("package", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (parts.Length == 2 && NuGetVersion.TryParse(parts[1], out var version))
                    resolved[parts[0]] = version;
            }
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("dependencies", out var dependencies))
                    continue;
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (projectIds.Contains(dependency.Name))
                        continue;
                    var requested = dependency.Value.ValueKind == JsonValueKind.String
                        ? dependency.Value.GetString()
                        : dependency.Value.ValueKind == JsonValueKind.Object &&
                          dependency.Value.TryGetProperty("version", out var range)
                            ? range.GetString() : null;
                    if (requested is not null && !CheckRange(resolved, dependency.Name, requested, target.Name))
                        return false;
                }
            }
            if (!assets.RootElement.TryGetProperty("project", out var project) ||
                !project.TryGetProperty("frameworks", out var frameworks) ||
                !frameworks.TryGetProperty(framework, out var spec) ||
                !spec.TryGetProperty("dependencies", out var direct))
                continue;
            foreach (var dependency in direct.EnumerateObject())
            {
                if (dependency.Value.ValueKind == JsonValueKind.Object &&
                    dependency.Value.TryGetProperty("target", out var kind) &&
                    kind.GetString()?.Equals("Package", StringComparison.OrdinalIgnoreCase) == false)
                    continue;
                var requested = dependency.Value.ValueKind == JsonValueKind.String
                    ? dependency.Value.GetString()
                    : dependency.Value.ValueKind == JsonValueKind.Object &&
                      dependency.Value.TryGetProperty("version", out var range)
                        ? range.GetString() : null;
                if (requested is not null && !CheckRange(resolved, dependency.Name, requested, target.Name))
                    return false;
            }
        }
        return true;
    }

    private bool CheckRange(
        IReadOnlyDictionary<string, NuGetVersion> resolved, string name, string requested, string target)
    {
        var validRange = VersionRange.TryParse(requested, out var range);
        var found = resolved.TryGetValue(name, out var version);
        if (!validRange || !found || !range!.Satisfies(version))
        {
            Log.LogError(
                $"Dependency guard: {Project} ({target}) resolves {name} outside requested "
                + $"range {requested} (actual: {version?.ToString() ?? "missing"}).");
            return false;
        }
        return true;
    }
}
