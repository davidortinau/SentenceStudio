using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationOperationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationOperation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Authority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CapabilityCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    CapabilityFamily = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapabilityVersion = table.Column<int>(type: "integer", nullable: false),
                    CapabilityFingerprint = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Confirmation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ParentOperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ParentApplicationVersion = table.Column<long>(type: "bigint", nullable: true),
                    ParentFence = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    CanonicalRequestDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    DecisionReferenceDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ExpectedDomainVersion = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedSynchronizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    LeaseId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PurgeAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TerminalAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PayloadPurgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationOperation", x => x.Id);
                    table.CheckConstraint("CK_ApplicationOperation_Authority", "\"Authority\" IN ('NativeLocal','Server')");
                    table.CheckConstraint("CK_ApplicationOperation_Capability", "\"CapabilityVersion\" > 0 AND length(\"CapabilityCode\") > 0 AND length(\"CapabilityFamily\") > 0 AND length(\"CapabilityFingerprint\") = 71");
                    table.CheckConstraint("CK_ApplicationOperation_CanonicalRequestDigest", "length(\"CanonicalRequestDigest\") = 32");
                    table.CheckConstraint("CK_ApplicationOperation_Confirmation", "\"Confirmation\" IN ('Gesture','Accept','ProtectedConfirmation')");
                    table.CheckConstraint("CK_ApplicationOperation_Decision", "(\"DecisionReferenceDigest\" IS NULL AND \"Decision\" IS NULL) OR (length(\"DecisionReferenceDigest\") = 32 AND \"Decision\" IN ('Accept','Reject','Cancel','Confirm'))");
                    table.CheckConstraint("CK_ApplicationOperation_Effect", "\"Effect\" IN ('Write','Launch','Composite')");
                    table.CheckConstraint("CK_ApplicationOperation_IdempotencyDigest", "\"IdempotencyDigest\" IS NULL OR length(\"IdempotencyDigest\") = 32");
                    table.CheckConstraint("CK_ApplicationOperation_Lease", "(\"Status\" = 'Executing' AND \"LeaseId\" IS NOT NULL AND \"LeaseExpiresAtUtc\" IS NOT NULL AND \"AttemptCount\" > 0) OR (\"Status\" <> 'Executing' AND \"LeaseId\" IS NULL AND \"LeaseExpiresAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_ApplicationOperation_Lifecycle", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"PurgeAfterUtc\" >= \"CreatedAtUtc\" AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_ApplicationOperation_ParentFence", "(\"ParentOperationId\" IS NULL AND \"ParentApplicationVersion\" IS NULL AND \"ParentFence\" IS NULL) OR (\"ParentOperationId\" IS NOT NULL AND \"ParentOperationId\" <> \"Id\" AND \"ParentApplicationVersion\" > 0 AND \"ParentFence\" >= 0)");
                    table.CheckConstraint("CK_ApplicationOperation_PayloadPurge", "\"PayloadPurgedAtUtc\" IS NULL OR (\"TerminalAtUtc\" IS NOT NULL AND \"PayloadPurgedAtUtc\" >= \"TerminalAtUtc\")");
                    table.CheckConstraint("CK_ApplicationOperation_Status", "\"Status\" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed')");
                    table.CheckConstraint("CK_ApplicationOperation_Terminal", "(\"Status\" IN ('Executed','Rejected','Cancelled','Expired','Failed','Reversed') AND \"TerminalAtUtc\" >= \"CreatedAtUtc\") OR (\"Status\" IN ('Proposed','AwaitingProtectedConfirmation','Executing') AND \"TerminalAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_ApplicationOperation_Versions", "\"ExpectedDomainVersion\" >= 0 AND \"ExpectedSynchronizationVersion\" >= 0 AND \"ApplicationVersion\" > 0 AND \"Fence\" >= 0 AND \"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_ApplicationOperation_ApplicationOperation_ParentOperationId",
                        column: x => x.ParentOperationId,
                        principalTable: "ApplicationOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationOperationConfirmation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ConfirmationDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    DecisionReferenceDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedApplicationVersion = table.Column<long>(type: "bigint", nullable: true),
                    ConsumedFence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationOperationConfirmation", x => x.Id);
                    table.CheckConstraint("CK_ApplicationOperationConfirmation_Consumption", "(\"ConsumedAtUtc\" IS NULL AND \"ConsumedApplicationVersion\" IS NULL AND \"ConsumedFence\" IS NULL) OR (\"ConsumedAtUtc\" IS NOT NULL AND \"ConsumedApplicationVersion\" > 0 AND \"ConsumedFence\" >= 0)");
                    table.CheckConstraint("CK_ApplicationOperationConfirmation_Digests", "length(\"ConfirmationDigest\") = 32 AND length(\"DecisionReferenceDigest\") = 32");
                    table.CheckConstraint("CK_ApplicationOperationConfirmation_Lifecycle", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND (\"ConsumedAtUtc\" IS NULL OR (\"ConsumedAtUtc\" >= \"CreatedAtUtc\" AND \"ConsumedAtUtc\" <= \"ExpiresAtUtc\")) AND (\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"CreatedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_ApplicationOperationConfirmation_ApplicationOperation_Opera~",
                        column: x => x.OperationId,
                        principalTable: "ApplicationOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationOperationContinuation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Workflow = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WorkflowVersion = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    InteractionScopeDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ParentContinuationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AllowsAutomaticResume = table.Column<bool>(type: "boolean", nullable: false),
                    AutomaticResumeCount = table.Column<int>(type: "integer", nullable: false),
                    ApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PurgeAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationOperationContinuation", x => x.Id);
                    table.CheckConstraint("CK_ApplicationOperationContinuation_Digest", "length(\"InteractionScopeDigest\") = 32");
                    table.CheckConstraint("CK_ApplicationOperationContinuation_Lifecycle", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"PurgeAfterUtc\" >= \"ExpiresAtUtc\" AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_ApplicationOperationContinuation_Resume", "\"WorkflowVersion\" > 0 AND \"AutomaticResumeCount\" >= 0 AND \"AutomaticResumeCount\" <= 1 AND \"ApplicationVersion\" > 0 AND (\"AllowsAutomaticResume\" = TRUE OR \"AutomaticResumeCount\" = 0) AND (\"ParentContinuationId\" IS NULL OR (\"AllowsAutomaticResume\" = FALSE AND \"AutomaticResumeCount\" = 0)) AND ((\"AutomaticResumeCount\" = 0 AND \"ResumedAtUtc\" IS NULL) OR (\"AutomaticResumeCount\" = 1 AND \"ResumedAtUtc\" IS NOT NULL))");
                    table.CheckConstraint("CK_ApplicationOperationContinuation_State", "\"State\" IN ('AwaitingDecision','ReadyToResume','Completed','Expired','Cancelled')");
                    table.CheckConstraint("CK_ApplicationOperationContinuation_Workflow", "\"Workflow\" IN ('Clarification','PostReceiptResume')");
                    table.ForeignKey(
                        name: "FK_ApplicationOperationContinuation_ApplicationOperationContin~",
                        column: x => x.ParentContinuationId,
                        principalTable: "ApplicationOperationContinuation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationOperationContinuation_ApplicationOperation_Opera~",
                        column: x => x.OperationId,
                        principalTable: "ApplicationOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationOperationEvent",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationOperationEvent", x => x.Id);
                    table.CheckConstraint("CK_ApplicationOperationEvent_Failure", "(\"Kind\" = 'Failed' AND \"FailureCode\" IN ('InvalidProtectedContent','InvalidCanonicalRequest','CapabilityUnavailable','AuthorizationDenied','StaleDomainVersion','StaleSynchronizationVersion','PreEffectHandlerFailure')) OR (\"Kind\" <> 'Failed' AND \"FailureCode\" IS NULL)");
                    table.CheckConstraint("CK_ApplicationOperationEvent_Kind", "\"Kind\" IN ('Proposed','AwaitingProtectedConfirmation','ExecutionClaimed','LeaseRecovered','Executed','Rejected','Cancelled','Expired','Failed','ReversalLinked','Reversed','ContinuationCreated','ContinuationResumed')");
                    table.CheckConstraint("CK_ApplicationOperationEvent_Sequence", "\"Sequence\" > 0 AND \"ApplicationVersion\" > 0 AND \"Fence\" >= 0");
                    table.CheckConstraint("CK_ApplicationOperationEvent_Status", "(\"FromStatus\" IS NULL OR \"FromStatus\" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed')) AND \"ToStatus\" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed')");
                    table.ForeignKey(
                        name: "FK_ApplicationOperationEvent_ApplicationOperation_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ApplicationOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationOperationReceipt",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ReceiptVersion = table.Column<int>(type: "integer", nullable: false),
                    BeforeDomainVersion = table.Column<long>(type: "bigint", nullable: false),
                    BeforeSynchronizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    AfterDomainVersion = table.Column<long>(type: "bigint", nullable: false),
                    AfterSynchronizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    Reversal = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReversalExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversalOperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CommittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationOperationReceipt", x => x.Id);
                    table.CheckConstraint("CK_ApplicationOperationReceipt_Reversal", "\"Reversal\" IN ('Unavailable','Available','Expired','Completed')");
                    table.CheckConstraint("CK_ApplicationOperationReceipt_ReversalState", "(\"Reversal\" = 'Available' AND \"ReversalExpiresAtUtc\" IS NOT NULL) OR (\"Reversal\" = 'Completed' AND \"ReversalOperationId\" IS NOT NULL) OR (\"Reversal\" IN ('Unavailable','Expired') AND \"ReversalOperationId\" IS NULL)");
                    table.CheckConstraint("CK_ApplicationOperationReceipt_Versions", "\"ReceiptVersion\" > 0 AND \"BeforeDomainVersion\" >= 0 AND \"BeforeSynchronizationVersion\" >= 0 AND \"AfterDomainVersion\" >= \"BeforeDomainVersion\" AND \"AfterSynchronizationVersion\" >= \"BeforeSynchronizationVersion\"");
                    table.ForeignKey(
                        name: "FK_ApplicationOperationReceipt_ApplicationOperation_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ApplicationOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationOperationReceipt_ApplicationOperation_ReversalOp~",
                        column: x => x.ReversalOperationId,
                        principalTable: "ApplicationOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationProtectedPayload",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProtectionVersion = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", maxLength: 1114112, nullable: false),
                    PlaintextLength = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PurgeAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationProtectedPayload", x => x.Id);
                    table.CheckConstraint("CK_ApplicationProtectedPayload_Content", "\"ContentKind\" IN ('CanonicalRequest','ProposalPresentation','PriorState','Receipt','ContinuationState')");
                    table.CheckConstraint("CK_ApplicationProtectedPayload_Length", "\"PlaintextLength\" > 0 AND \"PlaintextLength\" <= 1048576 AND length(\"Ciphertext\") > 0 AND length(\"Ciphertext\") <= 1114112");
                    table.CheckConstraint("CK_ApplicationProtectedPayload_Lifecycle", "\"PurgeAfterUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_ApplicationProtectedPayload_Subject", "(\"SubjectKind\" = 'Operation' AND \"SubjectId\" = \"OperationId\") OR \"SubjectKind\" = 'Continuation'");
                    table.CheckConstraint("CK_ApplicationProtectedPayload_Versions", "\"ProtectionVersion\" = 1 AND \"SchemaVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_ApplicationProtectedPayload_ApplicationOperation_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ApplicationOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperation_ParentOperationId",
                table: "ApplicationOperation",
                column: "ParentOperationId",
                unique: true,
                filter: "\"ParentOperationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperation_Status_LeaseExpiresAtUtc",
                table: "ApplicationOperation",
                columns: new[] { "Status", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperation_UserProfileId_Authority_DecisionReferenc~",
                table: "ApplicationOperation",
                columns: new[] { "UserProfileId", "Authority", "DecisionReferenceDigest" },
                filter: "\"DecisionReferenceDigest\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperation_UserProfileId_Authority_IdempotencyDigest",
                table: "ApplicationOperation",
                columns: new[] { "UserProfileId", "Authority", "IdempotencyDigest" },
                unique: true,
                filter: "\"IdempotencyDigest\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperation_UserProfileId_Authority_CapabilityCode~",
                table: "ApplicationOperation",
                columns: new[] { "UserProfileId", "Authority", "CapabilityCode", "CapabilityVersion", "IdempotencyDigest" },
                unique: true,
                filter: "\"IdempotencyDigest\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperation_UserProfileId_PurgeAfterUtc",
                table: "ApplicationOperation",
                columns: new[] { "UserProfileId", "PurgeAfterUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperation_UserProfileId_Status_ExpiresAtUtc",
                table: "ApplicationOperation",
                columns: new[] { "UserProfileId", "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationConfirmation_OperationId_ConfirmationDi~",
                table: "ApplicationOperationConfirmation",
                columns: new[] { "OperationId", "ConfirmationDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationConfirmation_OperationId",
                table: "ApplicationOperationConfirmation",
                column: "OperationId",
                unique: true,
                filter: "\"ConsumedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationConfirmation_UserProfileId_ExpiresAtUtc",
                table: "ApplicationOperationConfirmation",
                columns: new[] { "UserProfileId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationContinuation_OperationId",
                table: "ApplicationOperationContinuation",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationContinuation_ParentContinuationId",
                table: "ApplicationOperationContinuation",
                column: "ParentContinuationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationContinuation_UserProfileId_ExpiresAtUtc",
                table: "ApplicationOperationContinuation",
                columns: new[] { "UserProfileId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationContinuation_UserProfileId_InteractionS~",
                table: "ApplicationOperationContinuation",
                columns: new[] { "UserProfileId", "InteractionScopeDigest" },
                unique: true,
                filter: "\"State\" IN ('AwaitingDecision','ReadyToResume')");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationEvent_OperationId_Sequence",
                table: "ApplicationOperationEvent",
                columns: new[] { "OperationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationEvent_UserProfileId_OccurredAtUtc",
                table: "ApplicationOperationEvent",
                columns: new[] { "UserProfileId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationReceipt_OperationId",
                table: "ApplicationOperationReceipt",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationReceipt_ReversalOperationId",
                table: "ApplicationOperationReceipt",
                column: "ReversalOperationId",
                unique: true,
                filter: "\"ReversalOperationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOperationReceipt_UserProfileId_CommittedAtUtc",
                table: "ApplicationOperationReceipt",
                columns: new[] { "UserProfileId", "CommittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationProtectedPayload_OperationId",
                table: "ApplicationProtectedPayload",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationProtectedPayload_SubjectKind_SubjectId_ContentKi~",
                table: "ApplicationProtectedPayload",
                columns: new[] { "SubjectKind", "SubjectId", "ContentKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationProtectedPayload_UserProfileId_PurgeAfterUtc",
                table: "ApplicationProtectedPayload",
                columns: new[] { "UserProfileId", "PurgeAfterUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationOperationConfirmation");

            migrationBuilder.DropTable(
                name: "ApplicationOperationContinuation");

            migrationBuilder.DropTable(
                name: "ApplicationOperationEvent");

            migrationBuilder.DropTable(
                name: "ApplicationOperationReceipt");

            migrationBuilder.DropTable(
                name: "ApplicationProtectedPayload");

            migrationBuilder.DropTable(
                name: "ApplicationOperation");
        }
    }
}
