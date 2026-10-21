namespace SentenceStudio.Evaluation.Tests.Infrastructure;

/// <summary>
/// Run settings for the live evaluation suite. Every value can be overridden with an
/// environment variable so the suite never needs a config file or a secret on disk.
/// Authentication is keyless (Entra ID through the Azure CLI login), the same identity the
/// API and WebApp hosts resolve through DefaultAzureCredential on a developer machine.
/// </summary>
public sealed record LiveEvalSettings
{
    public const string GateVariable = "SS_RUN_LIVE_EVALS";

    /// <summary>True only when SS_RUN_LIVE_EVALS=1. Every live test skips otherwise.</summary>
    public static bool LiveEvalsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal);

    /// <summary>Azure OpenAI resource root (AzureOpenAIClient wants the root, not /openai/v1).</summary>
    public required Uri ResourceEndpoint { get; init; }

    /// <summary>Model under test. Defaults to the app's fast tier default (AiClientRegistration.FastModel).</summary>
    public required string TargetDeployment { get; init; }

    /// <summary>Judge model used by the LLM-based evaluators.</summary>
    public required string JudgeDeployment { get; init; }

    /// <summary>Reasoning effort for the judge, expressed with the app's own vocabulary (AiChatOptionsFactory).</summary>
    public required string JudgeReasoningEffort { get; init; }

    /// <summary>
    /// Output cap for target calls. The app sends no cap on this path; the eval adds one only so the
    /// worst-case spend is bounded. gpt-5-mini counts reasoning tokens against this cap.
    /// </summary>
    public required int TargetMaxOutputTokens { get; init; }

    /// <summary>Output cap for judge calls (replaces the evaluators' built-in 800, see ReasoningJudgeChatClient).</summary>
    public required int JudgeMaxOutputTokens { get; init; }

    /// <summary>Hard spend ceiling in USD across every live call recorded in the ledger (all runs).</summary>
    public required decimal BudgetCapUsd { get; init; }

    /// <summary>Root folder for results, response cache, captures and the live-call ledger (gitignored).</summary>
    public required string StorageRoot { get; init; }

    /// <summary>Execution name shown in the report. Re-using a name overwrites that execution's results.</summary>
    public required string ExecutionName { get; init; }

    /// <summary>When true, quality thresholds fail the test as well as the report. Default: report only.</summary>
    public required bool EnforceThresholds { get; init; }

    public static LiveEvalSettings FromEnvironment()
    {
        var central = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "America/Chicago");
        return new LiveEvalSettings
        {
            ResourceEndpoint = new Uri(Env("SS_EVAL_AOAI_ENDPOINT", "https://daortin-sstudio-eus2.openai.azure.com/")),
            TargetDeployment = Env("SS_EVAL_TARGET_DEPLOYMENT", "gpt-5-mini"),
            JudgeDeployment = Env("SS_EVAL_JUDGE_DEPLOYMENT", "gpt-5"),
            JudgeReasoningEffort = Env("SS_EVAL_JUDGE_REASONING_EFFORT", "low"),
            TargetMaxOutputTokens = int.Parse(Env("SS_EVAL_TARGET_MAX_OUTPUT_TOKENS", "12000")),
            JudgeMaxOutputTokens = int.Parse(Env("SS_EVAL_JUDGE_MAX_OUTPUT_TOKENS", "3000")),
            BudgetCapUsd = decimal.Parse(Env("SS_EVAL_BUDGET_USD", "1.00"), System.Globalization.CultureInfo.InvariantCulture),
            StorageRoot = Env("SS_EVAL_STORAGE_ROOT", Path.Combine(RepositoryRoot(), "artifacts", "evaluation")),
            ExecutionName = Env("SS_EVAL_EXECUTION_NAME", $"local-{central:yyyy-MM-dd-HHmm}"),
            EnforceThresholds = Env("SS_EVAL_ENFORCE_THRESHOLDS", "0") == "1",
        };
    }

    private static string Env(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>Walks up from the test output folder to the folder that holds Directory.Packages.props.</summary>
    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate the repository root (Directory.Packages.props) above " + AppContext.BaseDirectory);
    }
}
