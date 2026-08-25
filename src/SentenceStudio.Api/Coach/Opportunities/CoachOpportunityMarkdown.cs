using System.Globalization;
using System.Text;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// Renders a paste-ready decision-record block from content-free fields only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server renders; a human pastes and commits.</b> No bot writes
/// <c>docs/sam-future-opportunities.md</c>, because that log's value is that every entry passed
/// Zoe triage and — for anything policy-gated — Captain's explicit sign-off. A service account
/// with commit rights to it would bypass the exact gate the log exists to enforce.
/// </para>
/// <para>
/// Every field interpolated here is an enum name, a closed-vocabulary code, a digest, a count, or
/// a date. There is no path by which learner text reaches this output, which is why the result is
/// safe to paste into a repository file.
/// </para>
/// </remarks>
public static class CoachOpportunityMarkdown
{
    /// <summary>Renders the block for one reviewed row.</summary>
    public static string Render(CoachOpportunity row, CoachOpportunityRollupDto? rollup)
    {
        ArgumentNullException.ThrowIfNull(row);

        var builder = new StringBuilder();
        var reviewed = row.ReviewedAtUtc ?? row.LastObservedAtUtc;

        builder.AppendLine($"### {row.CapabilityCode}");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine(
            $"| **Date** | {reviewed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} |");
        builder.AppendLine(
            $"| **Fingerprint** | `{CoachOpportunityFingerprint.Describe(row.Fingerprint)}` |");
        builder.AppendLine(
            $"| **Signal** | {row.Kind} \u00b7 {row.CapabilityCode} \u00b7 OfferLink={row.OfferLink}{ToolFragment(row)}{FailureFragment(row)} |");

        if (rollup is { } summary)
        {
            var frequency =
                $"| **Frequency** | {summary.TotalOccurrences} occurrence(s) \u00b7 " +
                $"{summary.DistinctLearners} distinct learner(s) \u00b7 " +
                $"{summary.FirstObservedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} \u2192 " +
                $"{summary.LastObservedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} |";

            builder.AppendLine(frequency);
        }

        builder.AppendLine(
            "| **Evidence/status** | Runtime ledger (`CoachOpportunity`, fingerprint above). " +
            "Encrypted learner evidence is retained under owner scope and is not reproduced here. " +
            $"Surface: `{row.Surface}`. Disposition: `{row.Disposition}`. |");
        builder.AppendLine(
            $"| **Decision notes** | {DecisionNotes(row)} |");

        return builder.ToString();
    }

    private static string ToolFragment(CoachOpportunity row) =>
        string.IsNullOrWhiteSpace(row.ToolName) ? string.Empty : $" \u00b7 tool={row.ToolName}";

    private static string FailureFragment(CoachOpportunity row) =>
        string.IsNullOrWhiteSpace(row.FailureCode) ? string.Empty : $" \u00b7 failure={row.FailureCode}";

    private static string DecisionNotes(CoachOpportunity row)
    {
        if (row.ReviewerNoteCode is not { } note)
        {
            return "_(pending Zoe/Captain review)_";
        }

        var linked = string.IsNullOrWhiteSpace(row.LinkedSpecPath)
            ? string.Empty
            : $" \u2014 `{row.LinkedSpecPath}`";

        return $"{row.Status} \u00b7 {note}{linked}";
    }
}
