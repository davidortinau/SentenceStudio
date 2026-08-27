using System.Diagnostics;
using System.Diagnostics.Metrics;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The telemetry allow-list guard. The coach handles learner text, due vocabulary, and user
/// identifiers, so the rule is not "avoid logging secrets" but "only these tag names, and only
/// enum, boolean, numeric, or known-tool-name values".
/// </summary>
/// <remarks>
/// Runs in <see cref="GlobalTelemetryListenerCollection"/> because <c>TelemetryCapture</c> below
/// registers a process-global <see cref="ActivityListener"/> / <see cref="MeterListener"/>, which
/// would otherwise capture coach activities produced by test classes running in parallel. See that
/// collection for the full explanation.
/// </remarks>
[Collection(GlobalTelemetryListenerCollection.Name)]
public class CoachTelemetryTests
{
    /// <summary>
    /// Names that must never appear as a coach tag, whatever a future change makes convenient.
    /// </summary>
    public static readonly string[] ForbiddenTagNames =
    {
        "user_id",
        "user_profile_id",
        "tenant_id",
        "email",
        "prompt",
        "response",
        "learner_text",
        "message",
        "tool_arguments",
        "tool_result",
        "evidence",
        "term",
        "vocabulary_term",
        "session",
        "session_json",
        "diary",
        "transcript"
    };

    [Fact]
    public void MetricTags_AreASubsetOfActivityTags()
        => CoachTelemetryTags.AllowedMetricTags.Should().BeSubsetOf(CoachTelemetryTags.AllowedActivityTags);

    [Fact]
    public void AllowedTags_ContainNoIdentityOrContentName()
    {
        CoachTelemetryTags.AllowedActivityTags.Should().NotIntersectWith(ForbiddenTagNames);
        CoachTelemetryTags.AllowedMetricTags.Should().NotIntersectWith(ForbiddenTagNames);
    }

    [Fact]
    public void AllowedMetricTags_AreExactlyTheApprovedDimensions()
        => CoachTelemetryTags.AllowedMetricTags.Should().BeEquivalentTo(new[]
        {
            "outcome",
            "stop_reason",
            "tool_name",
            "success",
            "implementation",
            "constraint_field",
            "revision_source",
            "acceptance",
            // Added with durable history. It is a metric dimension rather than a span-only count
            // because the whole point is to compare restoration outcomes across runs, and its
            // values are closed: NormalizeValue refuses any string outside KnownRebuildReasons,
            // so the dimension cannot be widened by a caller passing free text.
            "rebuild_reason"
        });

    [Fact]
    public void RebuildReason_RefusesAValueOutsideItsClosedSet()
    {
        var act = () => CoachTelemetryTags.MetricTag(CoachTelemetryTags.RebuildReason, "whatever_happened");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RebuildReason_AcceptsEveryKnownReason()
    {
        foreach (var reason in CoachTelemetryTags.KnownRebuildReasons)
        {
            var tag = CoachTelemetryTags.MetricTag(CoachTelemetryTags.RebuildReason, reason);
            tag.Value.Should().Be(reason);
        }
    }

    [Theory]
    [InlineData("user_profile_id")]
    [InlineData("prompt")]
    [InlineData("learner_text")]
    [InlineData("anything_else")]
    public void ActivityTag_RejectsAnUnknownName(string name)
    {
        var act = () => CoachTelemetryTags.ActivityTag(name, 1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*allow-list*");
    }

    [Fact]
    public void MetricTag_RejectsASpanOnlyCountName()
    {
        var act = () => CoachTelemetryTags.MetricTag(CoachTelemetryTags.InputTokens, 10L);

        act.Should().Throw<InvalidOperationException>("per-run counts must not become metric dimensions");
    }

    [Fact]
    public void ActivityTag_AcceptsASpanOnlyCountName()
        => CoachTelemetryTags.ActivityTag(CoachTelemetryTags.InputTokens, 10L).Value.Should().Be(10L);

    [Fact]
    public void Tag_RejectsFreeText()
    {
        var act = () => CoachTelemetryTags.MetricTag(CoachTelemetryTags.Outcome, "the learner said hello");

        act.Should().Throw<InvalidOperationException>().WithMessage("*free-text*");
    }

    [Fact]
    public void Tag_RejectsAnUnknownToolName()
    {
        var act = () => CoachTelemetryTags.MetricTag(CoachTelemetryTags.ToolName, "write_plan_directly");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Tag_AcceptsAKnownToolName()
        => CoachTelemetryTags.MetricTag(CoachTelemetryTags.ToolName, CoachToolNames.PreviewPracticePlan)
            .Value.Should().Be(CoachToolNames.PreviewPracticePlan);

    [Fact]
    public void Tag_RejectsNull()
    {
        var act = () => CoachTelemetryTags.MetricTag(CoachTelemetryTags.Success, null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Tag_RejectsAnArbitraryObject()
    {
        var act = () => CoachTelemetryTags.ActivityTag(CoachTelemetryTags.Outcome, new { Secret = "value" });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Tag_ConvertsEnumsToNamesAndDecimalsToDouble()
    {
        CoachTelemetryTags.MetricTag(CoachTelemetryTags.StopReason, CoachStopReason.Timeout)
            .Value.Should().Be("Timeout");
        CoachTelemetryTags.ActivityTag(CoachTelemetryTags.EstimatedCostUsd, 0.25m)
            .Value.Should().Be(0.25d);
    }

    [Fact]
    public void ToolNames_NormalizeUnknownInputToTheUnknownSentinel()
    {
        CoachToolNames.Normalize("get_practice_balance").Should().Be(CoachToolNames.GetPracticeBalance);
        CoachToolNames.Normalize("DROP TABLE plans").Should().Be(CoachToolNames.Unknown);
        CoachToolNames.Normalize(null).Should().Be(CoachToolNames.Unknown);
        CoachToolNames.IsKnown("get_learner_profile_summary").Should().BeTrue();
    }

    [Fact]
    public void EmittedSpanAndMetricTags_StayInsideTheAllowList()
    {
        using var capture = new TelemetryCapture();
        using var telemetry = new CoachTelemetry();

        using (var runActivity = telemetry.StartRun(CoachImplementation.Baseline))
        {
            using (var toolActivity = telemetry.StartToolCall(CoachToolNames.GetPracticeBalance))
            {
                telemetry.RecordToolCall(CoachToolNames.GetPracticeBalance, success: true, TimeSpan.FromMilliseconds(12));
            }

            telemetry.RecordConstraintChange(new[]
            {
                CoachConstraintField.AvailableMinutes,
                CoachConstraintField.AudioAllowed
            });
            telemetry.RecordPlanRevision(CoachRevisionSource.DirectRequest, success: true, 2, 1, runActivity);
            telemetry.RecordSuggestionOutcome(CoachAcceptanceState.Accepted);
            telemetry.RecordRunCompleted(
                runActivity,
                CoachImplementation.Baseline,
                CoachTurnStatus.Completed,
                CoachStopReason.Completed,
                TimeSpan.FromSeconds(3),
                modelIterations: 2,
                toolCalls: 1,
                new CoachRunUsage(900, 300, 0.011m));
        }

        telemetry.RecordRunDenied(CoachImplementation.Baseline, CoachStopReason.RateLimit);

        capture.ActivityTagNames.Should().NotBeEmpty();
        capture.ActivityTagNames.Should().BeSubsetOf(CoachTelemetryTags.AllowedActivityTags);
        capture.MetricTagNames.Should().NotBeEmpty();
        capture.MetricTagNames.Should().BeSubsetOf(CoachTelemetryTags.AllowedMetricTags);
        capture.TagValues.Should().OnlyContain(v => IsLowCardinalityValue(v));
    }

    [Fact]
    public void RunSpan_CarriesOutcomeStopReasonAndCounts()
    {
        using var capture = new TelemetryCapture();
        using var telemetry = new CoachTelemetry();

        using (var runActivity = telemetry.StartRun(CoachImplementation.Harness))
        {
            telemetry.RecordRunCompleted(
                runActivity,
                CoachImplementation.Harness,
                CoachTurnStatus.Incomplete,
                CoachStopReason.IterationLimit,
                TimeSpan.FromSeconds(45),
                modelIterations: 6,
                toolCalls: 4,
                new CoachRunUsage(1_500, 1_200, 0.03m));
        }

        var run = capture.Activities.Single(a => a.OperationName == CoachTelemetry.RunActivityName);
        run.GetTagItem(CoachTelemetryTags.Implementation).Should().Be("Harness");
        run.GetTagItem(CoachTelemetryTags.Outcome).Should().Be("Incomplete");
        run.GetTagItem(CoachTelemetryTags.StopReason).Should().Be("IterationLimit");
        run.GetTagItem(CoachTelemetryTags.ModelIterations).Should().Be(6);
        run.GetTagItem(CoachTelemetryTags.ToolCalls).Should().Be(4);
        run.GetTagItem(CoachTelemetryTags.InputTokens).Should().Be(1_500L);
        run.GetTagItem(CoachTelemetryTags.OutputTokens).Should().Be(1_200L);
        run.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public void ToolSpan_NormalizesAModelSuppliedName()
    {
        using var capture = new TelemetryCapture();
        using var telemetry = new CoachTelemetry();

        using (telemetry.StartToolCall("delete_everything; --learner said so")) { }

        var tool = capture.Activities.Single(a => a.OperationName == CoachTelemetry.ToolActivityName);
        tool.GetTagItem(CoachTelemetryTags.ToolName).Should().Be(CoachToolNames.Unknown);
    }

    [Fact]
    public void RecordToolCall_WithAnUnknownName_DoesNotThrowAndRecordsTheSentinel()
    {
        using var capture = new TelemetryCapture();
        using var telemetry = new CoachTelemetry();

        telemetry.RecordToolCall("something the model made up", success: false, TimeSpan.FromMilliseconds(5));

        capture.MetricTagNames.Should().BeSubsetOf(CoachTelemetryTags.AllowedMetricTags);
        capture.TagValues.Should().Contain(CoachToolNames.Unknown);
    }

    [Fact]
    public void TelemetryNames_MatchTheDocumentedSourceAndMeter()
    {
        CoachTelemetry.ActivitySourceName.Should().Be("SentenceStudio.Coach");
        CoachTelemetry.MeterName.Should().Be("SentenceStudio.Coach");
    }

    private static bool IsLowCardinalityValue(object? value) => value switch
    {
        bool or int or long or double => true,
        string s => IsEnumName(s) || CoachToolNames.IsKnown(s),
        _ => false
    };

    private static bool IsEnumName(string value)
        => Enum.GetNames<CoachTurnStatus>().Contains(value)
           || Enum.GetNames<CoachStopReason>().Contains(value)
           || Enum.GetNames<CoachConstraintField>().Contains(value)
           || Enum.GetNames<CoachRevisionSource>().Contains(value)
           || Enum.GetNames<CoachAcceptanceState>().Contains(value)
           || Enum.GetNames<CoachImplementation>().Contains(value);

    /// <summary>
    /// Listens to the coach <see cref="ActivitySource"/> and <see cref="Meter"/> and records every
    /// tag name and value that was actually emitted.
    /// </summary>
    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;
        private readonly object _gate = new();

        public TelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CoachTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = OnActivityStopped
            };

            // PROCESS-GLOBAL. AddActivityListener has no per-instance scope and the only filter
            // available is the source NAME, so this listener sees coach activities started by any
            // test running concurrently — not just this one. That is why CoachTelemetryTests lives
            // in GlobalTelemetryListenerCollection (DisableParallelization). Removing that attribute
            // reintroduces a ~1-in-10 "Sequence contains more than one matching element" failure on
            // the .Single(...) assertions below.
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener();
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CoachTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) => OnMeasurement(tags));
            _meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) => OnMeasurement(tags));
            _meterListener.Start();
        }

        public List<Activity> Activities { get; } = new();
        public HashSet<string> ActivityTagNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MetricTagNames { get; } = new(StringComparer.Ordinal);
        public List<object?> TagValues { get; } = new();

        private void OnActivityStopped(Activity activity)
        {
            lock (_gate)
            {
                Activities.Add(activity);
                foreach (var tag in activity.TagObjects)
                {
                    ActivityTagNames.Add(tag.Key);
                    TagValues.Add(tag.Value);
                }
            }
        }

        private void OnMeasurement(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_gate)
            {
                foreach (var tag in tags)
                {
                    MetricTagNames.Add(tag.Key);
                    TagValues.Add(tag.Value);
                }
            }
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }
    }
}
