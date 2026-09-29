using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using CoreSync.PostgreSQL;
using Npgsql;

namespace SentenceStudio.Api.Tests.Startup;

public sealed class CoreSyncProviderPackageTests
{
    private const string ProviderId = "CoreSync.PostgreSQL";
    private const string ProviderVersion = "0.1.129-local3";
    private const string ProviderSha256 = "c301f31bb4c7415242b545835230d566499f6805fd3bc74cd51098e10c0a05f9";

    [Fact]
    public void Api_resolves_the_reviewed_caller_owned_provider()
    {
        var root = RepositoryRoot();
        var packageVersions = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var pin = packageVersions.Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == ProviderId);
        Assert.Equal($"[{ProviderVersion}]", (string?)pin.Attribute("Version"));

        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "src", "SentenceStudio.Api", "obj", "project.assets.json")));
        var libraries = assets.RootElement.GetProperty("libraries");
        var provider = Assert.Single(libraries.EnumerateObject().Where(
            library => library.Name.StartsWith($"{ProviderId}/", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal($"{ProviderId}/{ProviderVersion}", provider.Name);
        Assert.True(libraries.TryGetProperty("CoreSync/0.1.129", out _),
            "the stable HTTP and SQLite packages require the stable CoreSync base");

        var packagesPath = assets.RootElement.GetProperty("project").GetProperty("restore")
            .GetProperty("packagesPath").GetString();
        var packagePath = provider.Value.GetProperty("path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(packagesPath));
        Assert.False(string.IsNullOrWhiteSpace(packagePath));

        var cachedArchive = Path.Combine(packagesPath!, packagePath!,
            $"{ProviderId.ToLowerInvariant()}.{ProviderVersion}.nupkg");
        Assert.True(File.Exists(cachedArchive), $"Resolved package archive is missing: {cachedArchive}");
        Assert.Equal(ProviderSha256, Sha256(cachedArchive));

        var overload = typeof(PostgreSQLSyncProvider).GetMethod(
            nameof(PostgreSQLSyncProvider.ApplyProvisionAsync),
            [typeof(NpgsqlConnection), typeof(NpgsqlTransaction), typeof(CancellationToken)]);
        Assert.NotNull(overload);
        Assert.Equal(typeof(Task), overload.ReturnType);
    }

    [Fact]
    public void Exact_provider_and_stable_base_map_to_their_reviewed_sources()
    {
        var root = RepositoryRoot();
        var configPath = Path.Combine(root, "src", "NuGet.config");
        var config = XDocument.Load(configPath);
        var mapping = config.Root!.Element("packageSourceMapping")!;

        Assert.Equal(new[] { "coresync-postgresql-local3" }, EffectiveSources(mapping, ProviderId));
        Assert.Equal(new[] { "nuget.org" }, EffectiveSources(mapping, "CoreSync"));

        var sources = config.Root.Element("packageSources")!.Elements("add")
            .ToDictionary(source => (string)source.Attribute("key")!,
                source => (string)source.Attribute("value")!);
        Assert.Equal("https://api.nuget.org/v3/index.json", sources["nuget.org"]);

        var reviewedFeed = sources["coresync-postgresql-local3"];
        Assert.False(Path.IsPathFullyQualified(reviewedFeed));
        var feedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, reviewedFeed));
        Assert.StartsWith(Path.Combine(root, "lib") + Path.DirectorySeparatorChar, feedPath,
            StringComparison.Ordinal);
        Assert.True(Directory.Exists(feedPath), $"Reviewed provider feed is missing: {feedPath}");

        var archive = Assert.Single(Directory.EnumerateFiles(feedPath, "*.nupkg",
            SearchOption.AllDirectories));
        Assert.Equal($"{ProviderId}.{ProviderVersion}.nupkg", Path.GetFileName(archive));
        Assert.Equal(ProviderSha256, Sha256(archive));
    }

    private static string[] EffectiveSources(XElement mapping, string packageId)
    {
        var matches = mapping.Elements("packageSource")
            .Select(source => (
                Key: (string)source.Attribute("key")!,
                Specificity: source.Elements("package")
                    .Select(package => PatternSpecificity((string)package.Attribute("pattern")!, packageId))
                    .DefaultIfEmpty(-1).Max()))
            .Where(match => match.Specificity >= 0)
            .ToArray();
        Assert.NotEmpty(matches);

        var highestSpecificity = matches.Max(match => match.Specificity);
        return matches.Where(match => match.Specificity == highestSpecificity)
            .Select(match => match.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    private static int PatternSpecificity(string pattern, string packageId)
    {
        if (string.Equals(pattern, packageId, StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        return pattern.EndsWith('*')
            && packageId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                ? pattern.Length - 1
                : -1;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
