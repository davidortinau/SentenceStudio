using System.Reflection;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Build-time-style guard against the timezone-bug regression. Plan-generation
/// code must read "today" through <c>IPlanDateContext.UserLocalDate</c>, never
/// directly via <see cref="DateTime.UtcNow"/>, <see cref="DateTime.Today"/>,
/// etc. — that's what caused Captain's daily plan to roll over at the wrong
/// midnight in the first place.
///
/// We scan source files under the gated directories for banned tokens. The
/// scan is line-oriented so we can allow opt-out via the inline marker
/// <c>// allow:plan-date</c> at end-of-line (used sparingly with rationale).
/// </summary>
public class PlanDateContextBannedSymbolsTests
{
    private static readonly string[] _bannedTokens =
    {
        "DateTime.UtcNow",
        "DateTime.Now",
        "DateTime.Today",
        "DateTimeOffset.UtcNow",
        "DateTimeOffset.Now",
    };

    private static readonly string[] _gatedRelativePaths =
    {
        Path.Combine("src", "SentenceStudio.Shared", "Services", "PlanGeneration"),
        Path.Combine("src", "SentenceStudio.Api", "Plans"),
    };

    private const string AllowMarker = "// allow:plan-date";

    [Fact]
    public void PlanGenerationCode_DoesNotCallSystemClockDirectly()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var rel in _gatedRelativePaths)
        {
            var dir = Path.Combine(repoRoot, rel);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Self-exemption: the BannedSymbols guard file itself (this test)
                // and the IPlanDateContext / PlanDateContext implementation are
                // allowed to reference the system clock — the whole point of
                // PlanDateContext is to wrap it.
                var name = Path.GetFileName(file);
                if (name is "PlanDateContext.cs" or "IPlanDateContext.cs"
                    or "HttpPlanDateContext.cs" or "DevicePlanDateContextProvider.cs"
                    or "TimeZoneResolver.cs")
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.Contains(AllowMarker, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    foreach (var token in _bannedTokens)
                    {
                        if (line.Contains(token, StringComparison.Ordinal))
                        {
                            offenders.Add(
                                $"{Path.GetRelativePath(repoRoot, file)}:{i + 1}: '{token}' — " +
                                "use IPlanDateContext instead, or append '// allow:plan-date <reason>' if intentional.");
                        }
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Plan-generation code referenced the system clock directly:\n" +
            string.Join("\n", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && File.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root (expected src/SentenceStudio.sln).");
    }
}
