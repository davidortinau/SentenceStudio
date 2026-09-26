using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.AppOperation;

public sealed class ApplicationContractSerializationTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Representative_proposal_round_trips()
    {
        AssertRoundTrip(new ApplicationOperationProposal
        {
            OperationId = "operation-1",
            Capability = Capability(),
            Status = ApplicationOperationStatus.Proposed,
            Effect = ApplicationEffectClass.Write,
            Confirmation = ApplicationConfirmationPolicy.Accept,
            AllowedDecisions = [ApplicationOperationDecision.Accept, ApplicationOperationDecision.Reject],
            SafeSummary = "Add a prepared vocabulary set.",
            SafePreview = ["12 terms"],
            ExpiresAtUtc = Now.AddMinutes(10)
        });
    }

    [Fact]
    public void Representative_receipt_round_trips()
    {
        AssertRoundTrip(new ApplicationOperationReceipt
        {
            ReceiptId = "receipt-1",
            OperationId = "operation-1",
            Capability = Capability(),
            Status = ApplicationOperationStatus.Executed,
            BeforeVersion = new() { DomainVersion = 20, SynchronizationVersion = 30 },
            AfterVersion = new() { DomainVersion = 21, SynchronizationVersion = 31 },
            IsReplay = true,
            Reversal = ApplicationReversalAvailability.Available,
            ReversalExpiresAtUtc = Now.AddMinutes(5),
            ClientActions =
            [
                new()
                {
                    Kind = ClientActionKind.LaunchActivity,
                    LaunchActivity = new()
                    {
                        Activity = ApplicationActivityKind.VocabularyReview,
                        LaunchReferenceId = "launch-1"
                    }
                }
            ],
            SafeSummary = "The prepared vocabulary set was added.",
            CommittedAtUtc = Now
        });
    }

    [Fact]
    public void Representative_limitation_round_trips()
    {
        AssertRoundTrip(new ApplicationLimitation
        {
            Reason = ApplicationLimitationReason.SynchronizationRequired,
            Retry = ApplicationRetryClassification.RetryAfterSynchronization,
            AlternativeCapabilities = [Capability("progress.summary.read", "progress")],
            AlternativeActions =
            [
                new()
                {
                    Kind = ClientActionKind.RefreshDomainView,
                    RefreshDomainView = new() { View = ApplicationDomainViewKind.Progress }
                }
            ],
            SafeSummary = "Synchronize the application before trying again."
        });
    }

    [Fact]
    public void Representative_continuation_round_trips()
    {
        AssertRoundTrip(new ApplicationContinuation
        {
            ContinuationId = "continuation-1",
            Workflow = ApplicationContinuationWorkflow.Clarification,
            WorkflowVersion = 1,
            State = ApplicationContinuationState.AwaitingDecision,
            AllowedNextDecisions = [ApplicationOperationDecision.Continue, ApplicationOperationDecision.Cancel],
            ExpiresAtUtc = Now.AddMinutes(10),
            SafeSummary = "Choose whether to start the activity directly."
        });
    }

    [Fact]
    public void Representative_safe_observation_round_trips_without_owner_content()
    {
        AssertRoundTrip(new CoachObservationEnvelope
        {
            Reference = new()
            {
                ObservationId = "observation-1",
                Capability = Capability("progress.summary.read", "progress"),
                ObservationVersion = 1,
                AsOfUtc = Now
            },
            State = CoachObservationState.Fresh
        });
    }

    private static void AssertRoundTrip<T>(T original)
    {
        var json = JsonSerializer.Serialize(original, WireJson.Client);
        var restored = JsonSerializer.Deserialize<T>(json, WireJson.Client);

        restored.Should().BeEquivalentTo(original);
    }

    private static ApplicationCapabilityReference Capability(
        string code = "vocabulary.catalog.read",
        string family = "vocabulary") =>
        new()
        {
            Capability = new() { Code = code, Family = family },
            Version = new() { Value = 1 }
        };
}
