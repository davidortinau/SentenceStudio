using FluentAssertions;
using System.Text.Json;
using System.Xml.Linq;
using SentenceStudio.Application.Capabilities;

namespace SentenceStudio.UnitTests.ApplicationCapabilities;

public sealed class ApplicationCapabilityArchitectureTests
{
    [Fact]
    public void Evaluated_application_project_has_contracts_as_its_only_project_reference()
    {
        var references = EvaluatedProjectReferences();

        AssertOnlyContractsReference(references);
    }

    [Fact]
    public void Evaluated_reference_guard_rejects_an_unused_forbidden_project_reference()
    {
        var failure = Record.Exception(() => AssertOnlyContractsReference(
            ["SentenceStudio.Contracts", "SentenceStudio.Shared"]));

        failure.Should().NotBeNull();
        failure!.Message.Should().Contain("SentenceStudio.Shared");
    }

    [Fact]
    public void Emitted_application_assembly_has_only_the_contracts_sentence_studio_reference()
    {
        var references = typeof(IApplicationCapabilityCatalog).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("SentenceStudio.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        references.Should().Equal("SentenceStudio.Contracts");
    }

    [Fact]
    public void Application_project_has_no_forbidden_framework_or_infrastructure_dependencies()
    {
        var references = typeof(IApplicationCapabilityCatalog).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();
        var forbiddenFragments = new[]
        {
            "AspNetCore",
            "EntityFrameworkCore",
            "Microsoft.Agents",
            "Maui",
            "Http",
            "SentenceStudio.Api",
            "SentenceStudio.Coach",
            "SentenceStudio.Infrastructure",
            "SentenceStudio.UI"
        };

        foreach (var fragment in forbiddenFragments)
        {
            references.Should().NotContain(
                name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                $"the pure Application catalog must not reference {fragment}");
        }
    }

    [Fact]
    public void Public_catalog_api_uses_only_application_and_coach_technical_naming()
    {
        var publicIdentifiers = typeof(IApplicationCapabilityCatalog).Assembly
            .GetExportedTypes()
            .SelectMany(type => new[] { type.FullName! }
                .Concat(type.GetMembers().Where(member => member.DeclaringType == type).Select(member => member.Name)))
            .ToArray();

        publicIdentifiers.Should().NotContain(
            identifier => identifier.Contains("Sam", StringComparison.Ordinal),
            "product-facing names must not become technical identifiers");
    }

    private static string[] EvaluatedProjectReferences()
    {
        var repositoryRoot = RepositoryRoot();
        var graphPath = Path.Combine(
            repositoryRoot,
            "src",
            "SentenceStudio.Application",
            "obj",
            "SentenceStudio.Application.csproj.nuget.dgspec.json");
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "SentenceStudio.Application",
            "SentenceStudio.Application.csproj");
        File.Exists(graphPath).Should().BeTrue(
            "the evaluated NuGet/MSBuild dependency graph is produced before the test assembly builds");

        using var document = JsonDocument.Parse(File.ReadAllText(graphPath));
        var project = document.RootElement
            .GetProperty("projects")
            .EnumerateObject()
            .Single(entry =>
                string.Equals(
                    Path.GetFileName(entry.Name),
                    "SentenceStudio.Application.csproj",
                    StringComparison.Ordinal));
        var projectReferences = project.Value
            .GetProperty("restore")
            .GetProperty("frameworks")
            .GetProperty("net10.0")
            .GetProperty("projectReferences");

        var evaluatedReferences = projectReferences
            .EnumerateObject()
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var declaredReferences = XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFileNameWithoutExtension(
                path!.Replace('\\', Path.DirectorySeparatorChar)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        evaluatedReferences.Should().Equal(
            declaredReferences,
            "a stale evaluated graph must not hide a declared ProjectReference");

        return evaluatedReferences;
    }

    private static void AssertOnlyContractsReference(IEnumerable<string> references) =>
        references.Should().Equal("SentenceStudio.Contracts");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Application")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the SentenceStudio repository root.");
    }
}
