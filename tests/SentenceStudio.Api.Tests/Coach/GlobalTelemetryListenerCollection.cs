using Xunit;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Serializes every test class that installs a <b>process-global</b> telemetry listener.
/// </summary>
/// <remarks>
/// <para>
/// <c>ActivitySource.AddActivityListener</c> and <c>MeterListener.Start</c> are process-wide by
/// design. There is no per-instance or per-test scoping in the .NET diagnostics API: a listener
/// registered by one test observes activities and measurements produced by <i>every</i> test
/// running at that moment, and the only filter available is the source/meter <b>name</b>.
/// </para>
/// <para>
/// That is a real race, not a theoretical one. <c>CoachTelemetryTests.TelemetryCapture</c> listens
/// to the <c>SentenceStudio.Coach</c> ActivitySource, and ten other coach test classes
/// (<c>CoachSessionServiceTests</c>, <c>BaselineLearningCoachTests</c>,
/// <c>CoachAgentOutputRecoveryTests</c>, <c>CoachArmSelectionTests</c>, and friends) start coach
/// run activities on that same source. With xUnit's default cross-collection parallelism, one of
/// their activities lands in the capture belonging to a telemetry test, and assertions of the form
/// <c>capture.Activities.Single(a =&gt; a.OperationName == RunActivityName)</c> throw
/// "Sequence contains more than one matching element". Measured failure rate before this
/// collection existed: 1 in 10 full Api.Tests runs.
/// </para>
/// <para>
/// <c>DisableParallelization</c> makes xUnit run this collection on its own, never alongside
/// another collection, which closes the window. Per-capture correlation was considered and
/// rejected: distinguishing "my" activity from another test's would require a discriminating tag
/// emitted by <c>CoachTelemetry</c> itself, and the tag allow-list these very tests enforce
/// forbids adding one — so the only alternative was a production change to test-only benefit.
/// </para>
/// <para>
/// Join this collection from any future test class that registers an <c>ActivityListener</c> or
/// <c>MeterListener</c>. Do not join it for merely asserting on telemetry types or names — that
/// needs no global state, and needlessly serializing tests slows the suite for everyone.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalTelemetryListenerCollection
{
    public const string Name = "Global telemetry listeners";
}
