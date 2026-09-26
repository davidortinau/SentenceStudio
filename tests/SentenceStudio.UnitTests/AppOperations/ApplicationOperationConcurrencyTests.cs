using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationConcurrencyTests
{
    [Fact]
    public async Task ConcurrentAcceptAndReject_ProduceOneWinnerAndOneTypedConflict()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using (var seed = harness.NewContext())
        {
            await harness.NewCoordinator(seed).ProposeAsync(
                ApplicationOperationSqliteHarness.Proposal());
        }

        var commands = new[]
        {
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Accept,
                decisionReference: "accept-race",
                leaseId: "accept-lease"),
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Reject,
                decisionReference: "reject-race",
                leaseId: null,
                leaseExpiresAtUtc: null)
        };
        var outcomes = await Task.WhenAll(commands.Select(command => RunDecisionAsync(harness, command)));

        outcomes.Count(outcome => outcome.Result is not null).Should().Be(1);
        outcomes.Count(outcome => outcome.Exception is ApplicationOperationConflictException).Should().Be(1);

        await using var verification = harness.NewContext();
        var operation = await verification.ApplicationOperations.AsNoTracking().SingleAsync();
        operation.Status.Should().BeOneOf(
            ApplicationOperationStatus.Executing,
            ApplicationOperationStatus.Rejected);
        operation.ApplicationVersion.Should().Be(2);
        (await verification.ApplicationOperationEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentConfirmationUse_ConsumesExactlyOnce()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var secret = "race-confirmation-secret"u8.ToArray();
        await using (var seed = harness.NewContext())
        {
            var coordinator = harness.NewCoordinator(seed);
            await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
                confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation));
            await coordinator.DecideAsync(
                ApplicationOperationSqliteHarness.Decision(
                    confirmationReference: "confirmation-race",
                    confirmationMaterial: secret,
                    leaseId: null,
                    leaseExpiresAtUtc: null));
        }

        var commands = new[]
        {
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "confirm-race-a",
                confirmationReference: "confirmation-race",
                confirmationMaterial: secret,
                leaseId: "confirm-lease-a",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)),
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "confirm-race-b",
                confirmationReference: "confirmation-race",
                confirmationMaterial: secret,
                leaseId: "confirm-lease-b",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2))
        };
        var outcomes = await Task.WhenAll(commands.Select(command => RunDecisionAsync(harness, command)));

        outcomes.Count(outcome => outcome.Result is not null).Should().Be(1);
        outcomes.Count(outcome => outcome.Exception is ApplicationOperationConflictException).Should().Be(1);

        await using var verification = harness.NewContext();
        var confirmation = await verification.ApplicationOperationConfirmations.AsNoTracking().SingleAsync();
        confirmation.ConsumedAtUtc.Should().NotBeNull();
        var operation = await verification.ApplicationOperations.AsNoTracking().SingleAsync();
        operation.Status.Should().Be(ApplicationOperationStatus.Executing);
        operation.ApplicationVersion.Should().Be(3);
        operation.AttemptCount.Should().Be(1);
    }

    private static async Task<DecisionOutcome> RunDecisionAsync(
        ApplicationOperationSqliteHarness harness,
        ApplicationOperationDecisionCommand command)
    {
        await Task.Yield();
        try
        {
            await using var db = harness.NewContext();
            var result = await harness.NewCoordinator(db).DecideAsync(command);
            return new DecisionOutcome(result, null);
        }
        catch (Exception exception)
        {
            return new DecisionOutcome(null, exception);
        }
    }

    private sealed record DecisionOutcome(
        ApplicationOperationTransitionResult? Result,
        Exception? Exception);
}
