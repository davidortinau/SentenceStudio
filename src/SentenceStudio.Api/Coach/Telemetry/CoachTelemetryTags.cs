using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Telemetry;

/// <summary>
/// The closed set of tag names the Learning Coach may attach to a span or a metric, and the guard
/// that enforces it.
/// </summary>
/// <remarks>
/// <para>
/// The coach handles learner free text, due vocabulary, diary-adjacent evidence, and user
/// identifiers. None of it may reach telemetry. Rather than trusting each call site, every tag goes
/// through <see cref="ActivityTag"/> or <see cref="MetricTag"/>, which reject an unknown name and
/// reject a value that is not an enum, a bool, a number, or an allow-listed tool name.
/// </para>
/// <para>
/// Two lists exist because the constraints differ. Metric tags become time-series dimensions, so
/// only bounded enum-like values are allowed. Span tags may additionally carry per-run counts,
/// which are useful on a trace and harmless there because a span is not a cardinality axis.
/// </para>
/// </remarks>
public static class CoachTelemetryTags
{
    // --- Dimensions (valid on both spans and metrics) ---

    /// <summary>The turn result, as a <see cref="CoachTurnStatus"/> name.</summary>
    public const string Outcome = "outcome";

    /// <summary>Why the turn stopped, as a <see cref="CoachStopReason"/> name.</summary>
    public const string StopReason = "stop_reason";

    /// <summary>The read-only tool that ran, from <see cref="CoachToolNames"/>.</summary>
    public const string ToolName = "tool_name";

    /// <summary>Whether the operation succeeded.</summary>
    public const string Success = "success";

    /// <summary>Which coach arm ran, as a <c>CoachImplementation</c> name.</summary>
    public const string Implementation = "implementation";

    /// <summary>A changed constraint field, as a <see cref="CoachConstraintField"/> name.</summary>
    public const string ConstraintField = "constraint_field";

    /// <summary>What caused a plan revision, as a <see cref="CoachRevisionSource"/> name.</summary>
    public const string RevisionSource = "revision_source";

    /// <summary>How a suggestion was answered, as a <see cref="CoachAcceptanceState"/> name.</summary>
    public const string Acceptance = "acceptance";

    /// <summary>Why a session was rebuilt, as a closed string from a known set.</summary>
    public const string RebuildReason = "rebuild_reason";

    // --- Span-only counts ---

    /// <summary>Model and tool iterations used by the run.</summary>
    public const string ModelIterations = "model_iterations";

    /// <summary>Tool calls made by the run.</summary>
    public const string ToolCalls = "tool_calls";

    /// <summary>Prompt tokens consumed by the run.</summary>
    public const string InputTokens = "input_tokens";

    /// <summary>Completion tokens produced by the run.</summary>
    public const string OutputTokens = "output_tokens";

    /// <summary>Estimated cost of the run, in USD.</summary>
    public const string EstimatedCostUsd = "estimated_cost_usd";

    /// <summary>Completed plan items preserved by a revision.</summary>
    public const string PreservedCompletedItems = "preserved_completed_items";

    /// <summary>Started plan items preserved by a revision.</summary>
    public const string PreservedInProgressItems = "preserved_in_progress_items";

    /// <summary>Run duration in milliseconds.</summary>
    public const string DurationMs = "duration_ms";

    /// <summary>Tag names allowed as metric dimensions. Bounded, enum-like values only.</summary>
    public static readonly IReadOnlySet<string> AllowedMetricTags = new HashSet<string>(StringComparer.Ordinal)
    {
        Outcome,
        StopReason,
        ToolName,
        Success,
        Implementation,
        ConstraintField,
        RevisionSource,
        Acceptance,
        RebuildReason
    };

    /// <summary>Tag names allowed on spans: every metric dimension plus per-run counts.</summary>
    public static readonly IReadOnlySet<string> AllowedActivityTags = new HashSet<string>(StringComparer.Ordinal)
    {
        Outcome,
        StopReason,
        ToolName,
        Success,
        Implementation,
        ConstraintField,
        RevisionSource,
        Acceptance,
        RebuildReason,
        ModelIterations,
        ToolCalls,
        InputTokens,
        OutputTokens,
        EstimatedCostUsd,
        PreservedCompletedItems,
        PreservedInProgressItems,
        DurationMs
    };

    /// <summary>Builds a validated span tag.</summary>
    /// <exception cref="InvalidOperationException">The name or the value is not allowed.</exception>
    public static KeyValuePair<string, object?> ActivityTag(string name, object? value)
        => Create(name, value, AllowedActivityTags, "span");

    /// <summary>Builds a validated metric dimension.</summary>
    /// <exception cref="InvalidOperationException">The name or the value is not allowed.</exception>
    public static KeyValuePair<string, object?> MetricTag(string name, object? value)
        => Create(name, value, AllowedMetricTags, "metric");

    private static KeyValuePair<string, object?> Create(
        string name,
        object? value,
        IReadOnlySet<string> allowed,
        string surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!allowed.Contains(name))
        {
            throw new InvalidOperationException(
                $"Coach telemetry {surface} tag '{name}' is not on the allow-list. " +
                "Add it to CoachTelemetryTags only if it is low-cardinality and carries no learner content.");
        }

        return new KeyValuePair<string, object?>(name, NormalizeValue(name, value));
    }

    /// <summary>Known low-cardinality rebuild reason values for the <see cref="RebuildReason"/> dimension.</summary>
    public static readonly IReadOnlySet<string> KnownRebuildReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "resume_success",
        "rebuild_memory_rotation",
        "deserialization_fallback",
        "policy_version_mismatch",
        "rebuild_no_row"
    };

    private static object NormalizeValue(string name, object? value) => value switch
    {
        null => throw new InvalidOperationException($"Coach telemetry tag '{name}' must not be null."),
        Enum e => e.ToString(),
        bool b => b,
        int i => i,
        long l => l,
        double d => d,
        decimal m => (double)m,
        string s when string.Equals(name, ToolName, StringComparison.Ordinal) && CoachToolNames.IsKnown(s) => s,
        string s when string.Equals(name, RebuildReason, StringComparison.Ordinal) && KnownRebuildReasons.Contains(s) => s,
        string => throw new InvalidOperationException(
            $"Coach telemetry tag '{name}' rejected a free-text string value. " +
            "Only enum names, booleans, numbers, known tool names, and known rebuild reasons are allowed."),
        _ => throw new InvalidOperationException(
            $"Coach telemetry tag '{name}' rejected a value of type {value.GetType().Name}.")
    };
}

/// <summary>
/// The closed set of read-only coach tool names. Keeping the list here bounds the cardinality of
/// the <see cref="CoachTelemetryTags.ToolName"/> dimension and stops a model-supplied string from
/// ever becoming a metric label.
/// </summary>
public static class CoachToolNames
{
    /// <summary>Languages, display language, goals, preferred session duration.</summary>
    public const string GetLearnerProfileSummary = "get_learner_profile_summary";

    /// <summary>Bounded 7/14/30-day minutes and attempts by activity type.</summary>
    public const string GetPracticeBalance = "get_practice_balance";

    /// <summary>Counts, mastery bands, lapse rates, and category tags. Never terms.</summary>
    public const string GetVocabularyDueSummary = "get_vocabulary_due_summary";

    /// <summary>Owned resource metadata, modality capabilities, counts, and last use.</summary>
    public const string GetResourceCatalog = "get_resource_catalog";

    /// <summary>The pure deterministic plan preview. Performs no write.</summary>
    public const string PreviewPracticePlan = "preview_practice_plan";

    /// <summary>The value recorded when a tool name is not recognized.</summary>
    public const string Unknown = "unknown";

    /// <summary>Every known tool name, plus <see cref="Unknown"/>.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        GetLearnerProfileSummary,
        GetPracticeBalance,
        GetVocabularyDueSummary,
        GetResourceCatalog,
        PreviewPracticePlan,
        Unknown
    };

    /// <summary>True when <paramref name="toolName"/> is a known coach tool name.</summary>
    public static bool IsKnown(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && All.Contains(toolName);

    /// <summary>Maps any input to a known tool name, falling back to <see cref="Unknown"/>.</summary>
    public static string Normalize(string? toolName)
        => IsKnown(toolName) ? toolName! : Unknown;
}
