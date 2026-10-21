using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using SentenceStudio.Evaluation.Tests.Infrastructure;
using SentenceStudio.Services;
using Xunit.Abstractions;

namespace SentenceStudio.Evaluation.Tests.Import;

/// <summary>
/// Live evaluation of the Import content flow's vocabulary extraction (free-text, Vocabulary mode).
/// One scenario per fixture: "Import.VocabularyExtraction.&lt;fixture-id&gt;".
/// Gated: runs only with SS_RUN_LIVE_EVALS=1. See docs/evaluation/README.md.
/// </summary>
[Trait("Category", "LiveEval")]
public sealed class ImportVocabularyExtractionEvaluationTests(ITestOutputHelper output)
{
    // Dots, not slashes: the disk store rejects path separators, and the report builds its tree from dots.
    public const string ScenarioPrefix = "Import.VocabularyExtraction.";

    private static readonly JsonSerializerOptions CaptureJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TheoryData<string> FixtureIds()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in ImportVocabularyFixtures.All.Fixtures)
        {
            data.Add(fixture.Id);
        }

        return data;
    }

    [LiveEvalTheory]
    [MemberData(nameof(FixtureIds))]
    public async Task VocabularyExtraction(string fixtureId)
    {
        var run = LiveEvalRun.Current;
        var settings = run.Settings;
        var set = ImportVocabularyFixtures.All;
        var fixture = ImportVocabularyFixtures.Get(fixtureId);
        var scenarioName = ScenarioPrefix + fixture.Id;
        LiveCallMeterChatClient.CurrentScenario.Value = scenarioName;
        var liveCallsBefore = run.Ledger.Records.Count;

        await using var scenarioRun = await run.ImportReporting.CreateScenarioRunAsync(
            scenarioName,
            additionalTags: [.. fixture.Tags, $"fixture:{fixture.Id}"]);

        // 1. Run the app's real import path against the model under test.
        var target = await run.CreateTargetPipelineAsync(scenarioRun);
        ContentImportPreview preview;
        IReadOnlyList<string> appLogs;
        await using (var harness = new AppImportHarness(target, settings.TargetDeployment))
        {
            preview = await harness.ParseAsync(fixture.Text, set.TargetLanguage, set.NativeLanguage);
            appLogs = harness.WarningsAndErrors;
        }

        var targetLiveCalls = run.Ledger.Records.Skip(liveCallsBefore).Where(r => r.Role == "target").ToList();
        var extraction = target.Calls.FirstOrDefault();

        // 2. What the judges and the report see: the exact prompt the app rendered and sent, and the
        //    model's exact typed-JSON reply (the GetResponseAsync<FreeTextVocabularyExtractionResponse>
        //    payload). The judges must see the real contract: a rendered list made Groundedness mark the
        //    reply down for "not returning the requested JSON". The deterministic evaluator scores the
        //    app's preview rows (after its confidence gating) through ImportPreviewContext instead.
        //    If the app never called the model (a non-free-text route), the preview text stands in.
        var rendered = ImportPreviewRenderer.Render(preview);
        IList<ChatMessage> messages = extraction is not null
            ? extraction.Messages.ToList()
            : [new ChatMessage(ChatRole.User, fixture.Text)];
        var response = extraction?.Response
            ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, rendered)) { ModelId = settings.TargetDeployment };

        // 3. Evaluate: deterministic coverage + LLM-judged groundedness and completeness.
        var result = await scenarioRun.EvaluateAsync(
            messages,
            response,
            additionalContext:
            [
                new VocabularyReferenceContext(fixture),
                new ImportPreviewContext(preview),
                new GroundednessEvaluatorContext(fixture.Text),
                new CompletenessEvaluatorContext(ImportPreviewRenderer.RenderGroundTruth(fixture)),
            ]);

        // 4. Annotate the coverage metrics with the target call's cost profile (persisted on dispose).
        var targetUsage = extraction?.Response.Usage;
        foreach (var name in new[] { VocabularyCoverageEvaluator.RecallMetricName, VocabularyCoverageEvaluator.PrecisionMetricName })
        {
            var metric = result.Get<NumericMetric>(name);
            metric.AddOrUpdateMetadata("target.model", settings.TargetDeployment);
            metric.AddOrUpdateMetadata("target.calls", target.Calls.Count.ToString(CultureInfo.InvariantCulture));
            metric.AddOrUpdateMetadata("target.liveCalls", targetLiveCalls.Count.ToString(CultureInfo.InvariantCulture));
            metric.AddOrUpdateMetadata("target.inputTokens", (targetUsage?.InputTokenCount ?? 0).ToString(CultureInfo.InvariantCulture));
            metric.AddOrUpdateMetadata("target.outputTokens", (targetUsage?.OutputTokenCount ?? 0).ToString(CultureInfo.InvariantCulture));
            metric.AddOrUpdateMetadata("target.reasoningTokens", (targetUsage?.ReasoningTokenCount ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var log in appLogs)
        {
            result.Get<NumericMetric>(VocabularyCoverageEvaluator.RecallMetricName)
                .AddDiagnostics(EvaluationDiagnostic.Warning($"App log: {log}"));
        }

        await WriteCaptureAsync(settings, fixture, scenarioName, preview, target, targetLiveCalls, appLogs, rendered);

        // 5. Console summary.
        output.WriteLine($"{scenarioName}  format={preview.DetectedFormat} rows={preview.Rows.Count} targetCalls={target.Calls.Count} live={targetLiveCalls.Count}");
        foreach (var metric in result.Metrics.Values)
        {
            var value = metric is NumericMetric n ? n.Value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "null" : "?";
            output.WriteLine($"  {metric.Name,-22} {value,-6} {(metric.Interpretation?.Failed == true ? "FAIL" : "pass")}  {metric.Reason}");
            foreach (var d in metric.Diagnostics ?? [])
            {
                output.WriteLine($"      [{d.Severity}] {d.Message}");
            }
        }

        output.WriteLine($"  ledger: {run.Ledger.Records.Count} live calls, est. ${run.Ledger.SpentUsd:F4} of ${run.Ledger.BudgetCapUsd:F2}");

        // Infrastructure must work: every metric produced a value and no evaluator reported an error.
        Assert.False(
            result.ContainsDiagnostics(d => d.Severity == EvaluationDiagnosticSeverity.Error),
            "An evaluator reported an error diagnostic; see the test output and report.");
        Assert.All(result.Metrics.Values, m => Assert.NotNull(((NumericMetric)m).Value));

        if (settings.EnforceThresholds)
        {
            Assert.All(result.Metrics.Values, m => Assert.False(m.Interpretation?.Failed == true, $"{m.Name}: {m.Interpretation?.Reason}"));
        }
    }

    private static async Task WriteCaptureAsync(
        LiveEvalSettings settings,
        ImportVocabularyFixture fixture,
        string scenarioName,
        ContentImportPreview preview,
        CapturingChatClient target,
        IReadOnlyList<LiveCallRecord> targetLiveCalls,
        IReadOnlyList<string> appLogs,
        string rendered)
    {
        var folder = Path.Combine(settings.StorageRoot, "captures", settings.ExecutionName);
        Directory.CreateDirectory(folder);
        var capture = new
        {
            scenario = scenarioName,
            fixture = fixture.Id,
            targetDeployment = settings.TargetDeployment,
            detectedFormat = preview.DetectedFormat,
            warnings = preview.Warnings,
            appLogs,
            calls = target.Calls.Select(c => new
            {
                prompt = string.Join("\n", c.Messages.Select(m => $"[{m.Role}] {m.Text}")),
                responseFormat = c.Options?.ResponseFormat?.GetType().Name,
                rawResponse = c.Response.Text,
                finishReason = c.Response.FinishReason?.Value,
                usage = c.Response.Usage is { } u
                    ? new { u.InputTokenCount, u.OutputTokenCount, u.ReasoningTokenCount, u.CachedInputTokenCount }
                    : null,
            }),
            liveCalls = targetLiveCalls,
            rows = preview.Rows.Select(r => new
            {
                r.RowNumber,
                r.TargetLanguageTerm,
                r.NativeLanguageTerm,
                r.SourceSentence,
                lexicalUnitType = r.LexicalUnitType.ToString(),
                status = r.Status.ToString(),
                r.Error,
                r.IsSelected,
            }),
            rendered,
        };

        await File.WriteAllTextAsync(Path.Combine(folder, fixture.Id + ".json"), JsonSerializer.Serialize(capture, CaptureJson));
    }
}
