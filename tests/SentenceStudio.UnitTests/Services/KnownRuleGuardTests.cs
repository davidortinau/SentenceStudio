using System.Text.RegularExpressions;
using FluentAssertions;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// docs/known-words-plan.md: "Known" is decided in one place, <c>VocabularyKnownRule</c>, which
/// reads the learner's mark and mode. A file that tests the score threshold or the retired
/// Familiar flags on its own would show or schedule a marked word by its score.
/// </summary>
public class KnownRuleGuardTests
{
    // Files that hold the rule, calculate the score, or mention the threshold in text or in a
    // one-time historical fix; none of them decides Known for the learner.
    private static readonly string[] Allowed =
    [
        "SentenceStudio.Shared/Services/VocabularyKnownRule.cs",
        "SentenceStudio.Shared/Services/VocabularyMasteryCalculator.cs",
        "SentenceStudio.Shared/Services/SyncService.cs",
        "SentenceStudio.Shared/Services/PlanGeneration/GeneratedPlanValidator.cs",
        // GetDue* comments, and resource aggregates whose KnownCount nothing reads
        // (docs/native-review-backlog.md).
        "SentenceStudio.Shared/Data/VocabularyProgressRepository.cs",
        // An extra score filter on words the Known rule already chose as due.
        "SentenceStudio.Shared/Services/SmartResourceService.cs",
    ];

    private static readonly Regex Stray = new(
        @"MasteryScore\s*>=\s*0\.8\d*f?|\bMASTERY_THRESHOLD\b|\bIsFamiliar\b|\bIsInGracePeriod\b",
        RegexOptions.Compiled);

    [Fact]
    public void NoSourceFileDecidesKnownOnItsOwn()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: Path.GetRelativePath(src, path).Replace('\\', '/'), Full: path))
            .Where(file => !Allowed.Contains(file.Path, StringComparer.Ordinal))
            .SelectMany(file => File.ReadLines(file.Full)
                .Select((line, index) => (file.Path, Line: index + 1, Text: line))
                .Where(row => Stray.IsMatch(row.Text)))
            .Select(row => $"{row.Path}:{row.Line}: {row.Text.Trim()}")
            .ToList();

        offenders.Should().BeEmpty(
            "Known is decided by VocabularyKnownRule with the learner's mode (docs/known-words-plan.md)");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
