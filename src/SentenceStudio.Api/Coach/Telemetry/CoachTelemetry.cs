using System.Diagnostics;
using System.Diagnostics.Metrics;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Telemetry;

/// <summary>
/// The single emission point for Learning Coach traces and metrics.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes enums, booleans, and counts — never a string that could carry learner
/// content. Tags are built through <see cref="CoachTelemetryTags"/>, which rejects an unknown name
/// or a free-text value at runtime. There is deliberately no general-purpose
/// <c>SetTag(string, object)</c> escape hatch on this type.
/// </para>
/// <para>
/// Nothing here records prompts, responses, tool arguments or results, evidence values, vocabulary
/// terms, diary or conversation content, serialized sessions, or any user, profile, tenant, or
/// email identifier. Correlation happens through the run id on the span, which is a server-generated
/// GUID with no learner meaning.
/// </para>
/// <para>
/// The source and meter names are registered with OpenTelemetry in
/// <c>SentenceStudio.ServiceDefaults</c>; the literals there must match the constants below.
/// </para>
/// </remarks>
public sealed class CoachTelemetry : IDisposable
{
    /// <summary>The coach <see cref="ActivitySource"/> name.</summary>
    public const string ActivitySourceName = "SentenceStudio.Coach";

    /// <summary>The coach <see cref="Meter"/> name.</summary>
    public const string MeterName = "SentenceStudio.Coach";

    /// <summary>The span name for one coach run.</summary>
    public const string RunActivityName = "coach.run";

    /// <summary>The span name for one read-only tool call.</summary>
    public const string ToolActivityName = "coach.tool";

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;

    private readonly Counter<long> _runs;
    private readonly Counter<long> _runsDenied;
    private readonly Histogram<double> _runDuration;
    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;
    private readonly Counter<double> _estimatedCost;
    private readonly Counter<long> _toolCalls;
    private readonly Histogram<double> _toolDuration;
    private readonly Counter<long> _constraintChanges;
    private readonly Counter<long> _planRevisions;
    private readonly Counter<long> _preservedItems;
    private readonly Counter<long> _suggestionOutcomes;
    private readonly Counter<long> _sessionRestorations;

    /// <summary>Creates the coach telemetry facade. Register as a singleton.</summary>
    public CoachTelemetry()
    {
        _activitySource = new ActivitySource(ActivitySourceName);
        _meter = new Meter(MeterName);

        _runs = _meter.CreateCounter<long>(
            "coach.runs", unit: "{run}", description: "Coach runs by outcome and stop reason.");
        _runsDenied = _meter.CreateCounter<long>(
            "coach.runs.denied", unit: "{run}", description: "Coach runs refused by a budget or concurrency limit.");
        _runDuration = _meter.CreateHistogram<double>(
            "coach.run.duration", unit: "ms", description: "Coach run duration.");
        _inputTokens = _meter.CreateCounter<long>(
            "coach.tokens.input", unit: "{token}", description: "Prompt tokens consumed by coach runs.");
        _outputTokens = _meter.CreateCounter<long>(
            "coach.tokens.output", unit: "{token}", description: "Completion tokens produced by coach runs.");
        _estimatedCost = _meter.CreateCounter<double>(
            "coach.cost.estimated", unit: "USD", description: "Estimated coach model cost.");
        _toolCalls = _meter.CreateCounter<long>(
            "coach.tool.calls", unit: "{call}", description: "Read-only coach tool calls by name and result.");
        _toolDuration = _meter.CreateHistogram<double>(
            "coach.tool.duration", unit: "ms", description: "Read-only coach tool call duration.");
        _constraintChanges = _meter.CreateCounter<long>(
            "coach.constraint.changes", unit: "{field}", description: "Constraint fields changed, by field name.");
        _planRevisions = _meter.CreateCounter<long>(
            "coach.plan.revisions", unit: "{revision}", description: "Plan revisions attempted, by source and result.");
        _preservedItems = _meter.CreateCounter<long>(
            "coach.plan.items.preserved", unit: "{item}", description: "Plan items preserved across a revision.");
        _suggestionOutcomes = _meter.CreateCounter<long>(
            "coach.suggestions", unit: "{suggestion}", description: "Suggestion answers, by acceptance state.");
        _sessionRestorations = _meter.CreateCounter<long>(
            "coach.session.restoration", unit: "{event}", description: "Session restoration events by rebuild reason.");
    }

    /// <summary>
    /// Starts the span for one coach run. Returns null when nothing is listening.
    /// </summary>
    public Activity? StartRun(CoachImplementation implementation)
    {
        var activity = _activitySource.StartActivity(RunActivityName, ActivityKind.Internal);
        if (activity is not null)
        {
            SetActivityTag(activity, CoachTelemetryTags.Implementation, implementation);
        }

        return activity;
    }

    /// <summary>
    /// Starts the span for one read-only tool call. The tool name is normalized against
    /// <see cref="CoachToolNames"/> so a model-supplied string can never reach a span.
    /// </summary>
    public Activity? StartToolCall(string? toolName)
    {
        var activity = _activitySource.StartActivity(ToolActivityName, ActivityKind.Internal);
        if (activity is not null)
        {
            SetActivityTag(activity, CoachTelemetryTags.ToolName, CoachToolNames.Normalize(toolName));
        }

        return activity;
    }

    /// <summary>
    /// Records the end of a coach run on both the span and the run metrics.
    /// </summary>
    public void RecordRunCompleted(
        Activity? activity,
        CoachImplementation implementation,
        CoachTurnStatus outcome,
        CoachStopReason stopReason,
        TimeSpan duration,
        int modelIterations,
        int toolCalls,
        CoachRunUsage usage)
    {
        var dimensions = new[]
        {
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.Implementation, implementation),
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.Outcome, outcome),
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.StopReason, stopReason)
        };

        _runs.Add(1, dimensions);
        _runDuration.Record(duration.TotalMilliseconds, dimensions);

        if (usage.InputTokens > 0)
        {
            _inputTokens.Add(usage.InputTokens, dimensions);
        }

        if (usage.OutputTokens > 0)
        {
            _outputTokens.Add(usage.OutputTokens, dimensions);
        }

        if (usage.EstimatedCostUsd > 0m)
        {
            _estimatedCost.Add((double)usage.EstimatedCostUsd, dimensions);
        }

        if (activity is null)
        {
            return;
        }

        SetActivityTag(activity, CoachTelemetryTags.Outcome, outcome);
        SetActivityTag(activity, CoachTelemetryTags.StopReason, stopReason);
        SetActivityTag(activity, CoachTelemetryTags.DurationMs, (long)duration.TotalMilliseconds);
        SetActivityTag(activity, CoachTelemetryTags.ModelIterations, modelIterations);
        SetActivityTag(activity, CoachTelemetryTags.ToolCalls, toolCalls);
        SetActivityTag(activity, CoachTelemetryTags.InputTokens, usage.InputTokens);
        SetActivityTag(activity, CoachTelemetryTags.OutputTokens, usage.OutputTokens);
        SetActivityTag(activity, CoachTelemetryTags.EstimatedCostUsd, usage.EstimatedCostUsd);

        activity.SetStatus(
            outcome == CoachTurnStatus.Completed ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    /// <summary>
    /// Records a run that never started because a budget or concurrency limit refused it.
    /// </summary>
    public void RecordRunDenied(CoachImplementation implementation, CoachStopReason stopReason)
        => _runsDenied.Add(
            1,
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.Implementation, implementation),
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.StopReason, stopReason));

    /// <summary>Records one read-only tool call and its result.</summary>
    public void RecordToolCall(string? toolName, bool success, TimeSpan duration)
    {
        var dimensions = new[]
        {
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.ToolName, CoachToolNames.Normalize(toolName)),
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.Success, success)
        };

        _toolCalls.Add(1, dimensions);
        _toolDuration.Record(duration.TotalMilliseconds, dimensions);
    }

    /// <summary>
    /// Records which constraint fields a change touched. Field names only — never the values the
    /// learner asked for.
    /// </summary>
    public void RecordConstraintChange(IReadOnlyList<CoachConstraintField> changedFields)
    {
        ArgumentNullException.ThrowIfNull(changedFields);

        for (var i = 0; i < changedFields.Count; i++)
        {
            _constraintChanges.Add(
                1,
                CoachTelemetryTags.MetricTag(CoachTelemetryTags.ConstraintField, changedFields[i]));
        }
    }

    /// <summary>Records the result of a plan revision and how much work it preserved.</summary>
    public void RecordPlanRevision(
        CoachRevisionSource source,
        bool success,
        int preservedCompletedItems,
        int preservedInProgressItems,
        Activity? activity = null)
    {
        var dimensions = new[]
        {
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.RevisionSource, source),
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.Success, success)
        };

        _planRevisions.Add(1, dimensions);

        if (success && preservedCompletedItems + preservedInProgressItems > 0)
        {
            _preservedItems.Add(preservedCompletedItems + preservedInProgressItems, dimensions);
        }

        if (activity is null)
        {
            return;
        }

        SetActivityTag(activity, CoachTelemetryTags.RevisionSource, source);
        SetActivityTag(activity, CoachTelemetryTags.Success, success);
        SetActivityTag(activity, CoachTelemetryTags.PreservedCompletedItems, preservedCompletedItems);
        SetActivityTag(activity, CoachTelemetryTags.PreservedInProgressItems, preservedInProgressItems);
    }

    /// <summary>Records how a pending suggestion was answered.</summary>
    public void RecordSuggestionOutcome(CoachAcceptanceState acceptance)
        => _suggestionOutcomes.Add(
            1,
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.Acceptance, acceptance));

    /// <summary>
    /// Records a session restoration event with a low-cardinality rebuild reason.
    /// Content-free — no conversation, learner, or session identifiers.
    /// </summary>
    public void RecordSessionRestoration(string rebuildReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rebuildReason);
        _sessionRestorations.Add(
            1,
            CoachTelemetryTags.MetricTag(CoachTelemetryTags.RebuildReason, rebuildReason));
    }

    /// <summary>Sets one allow-listed tag on a span.</summary>
    private static void SetActivityTag(Activity activity, string name, object value)
    {
        var tag = CoachTelemetryTags.ActivityTag(name, value);
        activity.SetTag(tag.Key, tag.Value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }
}
