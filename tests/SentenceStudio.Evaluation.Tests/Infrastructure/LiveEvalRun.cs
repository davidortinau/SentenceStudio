using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using SentenceStudio.Evaluation.Tests.Import;

namespace SentenceStudio.Evaluation.Tests.Infrastructure;

/// <summary>
/// Process-wide state for one live evaluation run: settings, the spend ledger, the judge, and the
/// MEAI ReportingConfiguration (disk results + disk response cache).
/// </summary>
public sealed class LiveEvalRun
{
    /// <summary>Bump when the eval pipeline changes in a way that should invalidate cached responses.</summary>
    public const string PipelineVersion = "import-eval-v1";

    private static readonly Lazy<LiveEvalRun> Instance = new(() => new LiveEvalRun(LiveEvalSettings.FromEnvironment()));

    private LiveEvalRun(LiveEvalSettings settings)
    {
        Settings = settings;
        Directory.CreateDirectory(settings.StorageRoot);
        Ledger = new LiveCallLedger(settings.StorageRoot, settings.BudgetCapUsd);

        // Judge pipeline (outermost first): [MEAI response cache, added per scenario by ScenarioRun]
        //   -> ReasoningJudgeChatClient -> LiveCallMeterChatClient -> Azure OpenAI (judge deployment)
        var judgeAzure = EvalChatClients.CreateAzure(settings.ResourceEndpoint, settings.JudgeDeployment);
        var judgeMeter = new LiveCallMeterChatClient(judgeAzure, Ledger, settings.JudgeDeployment, "judge", settings.ExecutionName);
        JudgeClient = new ReasoningJudgeChatClient(judgeMeter, settings.JudgeMaxOutputTokens, settings.JudgeReasoningEffort);

        // Target network client, shared; each scenario wraps it with its own cache and capture.
        TargetAzureClient = EvalChatClients.CreateAzure(settings.ResourceEndpoint, settings.TargetDeployment);

        ImportReporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: settings.StorageRoot,
            evaluators:
            [
                new VocabularyCoverageEvaluator(),
                new GroundednessEvaluator(),
                new CompletenessEvaluator(),
            ],
            chatConfiguration: new ChatConfiguration(JudgeClient),
            enableResponseCaching: true,
            // Long enough that the conference rehearsal reruns from cache instead of paying again.
            timeToLiveForCacheEntries: TimeSpan.FromDays(60),
            cachingKeys:
            [
                PipelineVersion,
                $"judge:{settings.JudgeDeployment}",
                $"judge-effort:{settings.JudgeReasoningEffort}",
                $"judge-max-output:{settings.JudgeMaxOutputTokens}",
            ],
            executionName: settings.ExecutionName,
            tags:
            [
                $"target:{settings.TargetDeployment}",
                $"judge:{settings.JudgeDeployment}",
                "flow:import-content",
            ]);
    }

    public static LiveEvalRun Current => Instance.Value;

    public LiveEvalSettings Settings { get; }

    public LiveCallLedger Ledger { get; }

    public IChatClient JudgeClient { get; }

    public IChatClient TargetAzureClient { get; }

    public ReportingConfiguration ImportReporting { get; }

    /// <summary>
    /// Target pipeline for one scenario (outermost first):
    ///   CapturingChatClient -> DistributedCachingChatClient (this scenario's MEAI disk cache)
    ///   -> OutputTokenCapChatClient -> LiveCallMeterChatClient -> Azure OpenAI (target deployment)
    /// </summary>
    public async Task<CapturingChatClient> CreateTargetPipelineAsync(ScenarioRun scenarioRun)
    {
        var cache = await ImportReporting.ResponseCacheProvider!.GetCacheAsync(scenarioRun.ScenarioName, scenarioRun.IterationName);
        var meter = new LiveCallMeterChatClient(TargetAzureClient, Ledger, Settings.TargetDeployment, "target", Settings.ExecutionName);
        var capped = new OutputTokenCapChatClient(meter, Settings.TargetMaxOutputTokens);
        var cached = new DistributedCachingChatClient(capped, cache)
        {
            CacheKeyAdditionalValues =
            [
                PipelineVersion,
                "role:target",
                $"target:{Settings.TargetDeployment}",
                $"target-max-output:{Settings.TargetMaxOutputTokens}",
            ],
        };

        return new CapturingChatClient(cached);
    }
}
