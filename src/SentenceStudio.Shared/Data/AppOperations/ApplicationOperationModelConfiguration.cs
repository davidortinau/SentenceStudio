using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Data.AppOperations;

internal static class ApplicationOperationModelConfiguration
{
    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter = new(
        value => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime(),
        value => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());

    internal static void ConfigureApplicationOperations(this ModelBuilder modelBuilder)
    {
        ConfigureOperation(modelBuilder);
        ConfigureProtectedPayload(modelBuilder);
        ConfigureConfirmation(modelBuilder);
        ConfigureReceipt(modelBuilder);
        ConfigureContinuation(modelBuilder);
        ConfigureEvent(modelBuilder);
        ConfigureUtcTimestamps(modelBuilder);
    }

    private static void ConfigureUtcTimestamps(ModelBuilder modelBuilder)
    {
        var operationRecordNamespace = typeof(ApplicationOperationRecord).Namespace;
        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .Where(entity => entity.ClrType.Namespace == operationRecordNamespace)
                     .SelectMany(entity => entity.GetProperties())
                     .Where(property =>
                         property.ClrType == typeof(DateTime)
                         || property.ClrType == typeof(DateTime?)))
        {
            property.SetValueConverter(UtcDateTimeConverter);
        }
    }

    private static void ConfigureOperation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApplicationOperationRecord>();
        entity.ToTable("ApplicationOperation", table =>
        {
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Authority",
                "\"Authority\" IN ('NativeLocal','Server')");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Effect",
                "\"Effect\" IN ('Write','Launch','Composite')");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Confirmation",
                "\"Confirmation\" IN ('Gesture','Accept','ProtectedConfirmation')");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Capability",
                "\"CapabilityVersion\" > 0 AND length(\"CapabilityCode\") > 0 AND length(\"CapabilityFamily\") > 0 AND length(\"CapabilityFingerprint\") = 71");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Status",
                "\"Status\" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed')");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Versions",
                "\"ExpectedDomainVersion\" >= 0 AND \"ExpectedSynchronizationVersion\" >= 0 AND \"ApplicationVersion\" > 0 AND \"Fence\" >= 0 AND \"AttemptCount\" >= 0");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Lifecycle",
                "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"PurgeAfterUtc\" >= \"CreatedAtUtc\" AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_IdempotencyDigest",
                "\"IdempotencyDigest\" IS NULL OR length(\"IdempotencyDigest\") = 32");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_CanonicalRequestDigest",
                "length(\"CanonicalRequestDigest\") = 32");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Decision",
                "(\"DecisionReferenceDigest\" IS NULL AND \"Decision\" IS NULL) OR (length(\"DecisionReferenceDigest\") = 32 AND \"Decision\" IN ('Accept','Reject','Cancel','Confirm'))");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_ParentFence",
                "(\"ParentOperationId\" IS NULL AND \"ParentApplicationVersion\" IS NULL AND \"ParentFence\" IS NULL) OR (\"ParentOperationId\" IS NOT NULL AND \"ParentOperationId\" <> \"Id\" AND \"ParentApplicationVersion\" > 0 AND \"ParentFence\" >= 0)");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Lease",
                "(\"Status\" = 'Executing' AND \"LeaseId\" IS NOT NULL AND \"LeaseExpiresAtUtc\" IS NOT NULL AND \"AttemptCount\" > 0) OR (\"Status\" <> 'Executing' AND \"LeaseId\" IS NULL AND \"LeaseExpiresAtUtc\" IS NULL)");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_Terminal",
                "(\"Status\" IN ('Executed','Rejected','Cancelled','Expired','Failed','Reversed') AND \"TerminalAtUtc\" >= \"CreatedAtUtc\") OR (\"Status\" IN ('Proposed','AwaitingProtectedConfirmation','Executing') AND \"TerminalAtUtc\" IS NULL)");
            table.HasCheckConstraint(
                "CK_ApplicationOperation_PayloadPurge",
                "\"PayloadPurgedAtUtc\" IS NULL OR (\"TerminalAtUtc\" IS NOT NULL AND \"PayloadPurgedAtUtc\" >= \"TerminalAtUtc\")");
        });

        entity.HasKey(operation => operation.Id);
        entity.Property(operation => operation.Id)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength)
            .ValueGeneratedNever();
        entity.Property(operation => operation.UserProfileId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOwnerIdLength);
        entity.Property(operation => operation.Authority)
            .HasConversion<string>()
            .HasMaxLength(16);
        entity.Property(operation => operation.CapabilityCode)
            .HasMaxLength(ApplicationOperationLimits.MaximumCapabilityCodeLength);
        entity.Property(operation => operation.CapabilityFamily)
            .HasMaxLength(ApplicationOperationLimits.MaximumCapabilityFamilyLength);
        entity.Property(operation => operation.CapabilityFingerprint)
            .HasMaxLength(ApplicationOperationLimits.MaximumFingerprintLength);
        entity.Property(operation => operation.Effect)
            .HasConversion<string>()
            .HasMaxLength(16);
        entity.Property(operation => operation.Confirmation)
            .HasConversion<string>()
            .HasMaxLength(32);
        entity.Property(operation => operation.Status)
            .HasConversion<string>()
            .HasMaxLength(32);
        entity.Property(operation => operation.ParentOperationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);
        entity.Property(operation => operation.IdempotencyDigest)
            .HasMaxLength(ApplicationOperationLimits.Sha256DigestBytes);
        entity.Property(operation => operation.CanonicalRequestDigest)
            .HasMaxLength(ApplicationOperationLimits.Sha256DigestBytes);
        entity.Property(operation => operation.DecisionReferenceDigest)
            .HasMaxLength(ApplicationOperationLimits.Sha256DigestBytes);
        entity.Property(operation => operation.Decision)
            .HasConversion<string>()
            .HasMaxLength(16);
        entity.Property(operation => operation.ApplicationVersion)
            .IsConcurrencyToken();
        entity.Property(operation => operation.LeaseId)
            .HasMaxLength(ApplicationOperationLimits.MaximumReferenceIdLength);

        entity.HasIndex(operation => new
        {
            operation.UserProfileId,
            operation.Status,
            operation.ExpiresAtUtc
        });
        entity.HasIndex(operation => new
        {
            operation.UserProfileId,
            operation.PurgeAfterUtc
        });
        entity.HasIndex(operation => new
        {
            operation.UserProfileId,
            operation.Authority,
            operation.CapabilityCode,
            operation.CapabilityVersion,
            operation.IdempotencyDigest
        })
            .IsUnique()
            .HasFilter("\"IdempotencyDigest\" IS NOT NULL");
        entity.HasIndex(operation => new
        {
            operation.UserProfileId,
            operation.Authority,
            operation.IdempotencyDigest
        })
            .IsUnique()
            .HasFilter("\"IdempotencyDigest\" IS NOT NULL");
        entity.HasIndex(operation => operation.ParentOperationId)
            .IsUnique()
            .HasFilter("\"ParentOperationId\" IS NOT NULL");
        entity.HasIndex(operation => new { operation.Status, operation.LeaseExpiresAtUtc });
        entity.HasIndex(operation => new
        {
            operation.UserProfileId,
            operation.Authority,
            operation.DecisionReferenceDigest
        })
            .HasFilter("\"DecisionReferenceDigest\" IS NOT NULL");

        entity.HasOne(operation => operation.ParentOperation)
            .WithMany(operation => operation.ReversalOperations)
            .HasForeignKey(operation => operation.ParentOperationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProtectedPayload(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApplicationProtectedPayloadRecord>();
        entity.ToTable("ApplicationProtectedPayload", table =>
        {
            table.HasCheckConstraint(
                "CK_ApplicationProtectedPayload_Subject",
                "(\"SubjectKind\" = 'Operation' AND \"SubjectId\" = \"OperationId\") OR \"SubjectKind\" = 'Continuation'");
            table.HasCheckConstraint(
                "CK_ApplicationProtectedPayload_Content",
                "\"ContentKind\" IN ('CanonicalRequest','ProposalPresentation','PriorState','Receipt','ContinuationState')");
            table.HasCheckConstraint(
                "CK_ApplicationProtectedPayload_Versions",
                "\"ProtectionVersion\" = 1 AND \"SchemaVersion\" > 0");
            table.HasCheckConstraint(
                "CK_ApplicationProtectedPayload_Length",
                "\"PlaintextLength\" > 0 AND \"PlaintextLength\" <= 1048576 AND length(\"Ciphertext\") > 0 AND length(\"Ciphertext\") <= 1114112");
            table.HasCheckConstraint(
                "CK_ApplicationProtectedPayload_Lifecycle",
                "\"PurgeAfterUtc\" >= \"CreatedAtUtc\"");
        });

        entity.HasKey(payload => payload.Id);
        entity.Property(payload => payload.Id)
            .HasMaxLength(ApplicationOperationLimits.MaximumReferenceIdLength)
            .ValueGeneratedNever();
        entity.Property(payload => payload.OperationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);
        entity.Property(payload => payload.UserProfileId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOwnerIdLength);
        entity.Property(payload => payload.SubjectKind)
            .HasConversion<string>()
            .HasMaxLength(16);
        entity.Property(payload => payload.SubjectId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);
        entity.Property(payload => payload.ContentKind)
            .HasConversion<string>()
            .HasMaxLength(32);
        entity.Property(payload => payload.Ciphertext)
            .HasMaxLength(ApplicationOperationLimits.MaximumProtectedCiphertextBytes);

        entity.HasIndex(payload => new
        {
            payload.SubjectKind,
            payload.SubjectId,
            payload.ContentKind
        }).IsUnique();
        entity.HasIndex(payload => new { payload.UserProfileId, payload.PurgeAfterUtc });
        entity.HasOne(payload => payload.Operation)
            .WithMany(operation => operation.ProtectedPayloads)
            .HasForeignKey(payload => payload.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureConfirmation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApplicationOperationConfirmationRecord>();
        entity.ToTable("ApplicationOperationConfirmation", table =>
        {
            table.HasCheckConstraint(
                "CK_ApplicationOperationConfirmation_Digests",
                "length(\"ConfirmationDigest\") = 32 AND length(\"DecisionReferenceDigest\") = 32");
            table.HasCheckConstraint(
                "CK_ApplicationOperationConfirmation_Lifecycle",
                "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND (\"ConsumedAtUtc\" IS NULL OR (\"ConsumedAtUtc\" >= \"CreatedAtUtc\" AND \"ConsumedAtUtc\" <= \"ExpiresAtUtc\")) AND (\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"CreatedAtUtc\")");
            table.HasCheckConstraint(
                "CK_ApplicationOperationConfirmation_Consumption",
                "(\"ConsumedAtUtc\" IS NULL AND \"ConsumedApplicationVersion\" IS NULL AND \"ConsumedFence\" IS NULL) OR (\"ConsumedAtUtc\" IS NOT NULL AND \"ConsumedApplicationVersion\" > 0 AND \"ConsumedFence\" >= 0)");
        });

        entity.HasKey(confirmation => confirmation.Id);
        entity.Property(confirmation => confirmation.Id)
            .HasMaxLength(ApplicationOperationLimits.MaximumReferenceIdLength)
            .ValueGeneratedNever();
        entity.Property(confirmation => confirmation.OperationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);
        entity.Property(confirmation => confirmation.UserProfileId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOwnerIdLength);
        entity.Property(confirmation => confirmation.ConfirmationDigest)
            .HasMaxLength(ApplicationOperationLimits.Sha256DigestBytes);
        entity.Property(confirmation => confirmation.DecisionReferenceDigest)
            .HasMaxLength(ApplicationOperationLimits.Sha256DigestBytes);

        entity.HasIndex(confirmation => new
        {
            confirmation.OperationId,
            confirmation.ConfirmationDigest
        }).IsUnique();
        entity.HasIndex(confirmation => confirmation.OperationId)
            .IsUnique()
            .HasFilter("\"ConsumedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL");
        entity.HasIndex(confirmation => new
        {
            confirmation.UserProfileId,
            confirmation.ExpiresAtUtc
        });
        entity.HasOne(confirmation => confirmation.Operation)
            .WithMany(operation => operation.Confirmations)
            .HasForeignKey(confirmation => confirmation.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureReceipt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApplicationOperationReceiptRecord>();
        entity.ToTable("ApplicationOperationReceipt", table =>
        {
            table.HasCheckConstraint(
                "CK_ApplicationOperationReceipt_Versions",
                "\"ReceiptVersion\" > 0 AND \"BeforeDomainVersion\" >= 0 AND \"BeforeSynchronizationVersion\" >= 0 AND \"AfterDomainVersion\" >= \"BeforeDomainVersion\" AND \"AfterSynchronizationVersion\" >= \"BeforeSynchronizationVersion\"");
            table.HasCheckConstraint(
                "CK_ApplicationOperationReceipt_Reversal",
                "\"Reversal\" IN ('Unavailable','Available','Expired','Completed')");
            table.HasCheckConstraint(
                "CK_ApplicationOperationReceipt_ReversalState",
                "(\"Reversal\" = 'Available' AND \"ReversalExpiresAtUtc\" IS NOT NULL) OR (\"Reversal\" = 'Completed' AND \"ReversalOperationId\" IS NOT NULL) OR (\"Reversal\" IN ('Unavailable','Expired') AND \"ReversalOperationId\" IS NULL)");
        });

        entity.HasKey(receipt => receipt.Id);
        entity.Property(receipt => receipt.Id)
            .HasMaxLength(ApplicationOperationLimits.MaximumReferenceIdLength)
            .ValueGeneratedNever();
        entity.Property(receipt => receipt.OperationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);
        entity.Property(receipt => receipt.UserProfileId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOwnerIdLength);
        entity.Property(receipt => receipt.Reversal)
            .HasConversion<string>()
            .HasMaxLength(16);
        entity.Property(receipt => receipt.ReversalOperationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);

        entity.HasIndex(receipt => receipt.OperationId).IsUnique();
        entity.HasIndex(receipt => new { receipt.UserProfileId, receipt.CommittedAtUtc });
        entity.HasIndex(receipt => receipt.ReversalOperationId)
            .IsUnique()
            .HasFilter("\"ReversalOperationId\" IS NOT NULL");
        entity.HasOne(receipt => receipt.Operation)
            .WithOne(operation => operation.Receipt)
            .HasForeignKey<ApplicationOperationReceiptRecord>(receipt => receipt.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(receipt => receipt.ReversalOperation)
            .WithOne()
            .HasForeignKey<ApplicationOperationReceiptRecord>(receipt => receipt.ReversalOperationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureContinuation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApplicationOperationContinuationRecord>();
        entity.ToTable("ApplicationOperationContinuation", table =>
        {
            table.HasCheckConstraint(
                "CK_ApplicationOperationContinuation_Workflow",
                "\"Workflow\" IN ('Clarification','PostReceiptResume')");
            table.HasCheckConstraint(
                "CK_ApplicationOperationContinuation_State",
                "\"State\" IN ('AwaitingDecision','ReadyToResume','Completed','Expired','Cancelled')");
            table.HasCheckConstraint(
                "CK_ApplicationOperationContinuation_Digest",
                "length(\"InteractionScopeDigest\") = 32");
            table.HasCheckConstraint(
                "CK_ApplicationOperationContinuation_Resume",
                "\"WorkflowVersion\" > 0 AND \"AutomaticResumeCount\" >= 0 AND \"AutomaticResumeCount\" <= 1 AND \"ApplicationVersion\" > 0 AND (\"AllowsAutomaticResume\" = TRUE OR \"AutomaticResumeCount\" = 0) AND (\"ParentContinuationId\" IS NULL OR (\"AllowsAutomaticResume\" = FALSE AND \"AutomaticResumeCount\" = 0)) AND ((\"AutomaticResumeCount\" = 0 AND \"ResumedAtUtc\" IS NULL) OR (\"AutomaticResumeCount\" = 1 AND \"ResumedAtUtc\" IS NOT NULL))");
            table.HasCheckConstraint(
                "CK_ApplicationOperationContinuation_Lifecycle",
                "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"PurgeAfterUtc\" >= \"ExpiresAtUtc\" AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
        });

        entity.HasKey(continuation => continuation.Id);
        entity.Property(continuation => continuation.Id)
            .HasMaxLength(ApplicationOperationLimits.MaximumReferenceIdLength)
            .ValueGeneratedNever();
        entity.Property(continuation => continuation.OperationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);
        entity.Property(continuation => continuation.UserProfileId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOwnerIdLength);
        entity.Property(continuation => continuation.Workflow)
            .HasConversion<string>()
            .HasMaxLength(24);
        entity.Property(continuation => continuation.State)
            .HasConversion<string>()
            .HasMaxLength(24);
        entity.Property(continuation => continuation.InteractionScopeDigest)
            .HasMaxLength(ApplicationOperationLimits.Sha256DigestBytes);
        entity.Property(continuation => continuation.ParentContinuationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumReferenceIdLength);
        entity.Property(continuation => continuation.ApplicationVersion)
            .IsConcurrencyToken();

        entity.HasIndex(continuation => new
        {
            continuation.UserProfileId,
            continuation.InteractionScopeDigest
        })
            .IsUnique()
            .HasFilter("\"State\" IN ('AwaitingDecision','ReadyToResume')");
        entity.HasIndex(continuation => new
        {
            continuation.UserProfileId,
            continuation.ExpiresAtUtc
        });
        entity.HasOne(continuation => continuation.Operation)
            .WithMany(operation => operation.Continuations)
            .HasForeignKey(continuation => continuation.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(continuation => continuation.ParentContinuation)
            .WithMany(continuation => continuation.ChildContinuations)
            .HasForeignKey(continuation => continuation.ParentContinuationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureEvent(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApplicationOperationEventRecord>();
        entity.ToTable("ApplicationOperationEvent", table =>
        {
            table.HasCheckConstraint(
                "CK_ApplicationOperationEvent_Sequence",
                "\"Sequence\" > 0 AND \"ApplicationVersion\" > 0 AND \"Fence\" >= 0");
            table.HasCheckConstraint(
                "CK_ApplicationOperationEvent_Kind",
                "\"Kind\" IN ('Proposed','AwaitingProtectedConfirmation','ExecutionClaimed','LeaseRecovered','Executed','Rejected','Cancelled','Expired','Failed','ReversalLinked','Reversed','ContinuationCreated','ContinuationResumed')");
            table.HasCheckConstraint(
                "CK_ApplicationOperationEvent_Status",
                "(\"FromStatus\" IS NULL OR \"FromStatus\" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed')) AND \"ToStatus\" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed')");
            table.HasCheckConstraint(
                "CK_ApplicationOperationEvent_Failure",
                "(\"Kind\" = 'Failed' AND \"FailureCode\" IN ('InvalidProtectedContent','InvalidCanonicalRequest','CapabilityUnavailable','AuthorizationDenied','StaleDomainVersion','StaleSynchronizationVersion','PreEffectHandlerFailure')) OR (\"Kind\" <> 'Failed' AND \"FailureCode\" IS NULL)");
        });

        entity.HasKey(operationEvent => operationEvent.Id);
        entity.Property(operationEvent => operationEvent.Id)
            .HasMaxLength(ApplicationOperationLimits.MaximumReferenceIdLength)
            .ValueGeneratedNever();
        entity.Property(operationEvent => operationEvent.OperationId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOperationIdLength);
        entity.Property(operationEvent => operationEvent.UserProfileId)
            .HasMaxLength(ApplicationOperationLimits.MaximumOwnerIdLength);
        entity.Property(operationEvent => operationEvent.Kind)
            .HasConversion<string>()
            .HasMaxLength(32);
        entity.Property(operationEvent => operationEvent.FromStatus)
            .HasConversion<string>()
            .HasMaxLength(32);
        entity.Property(operationEvent => operationEvent.ToStatus)
            .HasConversion<string>()
            .HasMaxLength(32);
        entity.Property(operationEvent => operationEvent.FailureCode)
            .HasConversion<string>()
            .HasMaxLength(ApplicationOperationLimits.MaximumFailureCodeLength);

        entity.HasIndex(operationEvent => new
        {
            operationEvent.OperationId,
            operationEvent.Sequence
        }).IsUnique();
        entity.HasIndex(operationEvent => new
        {
            operationEvent.UserProfileId,
            operationEvent.OccurredAtUtc
        });
        entity.HasOne(operationEvent => operationEvent.Operation)
            .WithMany(operation => operation.Events)
            .HasForeignKey(operationEvent => operationEvent.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
