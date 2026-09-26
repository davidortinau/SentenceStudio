using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Data.AppOperations;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationProtectionTests
{
    [Fact]
    public void Ciphertext_TamperAndEveryPurposeBoundaryFailAuthentication()
    {
        var protector = new DataProtectionApplicationOperationContentProtector(
            new EphemeralDataProtectionProvider());
        var original = Context();
        var plaintext = "owner-only-payload"u8.ToArray();
        var ciphertext = protector.Protect(original, plaintext);
        var tampered = ciphertext.ToArray();
        tampered[tampered.Length / 2] ^= 0x40;
        var variants = new[]
        {
            original with { Scope = ApplicationOperationSqliteHarness.Scope("owner-b") },
            original with
            {
                Scope = ApplicationOperationSqliteHarness.Scope(
                    authority: ApplicationExecutionAuthority.NativeLocal)
            },
            original with { SubjectId = "operation-b" },
            original with { SubjectKind = ApplicationOperationContentSubjectKind.Continuation },
            original with { ContentKind = ApplicationProtectedContentKind.PriorState },
            original with { CapabilityCode = "resource.delete" },
            original with { CapabilityVersion = 2 }
        };

        protector.Unprotect(original, ciphertext, plaintext.Length).Should().Equal(plaintext);
        var tamperAct = () => protector.Unprotect(original, tampered, plaintext.Length);
        tamperAct.Should().Throw<ApplicationOperationProtectionException>();
        foreach (var variant in variants)
        {
            var swapAct = () => protector.Unprotect(variant, ciphertext, plaintext.Length);
            swapAct.Should().Throw<ApplicationOperationProtectionException>(
                $"the protected purpose changed to {variant}");
        }
    }

    [Fact]
    public void Ciphertext_ExpectedLengthMismatchFailsAndReturnsNoPlaintext()
    {
        var protector = new DataProtectionApplicationOperationContentProtector(
            new EphemeralDataProtectionProvider());
        var plaintext = "bounded-payload"u8.ToArray();
        var ciphertext = protector.Protect(Context(), plaintext);

        var act = () => protector.Unprotect(Context(), ciphertext, plaintext.Length + 1);

        act.Should().Throw<ApplicationOperationProtectionException>();
    }

    [Fact]
    public async Task Store_PersistsNoPlaintextRequestPreviewReceiptContinuationOrConfirmationSecret()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var canonical = "private-canonical-request-9197";
        var preview = "private-preview-3318";
        var confirmation = "private-confirmation-7752";
        var proposal = ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation,
            contents:
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    1,
                    Encoding.UTF8.GetBytes(canonical)),
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.ProposalPresentation,
                    1,
                    Encoding.UTF8.GetBytes(preview)),
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.PriorState,
                    1,
                    "private-prior-state-2841"u8.ToArray())
            ]);
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(proposal);
        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: "confirmation-a",
                confirmationMaterial: Encoding.UTF8.GetBytes(confirmation),
                leaseId: null,
                leaseExpiresAtUtc: null));
        var executing = (await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "confirm-decision",
                confirmationReference: "confirmation-a",
                confirmationMaterial: Encoding.UTF8.GetBytes(confirmation),
                leaseId: "confirmation-lease",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)))).Operation;
        var continuation = new ApplicationOperationContinuationWrite(
            "continuation-a",
            ApplicationContinuationWorkflow.PostReceiptResume,
            1,
            ByteAssertions.Digest("interaction"),
            ParentContinuationId: null,
            AllowsAutomaticResume: true,
            StateSchemaVersion: 1,
            ProtectedStateSource: "private-continuation-6624"u8.ToArray(),
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3),
            ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5),
            ApplicationOperationSqliteHarness.NowUtc.AddDays(1));
        await coordinator.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(
                executing,
                "confirmation-lease",
                ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3)),
            new ProfileMutationHandler(db, "owner-a", continuation: continuation));

        var forbidden = new[]
        {
            canonical,
            preview,
            confirmation,
            "private-prior-state-2841",
            "durable-receipt",
            "private-continuation-6624"
        }.Select(Encoding.UTF8.GetBytes).ToArray();
        var payloads = await db.ApplicationProtectedPayloads.AsNoTracking().ToListAsync();
        payloads.Should().HaveCount(5);
        foreach (var payload in payloads)
        {
            forbidden.Should().OnlyContain(
                value => !payload.Ciphertext.ContainsBytes(value),
                "ciphertext must not contain any plaintext source");
        }

        var confirmationRecord = await db.ApplicationOperationConfirmations.AsNoTracking().SingleAsync();
        forbidden.Should().OnlyContain(
            value => !confirmationRecord.ConfirmationDigest.ContainsBytes(value)
                     && !confirmationRecord.DecisionReferenceDigest.ContainsBytes(value));
    }

    [Fact]
    public async Task Proposal_EnforcesPlaintextAndCiphertextBounds()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var exactlyMaximum = new byte[ApplicationOperationLimits.MaximumProtectedPlaintextBytes];
        exactlyMaximum[0] = 1;
        var accepted = ApplicationOperationSqliteHarness.Proposal(
            operationId: "maximum-payload",
            contents:
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    1,
                    exactlyMaximum)
            ]);

        await coordinator.ProposeAsync(accepted);

        foreach (var invalid in new[]
                 {
                     Array.Empty<byte>(),
                     new byte[ApplicationOperationLimits.MaximumProtectedPlaintextBytes + 1]
                 })
        {
            var proposal = ApplicationOperationSqliteHarness.Proposal(
                operationId: $"invalid-{invalid.Length}",
                contents:
                [
                    new ApplicationOperationContent(
                        ApplicationProtectedContentKind.CanonicalRequest,
                        1,
                        invalid)
                ]);
            var act = async () => await coordinator.ProposeAsync(proposal);
            await act.Should().ThrowAsync<ApplicationOperationValidationException>();
        }

        var oversizedCoordinator = new ApplicationOperationCoordinator(
            new RejectingStore(),
            new OversizedCiphertextProtector());
        var oversizedAct = async () => await oversizedCoordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal(operationId: "oversized-ciphertext"));
        await oversizedAct.Should().ThrowAsync<ApplicationOperationProtectionException>();
    }

    [Fact]
    public void EventPersistenceContract_IsContentFree()
    {
        typeof(ApplicationOperationEventRecord).GetProperties()
            .Select(property => property.Name)
            .Should().BeEquivalentTo(
            [
                "Id",
                "OperationId",
                "UserProfileId",
                "Sequence",
                "Kind",
                "FromStatus",
                "ToStatus",
                "FailureCode",
                "ApplicationVersion",
                "Fence",
                "OccurredAtUtc",
                "Operation"
            ]);
        typeof(ApplicationOperationEventRecord).GetProperties()
            .Should().NotContain(property =>
                property.Name.Contains("Request", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Preview", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Prior", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Receipt", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Continuation", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    private static ApplicationOperationProtectionContext Context() =>
        new(
            ApplicationOperationSqliteHarness.Scope(),
            ApplicationOperationContentSubjectKind.Operation,
            "operation-a",
            ApplicationProtectedContentKind.CanonicalRequest,
            "resource.update",
            1);

    private sealed class OversizedCiphertextProtector : IApplicationOperationContentProtector
    {
        public byte[] Protect(
            ApplicationOperationProtectionContext context,
            ReadOnlyMemory<byte> plaintext) =>
            new byte[ApplicationOperationLimits.MaximumProtectedCiphertextBytes + 1];

        public byte[] Unprotect(
            ApplicationOperationProtectionContext context,
            ReadOnlyMemory<byte> ciphertext,
            int expectedPlaintextLength) =>
            throw new NotSupportedException();
    }

    private sealed class RejectingStore : IApplicationOperationStore
    {
        public Task<ApplicationOperationSnapshot?> FindAsync(ApplicationOperationScope scope, string operationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationOperationTransitionResult> CreateAsync(ApplicationOperationCreateRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The coordinator should reject before store invocation.");

        public Task<ApplicationOperationTransitionResult> TransitionAsync(ApplicationOperationTransitionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationOperationExecutionResult> ExecuteAsync(ApplicationOperationExecutionRequest request, IApplicationOperationHandler handler, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationOperationReceiptSnapshot?> FindReceiptAsync(ApplicationOperationScope scope, string operationId, bool isReplay, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationOperationContinuationSnapshot?> FindContinuationAsync(ApplicationOperationScope scope, string continuationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationOperationContinuationSnapshot> ResumeContinuationAsync(ApplicationOperationScope scope, string continuationId, long expectedApplicationVersion, DateTime resumedAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
