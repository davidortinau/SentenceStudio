using System.Globalization;
using System.Text;

namespace SentenceStudio.Api.Coach.Opportunities.Digest;

/// <summary>
/// Renders the operational digest as markdown a reviewer can read, paste, or archive.
/// </summary>
/// <remarks>
/// <para>
/// Every value interpolated here comes off <see cref="CoachOpportunityDigest"/>, and that shape
/// carries counts, closed codes, statuses, timestamps, and fingerprints only. There is no path by
/// which a learner's words, an owner id, a conversation id, a message id, a tool argument, or an
/// email reaches this output — which is what makes the result safe to print to a CI log, upload as
/// a build artifact, or paste into <c>docs/sam-future-opportunities.md</c>.
/// </para>
/// <para>
/// The renderer states the window and the generation instant on purpose. A digest read six days
/// later, out of its artifact, must not be mistakable for the current state of the ledger.
/// </para>
/// </remarks>
public static class CoachOpportunityDigestMarkdown
{
    private const string DateFormat = "yyyy-MM-dd HH:mm 'UTC'";

    /// <summary>Renders the whole digest.</summary>
    public static string Render(CoachOpportunityDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);

        var builder = new StringBuilder();

        builder.AppendLine("# Sam opportunity digest");
        builder.AppendLine();
        builder.AppendLine(
            $"- **Generated:** {Format(digest.GeneratedAtUtc)}");
        builder.AppendLine(
            "- **Window:** " +
            (digest.WindowStartUtc is { } start
                ? $"{Format(start)} \u2192 {Format(digest.WindowEndUtc)}"
                : $"everything retained \u2192 {Format(digest.WindowEndUtc)}"));
        builder.AppendLine(
            $"- **Ledger buckets:** {digest.TotalOpportunityRows} \u00b7 " +
            $"**problems:** {digest.Lines.Count} \u00b7 " +
            $"**learner reports:** {digest.TotalReports}");
        builder.AppendLine(
            "- **Contents:** counts, closed codes, review statuses, timestamps, and content-free " +
            "fingerprints. No learner content, no owner, conversation, message, turn, or write " +
            "identifier, and no decrypted evidence.");

        if (digest.Truncated)
        {
            builder.AppendLine(
                $"- **Truncated:** more than {CoachOpportunityDigestReader.MaxLines} distinct " +
                "problems fell in this window; narrow the window and re-run.");
        }

        builder.AppendLine();
        AppendReportReasons(builder, digest);
        builder.AppendLine();
        AppendLines(builder, digest);

        return builder.ToString();
    }

    private static void AppendReportReasons(StringBuilder builder, CoachOpportunityDigest digest)
    {
        builder.AppendLine("## Learner reports by reason");
        builder.AppendLine();

        if (digest.ReportReasons.Count == 0)
        {
            builder.AppendLine(
                "_No learner reports in this window. That is not the same as no problems — see " +
                "the known gaps in `docs/sam-future-opportunities.md`._");
            return;
        }

        builder.AppendLine("| Reason | Reports | Distinct learners | First | Last |");
        builder.AppendLine("|---|---:|---:|---|---|");

        foreach (var reason in digest.ReportReasons)
        {
            builder.AppendLine(
                $"| `{reason.Reason}` | {reason.ReportCount} | {reason.DistinctLearners} | " +
                $"{Format(reason.FirstReportedAtUtc)} | {Format(reason.LastReportedAtUtc)} |");
        }
    }

    private static void AppendLines(StringBuilder builder, CoachOpportunityDigest digest)
    {
        builder.AppendLine("## Problems by frequency");
        builder.AppendLine();

        if (digest.Lines.Count == 0)
        {
            builder.AppendLine(
                "_No ledger rows in this window. An empty digest is not evidence of an absence of " +
                "problems — see the known gaps in `docs/sam-future-opportunities.md`._");
            return;
        }

        builder.AppendLine(
            "| Fingerprint | Kind | Capability | Tool | Failure | Occurrences | Learners | " +
            "Buckets | First | Last | Statuses |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---:|---|---|---|");

        foreach (var line in digest.Lines)
        {
            builder.AppendLine(
                $"| `{CoachOpportunityFingerprint.Describe(line.Fingerprint)}` " +
                $"| {line.Kind} " +
                $"| `{line.CapabilityCode}` " +
                $"| {Optional(line.ToolName)} " +
                $"| {Optional(line.FailureCode)} " +
                $"| {line.TotalOccurrences} " +
                $"| {line.DistinctLearners} " +
                $"| {line.RowCount} " +
                $"| {Format(line.FirstObservedAtUtc)} " +
                $"| {Format(line.LastObservedAtUtc)} " +
                $"| {(line.Statuses.Count == 0 ? "\u2014" : string.Join(", ", line.Statuses))} |");
        }

        builder.AppendLine();
        builder.AppendLine(
            "Review a line by recording a decision in `docs/sam-future-opportunities.md` against " +
            "its fingerprint. Reading the encrypted evidence behind a row still requires the " +
            "Development-only operator surface and the learner's own scope.");
    }

    private static string Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "\u2014" : $"`{value}`";

    private static string Format(DateTime value) =>
        value.ToString(DateFormat, CultureInfo.InvariantCulture);
}
