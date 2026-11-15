using System.Reflection;
using FluentAssertions;
using SentenceStudio.Contracts;
using Xunit;

namespace SentenceStudio.UnitTests.Lint;

/// <summary>
/// Claim names are part of the wire contract between the token issuers and every endpoint that
/// scopes data to the caller. A typo in a literal (<c>"user_profileid"</c>) silently degrades to
/// anonymous behaviour, so code must use the <see cref="AuthClaimTypes"/> constants (#202).
/// </summary>
public class ClaimNameLiteralTests
{
    /// <summary>
    /// Files allowed to contain a claim name as a literal, relative to the repository root, each
    /// with the reason. They use the string as data, not to read or issue a claim.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/SentenceStudio.Contracts/AuthClaimTypes.cs"] = "the definition",
        ["tests/SentenceStudio.Api.Tests/Coach/CoachTelemetryTests.cs"] =
            "lists identifier names that must never become telemetry tags",
        ["tests/SentenceStudio.Api.Tests/Coach/Operations/CoachWriteDomainsPostgresTests.cs"] =
            "proves Sam refuses a profile setting with this name",
    };

    private static readonly string[] ScannedExtensions = { ".cs", ".razor" };

    /// <summary>Every claim name in <see cref="AuthClaimTypes"/>, read by reflection so a new
    /// constant is protected without editing this test.</summary>
    public static TheoryData<string> ClaimNames()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(AuthClaimTypes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(string))
                data.Add((string)field.GetRawConstantValue()!);
        }
        return data;
    }

    [Fact]
    public void AuthClaimTypes_has_claim_names_to_protect()
    {
        ClaimNames().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ClaimNames))]
    public void Claim_name_appears_as_a_literal_only_in_allowed_files(string claimName)
    {
        var root = RepositoryRoot();
        var literal = "\"" + claimName + "\"";

        var offenders = new[] { "src", "tests" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
            .Where(path => ScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !IsBuildOutput(relative) && !Allowed.ContainsKey(relative))
            .Where(relative => File.ReadAllText(Path.Combine(root, relative)).Contains(literal, StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(
            "use AuthClaimTypes instead of the literal {0} (or add the file to Allowed with a reason)", literal);
    }

    [Fact]
    public void Allowed_files_exist()
    {
        var root = RepositoryRoot();
        Allowed.Keys.Where(relative => !File.Exists(Path.Combine(root, relative)))
            .Should().BeEmpty("a stale entry would hide a moved file");
    }

    private static bool IsBuildOutput(string relative) =>
        relative.Split('/').Any(segment => segment is "bin" or "obj" or "node_modules");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.Contracts", "AuthClaimTypes.cs")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
    }
}
