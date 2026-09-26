using FluentAssertions;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationStateMachineTests
{
    public static TheoryData<ApplicationOperationStatus, ApplicationOperationStatus, bool> AllowedTransitions =>
        new()
        {
            { ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Rejected, false },
            { ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Cancelled, false },
            { ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Expired, false },
            { ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Executing, false },
            { ApplicationOperationStatus.Proposed, ApplicationOperationStatus.AwaitingProtectedConfirmation, false },
            { ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Executing, false },
            { ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Rejected, false },
            { ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Cancelled, false },
            { ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Expired, false },
            { ApplicationOperationStatus.Executing, ApplicationOperationStatus.Executing, false },
            { ApplicationOperationStatus.Executing, ApplicationOperationStatus.Executed, true },
            { ApplicationOperationStatus.Executing, ApplicationOperationStatus.Failed, false },
            { ApplicationOperationStatus.Executed, ApplicationOperationStatus.Reversed, true }
        };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void ValidateTransition_AllowsOnlyDocumentedEdges(
        ApplicationOperationStatus current,
        ApplicationOperationStatus target,
        bool effectStarted)
    {
        var act = () => ApplicationOperationStateMachine.ValidateTransition(
            current,
            target,
            effectStarted);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateTransition_RejectsEveryUndocumentedEdgeIncludingUnknownValues()
    {
        var statuses = Enum.GetValues<ApplicationOperationStatus>()
            .Append((ApplicationOperationStatus)999)
            .ToArray();
        var allowed = AllowedTransitions
            .Select(row => (Current: (ApplicationOperationStatus)row[0],
                Target: (ApplicationOperationStatus)row[1],
                EffectStarted: (bool)row[2]))
            .ToHashSet();

        foreach (var current in statuses)
        {
            foreach (var target in statuses)
            {
                foreach (var effectStarted in new[] { false, true })
                {
                    if (allowed.Contains((current, target, effectStarted))
                        || allowed.Contains((current, target, !effectStarted))
                            && target != ApplicationOperationStatus.Failed)
                    {
                        continue;
                    }

                    var act = () => ApplicationOperationStateMachine.ValidateTransition(
                        current,
                        target,
                        effectStarted);

                    act.Should().Throw<ApplicationOperationConflictException>(
                        $"{current} -> {target} with effectStarted={effectStarted} is not documented");
                }
            }
        }
    }

    [Theory]
    [InlineData(ApplicationConfirmationPolicy.Gesture, ApplicationOperationStatus.Executing)]
    [InlineData(ApplicationConfirmationPolicy.Accept, ApplicationOperationStatus.Executing)]
    [InlineData(ApplicationConfirmationPolicy.ProtectedConfirmation, ApplicationOperationStatus.AwaitingProtectedConfirmation)]
    public void ResolveDecisionTarget_AcceptUsesDeclaredCeremony(
        ApplicationConfirmationPolicy confirmation,
        ApplicationOperationStatus expected)
    {
        ApplicationOperationStateMachine.ResolveDecisionTarget(
                ApplicationOperationStatus.Proposed,
                ApplicationOperationDecision.Accept,
                confirmation)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Reject, ApplicationOperationStatus.Rejected)]
    [InlineData(ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Cancel, ApplicationOperationStatus.Cancelled)]
    [InlineData(ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Confirm, ApplicationOperationStatus.Executing)]
    [InlineData(ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Reject, ApplicationOperationStatus.Rejected)]
    [InlineData(ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Cancel, ApplicationOperationStatus.Cancelled)]
    public void ResolveDecisionTarget_AllowsDocumentedNonAcceptDecisions(
        ApplicationOperationStatus current,
        ApplicationOperationDecision decision,
        ApplicationOperationStatus expected)
    {
        ApplicationOperationStateMachine.ResolveDecisionTarget(
                current,
                decision,
                ApplicationConfirmationPolicy.ProtectedConfirmation)
            .Should().Be(expected);
    }

    [Fact]
    public void ResolveDecisionTarget_RejectsAllOtherStatusDecisionPairs()
    {
        var allowed = new HashSet<(ApplicationOperationStatus, ApplicationOperationDecision)>
        {
            (ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Accept),
            (ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Reject),
            (ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Cancel),
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Confirm),
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Reject),
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Cancel)
        };

        foreach (var status in Enum.GetValues<ApplicationOperationStatus>()
                     .Append((ApplicationOperationStatus)999))
        {
            foreach (var decision in Enum.GetValues<ApplicationOperationDecision>()
                         .Append((ApplicationOperationDecision)999))
            {
                if (allowed.Contains((status, decision)))
                {
                    continue;
                }

                var act = () => ApplicationOperationStateMachine.ResolveDecisionTarget(
                    status,
                    decision,
                    ApplicationConfirmationPolicy.Accept);

                if (decision is ApplicationOperationDecision.Unknown
                    || !Enum.IsDefined(decision))
                {
                    act.Should().Throw<ApplicationOperationValidationException>();
                }
                else
                {
                    act.Should().Throw<ApplicationOperationConflictException>();
                }
            }
        }
    }

    [Fact]
    public void IsTerminal_ClassifiesOnlyImmutableOutcomes()
    {
        var terminal = new[]
        {
            ApplicationOperationStatus.Executed,
            ApplicationOperationStatus.Rejected,
            ApplicationOperationStatus.Cancelled,
            ApplicationOperationStatus.Expired,
            ApplicationOperationStatus.Failed,
            ApplicationOperationStatus.Reversed
        };

        foreach (var status in Enum.GetValues<ApplicationOperationStatus>())
        {
            ApplicationOperationStateMachine.IsTerminal(status)
                .Should().Be(terminal.Contains(status), status.ToString());
        }
    }

    [Theory]
    [InlineData(ApplicationExecutionAuthority.Server)]
    [InlineData(ApplicationExecutionAuthority.NativeLocal)]
    public void ValidateAuthority_AcceptsOnlyLedgerAuthorities(
        ApplicationExecutionAuthority authority)
    {
        var act = () => ApplicationOperationStateMachine.ValidateAuthority(authority);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(ApplicationExecutionAuthority.Unknown)]
    [InlineData(ApplicationExecutionAuthority.Client)]
    [InlineData(ApplicationExecutionAuthority.External)]
    [InlineData((ApplicationExecutionAuthority)999)]
    public void ValidateAuthority_RejectsEveryOtherAuthority(
        ApplicationExecutionAuthority authority)
    {
        var act = () => ApplicationOperationStateMachine.ValidateAuthority(authority);
        act.Should().Throw<ApplicationOperationValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Scope_RequiresOwner(string owner)
    {
        var act = () => new ApplicationOperationScope(
            owner,
            ApplicationExecutionAuthority.Server).Validate();

        act.Should().Throw<ApplicationOperationValidationException>();
    }
}
