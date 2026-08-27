using FluentAssertions;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using SentenceStudio.UnitTests.Logging;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Privacy guard for the coach plan-revision service path.
/// </summary>
/// <remarks>
/// Companion to <c>DeterministicPlanBuilderLoggingPrivacyTests</c>: that one
/// covers the generator, this one covers <see cref="PlanService"/>'s preview,
/// apply, and undo logs. Coach telemetry may never record a user or tenant id,
/// and an Aspire E2E showed the pilot learner's profile id in the dashboard.
/// </remarks>
public sealed class PlanServiceCoachRevisionLoggingPrivacyTests : IDisposable
{
    private const string PilotProfileId = "pilot-profile-id-zzz-9f3c1b";
    private const string PilotResourceId = "pilot-resource-id-zzz-4a7e2d";
    private const string PilotSkillId = "pilot-skill-id-zzz-1c8b0f";

    private static readonly string[] Secrets = [PilotProfileId, PilotResourceId, PilotSkillId];

    private readonly CoachPlanRevisionHarness _h = new();

    public PlanServiceCoachRevisionLoggingPrivacyTests() => _h.Scope.SetUser(PilotProfileId);

    public void Dispose() => _h.Dispose();

    private static readonly PlanConstraints ShortNoAudio = new() { AvailableMinutes = 10, AudioAllowed = false };

    private async Task<TodaysPlanDto> SeedPlanAsync()
    {
        _h.Generator.SetDefault(
            ("Reading", PilotResourceId, PilotSkillId, 10, 1),
            ("Listening", PilotResourceId, PilotSkillId, 10, 2));
        _h.Generator.SetConstrained(
            ("Reading", PilotResourceId, PilotSkillId, 5, 1),
            ("Cloze", PilotResourceId, PilotSkillId, 5, 2));

        return await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());
    }

    private void AssertNoProfileIdInLogs()
    {
        var offenders = _h.Logs.Entries
            .Where(e => e.AllText().Any(t =>
                Secrets.Any(secret => t.Contains(secret, StringComparison.OrdinalIgnoreCase))))
            .Select(e => $"{e.Level} [{e.Category}] {e.Message}")
            .ToList();

        offenders.Should().BeEmpty(
            "the coach plan-revision path may never log a raw profile, resource, or skill id");
    }

    [Fact]
    public async Task Preview_DoesNotLogTheRawProfileId()
    {
        await SeedPlanAsync();
        _h.Logs.Clear();

        var preview = await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        preview.Outcome.Should().Be(PlanPreviewOutcome.Success);
        AssertNoProfileIdInLogs();
    }

    [Fact]
    public async Task PreviewWithInvalidConstraints_DoesNotLogTheRawProfileId()
    {
        await SeedPlanAsync();
        _h.Logs.Clear();

        var preview = await _h.NewService().PreviewPlanAsync(new PlanConstraints { AvailableMinutes = 500 });

        preview.Outcome.Should().Be(PlanPreviewOutcome.InvalidConstraints);
        _h.Logs.Entries.Should().Contain(e => e.Message.Contains("Plan preview rejected", StringComparison.Ordinal));
        AssertNoProfileIdInLogs();
    }

    [Fact]
    public async Task ApplyAndUndo_DoNotLogTheRawProfileId()
    {
        await SeedPlanAsync();
        var original = await _h.NewService().GetTodaySnapshotAsync();
        _h.Logs.Clear();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = original.Version,
            SessionId = "session-1"
        });
        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        // No-change, stale, and undo branches all log; exercise them too.
        await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = applied.AfterPlanVersion,
            SessionId = "session-1"
        });
        await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = original.Version,
            SessionId = "session-1"
        });

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = applied.Before!,
            ExpectedPlanVersion = applied.AfterPlanVersion,
            RevisionId = "revision-1"
        });
        undo.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        _h.Logs.Entries.Should().Contain(e => e.Message.Contains("produced no change", StringComparison.Ordinal));
        _h.Logs.Entries.Should().Contain(e => e.Message.Contains("stale plan version", StringComparison.Ordinal));
        AssertNoProfileIdInLogs();
    }

    [Fact]
    public async Task ApplyWithNoPlan_DoesNotLogTheRawProfileId()
    {
        _h.Logs.Clear();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.PlanNotFound);
        _h.Logs.Entries.Should().Contain(e => e.Message.Contains("no plan exists", StringComparison.Ordinal));
        AssertNoProfileIdInLogs();
    }

    [Fact]
    public async Task LegacyGenerateToday_DoesNotLogRawIdentifiers()
    {
        _h.Logs.Clear();

        var plan = await SeedPlanAsync();

        plan.Items.Should().NotBeEmpty();
        AssertNoProfileIdInLogs();
    }

    [Fact]
    public async Task LegacyGenerateToday_WithUnknownActivityType_DoesNotLogRawIdentifiers()
    {
        _h.Generator.SetDefault(
            ("Reading", PilotResourceId, PilotSkillId, 10, 1),
            ("NotARealActivity", PilotResourceId, PilotSkillId, 10, 2));
        _h.Logs.Clear();

        var plan = await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());

        plan.Items.Should().ContainSingle("the unknown activity type is skipped");
        _h.Logs.Entries.Should().Contain(
            e => e.Message.Contains("Skipping unknown activity type", StringComparison.Ordinal));
        AssertNoProfileIdInLogs();
    }

    [Fact]
    public async Task LegacyGenerateToday_WhenGeneratorThrows_DoesNotLogRawIdentifiers()
    {
        _h.Generator.ThrowOnGenerate();
        _h.Logs.Clear();

        var plan = await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());

        plan.Items.Should().BeEmpty("a generator failure falls back to an empty plan");
        _h.Logs.Entries.Should().Contain(
            e => e.Message.Contains("Plan generator threw", StringComparison.Ordinal));
        AssertNoProfileIdInLogs();
    }

    [Fact]
    public async Task LegacyGenerateToday_WhenGeneratorThrows_DoesNotLogTheExceptionObject()
    {
        // Coach call paths reach the plan generators, and the LLM generator reaches a model
        // provider. A provider failure routinely quotes the prompt or the model's own output in
        // Exception.Message, in an inner exception, and in Exception.Data — and LogWarning(ex, ...)
        // writes all three through Exception.ToString(). Only the type name may be recorded.
        const string PromptSentinel = "PROMPT-SENTINEL-은는-4b7a";

        var inner = new InvalidOperationException($"inner echo: {PromptSentinel}");
        inner.Data["request_body"] = PromptSentinel;
        var failure = new InvalidOperationException(
            $"the response was filtered because of the prompt: {PromptSentinel}", inner);
        failure.Data["prompt"] = PromptSentinel;

        _h.Generator.ThrowOnGenerate(failure);
        _h.Logs.Clear();

        var plan = await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());

        plan.Items.Should().BeEmpty();

        var entry = _h.Logs.Entries.Should()
            .ContainSingle(e => e.Message.Contains("Plan generator threw", StringComparison.Ordinal))
            .Subject;

        // Not the rendered message, not a structured state value a sink would export, and not the
        // exception field. Asserting only on the message would pass while OpenTelemetry leaked it.
        entry.AllText().Should().NotContain(
            t => t.Contains(PromptSentinel, StringComparison.OrdinalIgnoreCase),
            "no part of the record may carry prompt or learner text");
        entry.Exception.Should().BeNull("the exception object must never reach the logger");

        // The safe fact survives, so the log is still worth reading.
        entry.State.Should().Contain(pair =>
            pair.Key == "Error" && pair.Value == nameof(InvalidOperationException));
    }

    [Fact]
    public async Task InvariantRollback_DoesNotLogTheRawProfileId()
    {
        var plan = await SeedPlanAsync();
        var completedId = plan.Items[0].Id;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, completedId, 12);

        var before = await _h.NewService().GetTodaySnapshotAsync();
        _h.Sabotage.ArmDeleteOf(_h.Row(PilotProfileId, completedId).Id);
        _h.Logs.Clear();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.ValidationFailed);
        _h.Logs.Entries.Should().Contain(e => e.Message.Contains("rolling back", StringComparison.Ordinal));
        AssertNoProfileIdInLogs();
        AssertNoLogCarriesAnException();
    }

    // ---------------------------------------------------------------- transaction control

    /// <summary>
    /// No log this service writes carries an exception object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The five transaction-control sites — no-transaction fallback, savepoint create, savepoint
    /// rollback, savepoint release, transaction rollback — used to pass the exception itself. A
    /// sink renders that through <c>Exception.ToString()</c>, which carries the message, every
    /// inner message, and <c>Exception.Data</c>. On this path those come from a provider handling
    /// a statement built from the learner's plan, so what ends up in telemetry is not a
    /// transaction fact.
    /// </para>
    /// <para>
    /// Asserted over the exception field specifically, because it is the one field the message
    /// template cannot influence: a site could carry a perfectly clean message and still export
    /// the whole exception beside it.
    /// </para>
    /// </remarks>
    private void AssertNoLogCarriesAnException()
    {
        var offenders = _h.Logs.Entries
            .Where(e => e.Exception is not null)
            .Select(e => $"{e.Level} [{e.Category}] {e.Message}")
            .ToList();

        offenders.Should().BeEmpty(
            "an exception object exports its message, its inner messages, and its Data — none of "
            + "which is a bounded transaction fact");
    }

    /// <summary>
    /// The whole revision path, driven end to end, writes no exception objects.
    /// </summary>
    [Fact]
    public async Task NoRevisionLogCarriesAnExceptionObject()
    {
        await SeedPlanAsync();
        var before = await _h.NewService().GetTodaySnapshotAsync();
        _h.Logs.Clear();

        await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version,
            SessionId = "session-1"
        });
        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        // The stale branch, which is the one that unwinds a transaction.
        await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version,
            SessionId = "session-1"
        });

        _h.Logs.Entries.Should().NotBeEmpty("the path under test has to have logged something");
        AssertNoLogCarriesAnException();
        AssertNoProfileIdInLogs();
    }

    /// <summary>
    /// The transaction-control sites do not pass an exception, checked at the source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A behavioural test can only assert about paths it can reach, and four of these five need a
    /// provider that refuses a transaction, a savepoint that fails to create, one that fails to
    /// roll back, or a connection that dies mid-rollback. Reproducing each would mean building
    /// four fault-injecting providers to protect five one-line call sites.
    /// </para>
    /// <para>
    /// The call shape is the thing that has to hold, so it is asserted directly and exhaustively:
    /// no logging call in this file may take an exception as its first argument. It is
    /// non-vacuous — the same assertion listed all five sites before they were changed — and it
    /// fails on a sixth site added later, which a behavioural test written today would not.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPlanServiceLoggingCallPassesAnException()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();

        var path = Path.Combine(
            root!.FullName, "src", "SentenceStudio.Shared", "Services", "Plans", "PlanService.cs");
        File.Exists(path).Should().BeTrue(path);

        var offenders = File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(entry =>
            {
                var trimmed = entry.Line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    return false;
                }

                return entry.Line.Contains("_logger.Log", StringComparison.Ordinal)
                    && (entry.Line.Contains("(ex,", StringComparison.Ordinal)
                        || entry.Line.Contains("(ex ,", StringComparison.Ordinal));
            })
            .Select(entry => $"line {entry.Number}: {entry.Line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty(
            "a plan-service log may carry a bounded type name, never the exception itself");
    }

    /// <summary>
    /// The sanitized sites still say something useful: a bounded exception type name.
    /// </summary>
    /// <remarks>
    /// Removing the exception is only half the fix. A message that reported nothing at all would
    /// satisfy the privacy rule and leave an operator with a savepoint failure and no idea what
    /// kind, so the replacement has to carry the type — which is a compile-time constant and can
    /// carry no learner text.
    /// </remarks>
    [Fact]
    public void TheTransactionControlSitesReportABoundedErrorType()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        var source = File.ReadAllText(Path.Combine(
            root!.FullName, "src", "SentenceStudio.Shared", "Services", "Plans", "PlanService.cs"));

        string[] sites =
        [
            "Provider does not support transactions; coach revision will not be atomic.",
            "Could not create a savepoint for the coach revision.",
            "Could not roll back to the coach revision savepoint.",
            "Could not release the coach revision savepoint.",
            "Rolling back the coach revision transaction failed."
        ];

        foreach (var site in sites)
        {
            source.Should().Contain(
                site + " Error={Error}", $"'{site}' must still name the failure kind");
        }

        // The only value any of them may pass is the type name.
        System.Text.RegularExpressions.Regex
            .Matches(source, @"Error=\{Error\}""[^;]*?;", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Should().AllSatisfy(match =>
                match.Value.Should().Contain("ex.GetType().Name"));
    }
}
