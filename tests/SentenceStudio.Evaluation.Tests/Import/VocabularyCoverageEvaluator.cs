using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using SentenceStudio.Services;

namespace SentenceStudio.Evaluation.Tests.Import;

/// <summary>The hand-curated reference for one fixture, shown on the coverage metrics in the report.</summary>
public sealed class VocabularyReferenceContext(ImportVocabularyFixture fixture)
    : EvaluationContext(ContextName, Render(fixture))
{
    public const string ContextName = "Reference vocabulary (hand-curated)";

    public ImportVocabularyFixture Fixture { get; } = fixture;

    private static string Render(ImportVocabularyFixture fixture)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Expected (dictionary form = gloss; alternates accepted):");
        foreach (var e in fixture.Expected)
        {
            var alternates = e.Alternates is { Count: > 0 } ? $"  [also: {string.Join(", ", e.Alternates)}]" : string.Empty;
            sb.AppendLine($"- {e.Lemma} = {e.Gloss}{alternates}");
        }

        sb.AppendLine($"Allowed extras (not required, not penalized): {string.Join(", ", fixture.Allowed)}");
        sb.AppendLine("Forbidden (penalized when pre-selected):");
        foreach (var f in fixture.Forbidden)
        {
            sb.AppendLine($"- {f.Term}: {f.Why}");
        }

        sb.Append("- any term containing Latin letters: not a Korean term");
        return sb.ToString();
    }
}

/// <summary>The app's structured preview rows, so the deterministic evaluator scores data rather than prose.</summary>
public sealed class ImportPreviewContext(ContentImportPreview preview)
    : EvaluationContext(ContextName, $"Detected format: {preview.DetectedFormat}; rows: {preview.Rows.Count}; warnings: {preview.Warnings.Count}")
{
    public const string ContextName = "App import preview";

    public ContentImportPreview Preview { get; } = preview;
}

/// <summary>
/// Custom deterministic evaluator (no model call). Scores the app's import preview against the
/// fixture's reference lemmas and returns three NumericMetrics:
///   Vocabulary Recall     (0..1, pass at >= 0.80)
///   Vocabulary Precision  (0..1, pass at >= 0.80)
///   Forbidden Terms       (count, pass at 0)
/// See <see cref="VocabularyCoverage"/> for exactly which rows are scored and how terms are matched.
/// </summary>
public sealed class VocabularyCoverageEvaluator : IEvaluator
{
    public const string RecallMetricName = "Vocabulary Recall";
    public const string PrecisionMetricName = "Vocabulary Precision";
    public const string ForbiddenMetricName = "Forbidden Terms";
    public const double RecallThreshold = 0.80;
    public const double PrecisionThreshold = 0.80;

    public IReadOnlyCollection<string> EvaluationMetricNames { get; } =
        [RecallMetricName, PrecisionMetricName, ForbiddenMetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var recall = new NumericMetric(RecallMetricName);
        var precision = new NumericMetric(PrecisionMetricName);
        var forbidden = new NumericMetric(ForbiddenMetricName);
        var result = new EvaluationResult(recall, precision, forbidden);

        var reference = additionalContext?.OfType<VocabularyReferenceContext>().FirstOrDefault();
        var preview = additionalContext?.OfType<ImportPreviewContext>().FirstOrDefault();
        if (reference is null || preview is null)
        {
            foreach (var metric in result.Metrics.Values)
            {
                metric.AddDiagnostics(EvaluationDiagnostic.Error(
                    $"{nameof(VocabularyCoverageEvaluator)} needs a {nameof(VocabularyReferenceContext)} and an {nameof(ImportPreviewContext)}."));
            }

            return new ValueTask<EvaluationResult>(result);
        }

        var score = VocabularyCoverage.Score(reference.Fixture, preview.Preview.Rows);

        recall.Value = Math.Round(score.Recall, 3);
        recall.Reason = $"{score.Found.Count} of {score.ExpectedCount} expected lemmas found in the pre-selected rows." +
            (score.Missed.Count > 0 ? $" Missed: {string.Join(", ", score.Missed.Select(m => $"{m.Lemma} ({m.Gloss})"))}." : string.Empty);
        recall.Interpretation = InterpretRatio(score.Recall, RecallThreshold, RecallMetricName);

        precision.Value = Math.Round(score.Precision, 3);
        precision.Reason = score.SelectedCount == 0
            ? "No rows were pre-selected for import."
            : $"{score.AcceptableSelectedCount} of {score.SelectedCount} pre-selected rows are expected or allowed terms." +
              (score.UnexpectedSelected.Count > 0 ? $" Not in the reference: {string.Join(", ", score.UnexpectedSelected)}." : string.Empty) +
              (score.ForbiddenSelected.Count > 0 ? $" Forbidden: {string.Join(", ", score.ForbiddenSelected)}." : string.Empty);
        precision.Interpretation = InterpretRatio(score.Precision, PrecisionThreshold, PrecisionMetricName);

        forbidden.Value = score.ForbiddenSelected.Count;
        forbidden.Reason = score.ForbiddenSelected.Count == 0
            ? "No forbidden term (surface form, wrong lemma, proper noun, English word) was pre-selected."
            : $"Pre-selected forbidden terms: {string.Join(", ", score.ForbiddenSelected)}.";
        forbidden.Interpretation = score.ForbiddenSelected.Count switch
        {
            0 => new EvaluationMetricInterpretation(EvaluationRating.Exceptional, failed: false),
            1 => new EvaluationMetricInterpretation(EvaluationRating.Poor, failed: true, reason: "1 forbidden term was pre-selected for import."),
            var n => new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, reason: $"{n} forbidden terms were pre-selected for import."),
        };

        if (score.ForbiddenUnchecked.Count > 0)
        {
            forbidden.AddDiagnostics(EvaluationDiagnostic.Informational(
                $"Shown but auto-deselected by the app's low-confidence gate (not scored): {string.Join(", ", score.ForbiddenUnchecked)}."));
        }

        var uncheckedRows = score.Rows.Where(r => !r.Selected).Select(r => r.Term).ToList();
        if (uncheckedRows.Count > 0)
        {
            precision.AddDiagnostics(EvaluationDiagnostic.Informational(
                $"{uncheckedRows.Count} low-confidence row(s) shown unchecked and not scored: {string.Join(", ", uncheckedRows)}."));
        }

        precision.AddDiagnostics(EvaluationDiagnostic.Informational(
            "Scored preview rows (verdict | term = gloss):\n" +
            string.Join("\n", score.Rows.Select(r => $"{r.Verdict}{(r.Selected ? string.Empty : ", unchecked")} | {r.Term} = {r.Gloss}"))));

        foreach (var warning in preview.Preview.Warnings)
        {
            recall.AddDiagnostics(EvaluationDiagnostic.Warning($"App preview warning: {warning}"));
        }

        recall.AddOrUpdateContext(reference);
        precision.AddOrUpdateContext(reference);
        forbidden.AddOrUpdateContext(reference);

        foreach (var metric in result.Metrics.Values)
        {
            metric.AddOrUpdateMetadata("detectedFormat", preview.Preview.DetectedFormat);
            metric.AddOrUpdateMetadata("rowsShown", score.Rows.Count.ToString(CultureInfo.InvariantCulture));
            metric.AddOrUpdateMetadata("rowsPreSelected", score.SelectedCount.ToString(CultureInfo.InvariantCulture));
        }

        return new ValueTask<EvaluationResult>(result);
    }

    internal static EvaluationMetricInterpretation InterpretRatio(double value, double threshold, string name)
    {
        var rating = value switch
        {
            >= 0.95 => EvaluationRating.Exceptional,
            >= 0.80 => EvaluationRating.Good,
            >= 0.60 => EvaluationRating.Average,
            >= 0.40 => EvaluationRating.Poor,
            _ => EvaluationRating.Unacceptable,
        };

        return value < threshold
            ? new EvaluationMetricInterpretation(rating, failed: true, reason: $"{name} {value:P0} is below the {threshold:P0} pass threshold.")
            : new EvaluationMetricInterpretation(rating, failed: false);
    }
}

/// <summary>
/// Renders the preview the way a learner reviews it, one row per line. Written to the capture
/// files, and used as the evaluated response only when the app never called the model.
/// </summary>
public static class ImportPreviewRenderer
{
    public static string Render(ContentImportPreview preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Import preview ({preview.DetectedFormat}), {preview.Rows.Count} rows:");
        foreach (var row in preview.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.TargetLanguageTerm))
            {
                sb.AppendLine($"{row.RowNumber}. (no term) {row.Error}");
                continue;
            }

            var selection = row.IsSelected ? string.Empty : " (unchecked: low confidence)";
            var example = string.IsNullOrWhiteSpace(row.SourceSentence) ? string.Empty : $" | example: {row.SourceSentence}";
            sb.AppendLine($"{row.RowNumber}. {row.TargetLanguageTerm} = {row.NativeLanguageTerm} [{row.LexicalUnitType}]{selection}{example}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The reference list as plain statements, used as Completeness ground truth. Alternates are
    /// included so the judge accepts the same forms the deterministic evaluator accepts.
    /// </summary>
    public static string RenderGroundTruth(ImportVocabularyFixture fixture) =>
        "Vocabulary a learner should get from this text, in dictionary form:\n" +
        string.Join("\n", fixture.Expected.Select(e =>
            $"- {e.Lemma} = {e.Gloss}" +
            (e.Alternates is { Count: > 0 } ? $" (also acceptable: {string.Join(", ", e.Alternates)})" : string.Empty)));
}
