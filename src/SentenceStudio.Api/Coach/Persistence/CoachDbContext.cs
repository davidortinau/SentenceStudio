using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// Server-only PostgreSQL context for Learning Coach state.
/// </summary>
/// <remarks>
/// This context is deliberately separate from <c>ApplicationDbContext</c>:
/// <list type="bullet">
/// <item>Coach state never syncs to a device, so it must not join the CoreSync entity set.</item>
/// <item>It must not produce mobile SQLite migrations — its migrations are PostgreSQL-only.</item>
/// <item>Its retention and deletion rules are independent of learner learning data.</item>
/// </list>
/// The relational JSON columns are configured as <c>jsonb</c> only when the active provider
/// is Npgsql, so behaviour tests can run the same model on a relational test provider.
/// </remarks>
public sealed class CoachDbContext : DbContext
{
    public CoachDbContext(DbContextOptions<CoachDbContext> options) : base(options)
    {
    }

    public DbSet<CoachSession> CoachSessions => Set<CoachSession>();

    public DbSet<CoachPlanRevision> CoachPlanRevisions => Set<CoachPlanRevision>();

    public DbSet<CoachUsage> CoachUsages => Set<CoachUsage>();

    public DbSet<History.CoachConversation> CoachConversations => Set<History.CoachConversation>();

    public DbSet<History.CoachMessage> CoachMessages => Set<History.CoachMessage>();

    public DbSet<History.CoachTurnOperation> CoachTurnOperations => Set<History.CoachTurnOperation>();

    public DbSet<Memory.CoachMemoryFact> CoachMemoryFacts => Set<Memory.CoachMemoryFact>();

    public DbSet<Operations.CoachWriteOperation> CoachWriteOperations => Set<Operations.CoachWriteOperation>();

    public DbSet<Operations.CoachWriteAudit> CoachWriteAudits => Set<Operations.CoachWriteAudit>();

    public DbSet<Opportunities.CoachOpportunity> CoachOpportunities => Set<Opportunities.CoachOpportunity>();

    public DbSet<Reports.CoachResponseReport> CoachResponseReports => Set<Reports.CoachResponseReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var isNpgsql = Database.IsNpgsql();
        var jsonColumnType = isNpgsql ? "jsonb" : null;

        modelBuilder.Entity<CoachSession>(entity =>
        {
            entity.ToTable("CoachSession");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(64);

            entity.Property(e => e.UserProfileId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AgentImplementation).IsRequired().HasMaxLength(32);
            entity.Property(e => e.AgentName).IsRequired().HasMaxLength(128);
            // Bounded by the same limit CoachOptionsValidator enforces on the operator value,
            // so a configuration change can never exceed the column it is stamped into.
            entity.Property(e => e.AgentConfigVersion)
                  .IsRequired()
                  .HasMaxLength(CoachOptionsValidator.MaxAgentConfigVersionLength);
            entity.Property(e => e.SessionSchemaVersion).IsRequired();

            // Ciphertext, not JSON. Never typed as jsonb.
            entity.Property(e => e.ProtectedAgentSession);

            entity.Property(e => e.ActiveConstraintsJson).IsRequired();
            entity.Property(e => e.PendingSuggestionId).HasMaxLength(64);

            if (jsonColumnType is not null)
            {
                entity.Property(e => e.ActiveConstraintsJson).HasColumnType(jsonColumnType);
                entity.Property(e => e.PendingSuggestionDeltaJson).HasColumnType(jsonColumnType);
            }

            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.StopReason).HasConversion<int>();

            entity.HasIndex(e => e.UserProfileId).HasDatabaseName("IX_CoachSession_UserProfileId");
            entity.HasIndex(e => new { e.UserProfileId, e.Status })
                  .HasDatabaseName("IX_CoachSession_UserProfileId_Status");
            entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("IX_CoachSession_ExpiresAt");
        });

        modelBuilder.Entity<CoachPlanRevision>(entity =>
        {
            entity.ToTable("CoachPlanRevision");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(64);

            entity.Property(e => e.UserProfileId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.RevisionNumber).IsRequired();
            entity.Property(e => e.Source).HasConversion<int>();
            entity.Property(e => e.IntentKind).HasConversion<int>();

            entity.Property(e => e.AcceptedConstraintDeltaJson).IsRequired();
            entity.Property(e => e.BeforePlanSnapshotJson).IsRequired();
            entity.Property(e => e.AfterPlanSnapshotJson).IsRequired();

            if (jsonColumnType is not null)
            {
                entity.Property(e => e.AcceptedConstraintDeltaJson).HasColumnType(jsonColumnType);
                entity.Property(e => e.BeforePlanSnapshotJson).HasColumnType(jsonColumnType);
                entity.Property(e => e.AfterPlanSnapshotJson).HasColumnType(jsonColumnType);
            }

            entity.Property(e => e.BeforePlanVersion).IsRequired().HasMaxLength(128);
            entity.Property(e => e.AfterPlanVersion).IsRequired().HasMaxLength(128);
            entity.Property(e => e.BeforePlanHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AfterPlanHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.UndoneByRevisionId).HasMaxLength(64);
            entity.Property(e => e.OperationId).HasMaxLength(64);

            entity.HasIndex(e => e.UserProfileId).HasDatabaseName("IX_CoachPlanRevision_UserProfileId");
            entity.HasIndex(e => new { e.UserProfileId, e.SessionId, e.RevisionNumber })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachPlanRevision_UserProfileId_SessionId_RevisionNumber");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_CoachPlanRevision_CreatedAt");

            // Unique, so a retry cannot write a second revision for an operation that already
            // wrote one. Recovery reads through this index to find the exact revision a crashed
            // turn produced, and the uniqueness is what makes "reconstruct one receipt, append it
            // once" a property of the schema rather than a hope about the code path.
            entity.HasIndex(e => new { e.UserProfileId, e.OperationId })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachPlanRevision_UserProfileId_OperationId");
        });

        modelBuilder.Entity<CoachUsage>(entity =>
        {
            entity.ToTable("CoachUsage");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(64);

            entity.Property(e => e.UserProfileId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.LocalDate).IsRequired();
            entity.Property(e => e.WeekKey).IsRequired().HasMaxLength(8);
            entity.Property(e => e.EstimatedCostUsd).HasPrecision(12, 6);

            entity.HasIndex(e => new { e.UserProfileId, e.LocalDate })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachUsage_UserProfileId_LocalDate");
            entity.HasIndex(e => new { e.UserProfileId, e.WeekKey })
                  .HasDatabaseName("IX_CoachUsage_UserProfileId_WeekKey");
        });

        ConfigureHistory(modelBuilder);
        ConfigureMemory(modelBuilder);
        ConfigureWriteOperations(modelBuilder);
        ConfigureOpportunities(modelBuilder);

        if (!isNpgsql)
        {
            return;
        }

        // Pin every timestamp to `timestamp with time zone`, which is what the coach
        // migration created. The API host sets the global
        // `Npgsql.EnableLegacyTimestampBehavior` switch for the SQLite-era DateTime values in
        // ApplicationDbContext; without this pin that switch would silently remap the coach's
        // DateTime columns to `timestamp without time zone`, so the runtime model would no
        // longer match its own migration and every insert would fail on a type mismatch.
        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
        {
            property.SetColumnType("timestamp with time zone");
            property.SetValueConverter(UtcDateTimeConverter);
        }
    }

    /// <summary>
    /// Forces every coach <see cref="DateTime"/> to arrive and leave the database as
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same legacy timestamp switch that makes the column-type pin above necessary also
    /// changes how a <c>timestamp with time zone</c> is materialised: Npgsql converts it into the
    /// host machine's local zone and hands back a <see cref="DateTimeKind.Local"/> value. The
    /// instant is preserved, so anything that only reads or re-persists the value is unaffected.
    /// </para>
    /// <para>
    /// Comparison is not unaffected. <see cref="DateTime"/> operators ignore
    /// <see cref="DateTime.Kind"/> and compare raw ticks, and the coach stores compare persisted
    /// timestamps against <c>TimeProvider.GetUtcNow().UtcDateTime</c> in memory — most importantly
    /// when deciding whether a turn operation's lease is still live. Without this converter that
    /// comparison is wrong by the host's UTC offset in whichever direction the host sits: west of
    /// UTC a live lease reads as expired and a second worker takes over an operation someone else
    /// still holds, defeating the fencing entirely; east of UTC a dead lease reads as live and
    /// crash recovery never fires. A deployment that happens to run in UTC hides the defect, which
    /// is what makes it worth pinning here rather than at each comparison.
    /// </para>
    /// <para>
    /// The same normalisation runs in both directions. Values written by the stores are already
    /// UTC, so the write side is a no-op for them, and it makes an accidental local or unspecified
    /// value safe rather than silently off by an offset.
    /// </para>
    /// </remarks>
    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter = new(
        value => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime(),
        value => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());

    /// <summary>
    /// Maps the durable Sam conversation history tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every child table carries <c>UserProfileId</c> and joins its parent on
    /// <c>(UserProfileId, ConversationId)</c> rather than on <c>ConversationId</c> alone. A
    /// single-column foreign key would let a row whose owner had been corrupted still resolve to
    /// a valid parent; the composite key makes a cross-owner row unrepresentable at the schema
    /// level instead of relying on every query being written correctly.
    /// </para>
    /// <para>
    /// Protected columns hold ciphertext and are never typed <c>jsonb</c>: the database must not
    /// try to parse them, and they would fail validation if it did.
    /// </para>
    /// </remarks>
    private static void ConfigureHistory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<History.CoachConversation>(entity =>
        {
            entity.ToTable("CoachConversation");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(History.CoachHistoryLimits.IdMaxLength);

            entity.Property(e => e.UserProfileId)
                  .IsRequired()
                  .HasMaxLength(History.CoachHistoryLimits.UserProfileIdMaxLength);
            entity.Property(e => e.TenantId).HasMaxLength(History.CoachHistoryLimits.TenantIdMaxLength);

            // Ciphertext, not JSON. Never typed as jsonb.
            entity.Property(e => e.ProtectedTitle).IsRequired();

            entity.Property(e => e.TitleSource).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.TargetLanguageCode)
                  .HasMaxLength(History.CoachHistoryLimits.TargetLanguageCodeMaxLength);

            entity.Property(e => e.MetadataSchemaVersion).IsRequired();
            entity.Property(e => e.ContentProtectionVersion).IsRequired();

            // A plain integer token rather than the Npgsql xmin pseudo-column, so optimistic
            // concurrency behaves identically on the relational test provider and on PostgreSQL.
            entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();

            // The owner-aware target every child foreign key points at.
            entity.HasAlternateKey(e => new { e.UserProfileId, e.Id });

            entity.HasIndex(e => e.UserProfileId).HasDatabaseName("IX_CoachConversation_UserProfileId");
            entity.HasIndex(e => new { e.UserProfileId, e.UpdatedAt, e.Id })
                  .HasDatabaseName("IX_CoachConversation_UserProfileId_UpdatedAt_Id");
            entity.HasIndex(e => new { e.UserProfileId, e.Status })
                  .HasDatabaseName("IX_CoachConversation_UserProfileId_Status");
        });

        modelBuilder.Entity<History.CoachMessage>(entity =>
        {
            entity.ToTable("CoachMessage");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(History.CoachHistoryLimits.IdMaxLength);

            entity.Property(e => e.UserProfileId)
                  .IsRequired()
                  .HasMaxLength(History.CoachHistoryLimits.UserProfileIdMaxLength);
            entity.Property(e => e.TenantId).HasMaxLength(History.CoachHistoryLimits.TenantIdMaxLength);
            entity.Property(e => e.ConversationId)
                  .IsRequired()
                  .HasMaxLength(History.CoachHistoryLimits.IdMaxLength);

            entity.Property(e => e.Sequence).IsRequired();
            entity.Property(e => e.Role).HasConversion<int>();
            entity.Property(e => e.Kind).HasConversion<int>();

            // Ciphertext, not JSON. Never typed as jsonb.
            entity.Property(e => e.ProtectedPayload).IsRequired();

            entity.Property(e => e.ContentSchemaVersion).IsRequired();
            entity.Property(e => e.ContentProtectionVersion).IsRequired();
            entity.Property(e => e.OperationId).HasMaxLength(History.CoachHistoryLimits.IdMaxLength);

            // The ledger invariant: one message per position per conversation per owner. Two
            // writers racing the same sequence lose here rather than producing a transcript with
            // two turns at the same point.
            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId, e.Sequence })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachMessage_UserProfileId_ConversationId_Sequence");
            entity.HasIndex(e => new { e.UserProfileId, e.OperationId })
                  .HasDatabaseName("IX_CoachMessage_UserProfileId_OperationId");

            entity.HasOne<History.CoachConversation>()
                  .WithMany()
                  .HasForeignKey(e => new { e.UserProfileId, e.ConversationId })
                  .HasPrincipalKey(c => new { c.UserProfileId, c.Id })
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<History.CoachTurnOperation>(entity =>
        {
            entity.ToTable("CoachTurnOperation");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(History.CoachHistoryLimits.IdMaxLength);

            entity.Property(e => e.UserProfileId)
                  .IsRequired()
                  .HasMaxLength(History.CoachHistoryLimits.UserProfileIdMaxLength);
            entity.Property(e => e.TenantId).HasMaxLength(History.CoachHistoryLimits.TenantIdMaxLength);
            entity.Property(e => e.ConversationId)
                  .IsRequired()
                  .HasMaxLength(History.CoachHistoryLimits.IdMaxLength);

            // A digest of the client's key, never the key itself.
            entity.Property(e => e.IdempotencyKeyDigest)
                  .IsRequired()
                  .HasMaxLength(History.CoachHistoryLimits.DigestMaxLength);

            // Ciphertext, not JSON. Never typed as jsonb.
            entity.Property(e => e.ProtectedRequestDigest).IsRequired();
            entity.Property(e => e.ProtectedOutcome);

            entity.Property(e => e.ContentProtectionVersion).IsRequired();
            entity.Property(e => e.BaseConversationVersion).IsRequired();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.LeaseOwner).HasMaxLength(History.CoachHistoryLimits.LeaseOwnerMaxLength);
            entity.Property(e => e.FencingVersion).IsRequired();
            entity.Property(e => e.AttemptCount).IsRequired();
            entity.Property(e => e.CancelRequested).IsRequired();
            entity.Property(e => e.ErrorCode).HasMaxLength(History.CoachHistoryLimits.ErrorCodeMaxLength);
            entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();

            // The idempotency invariant: one operation per key per conversation per owner.
            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId, e.IdempotencyKeyDigest })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachTurnOperation_UserProfileId_ConversationId_KeyDigest");
            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId, e.Status })
                  .HasDatabaseName("IX_CoachTurnOperation_UserProfileId_ConversationId_Status");

            // Drives crash recovery: find non-terminal operations whose lease has lapsed.
            entity.HasIndex(e => e.LeaseExpiresAt).HasDatabaseName("IX_CoachTurnOperation_LeaseExpiresAt");

            entity.HasOne<History.CoachConversation>()
                  .WithMany()
                  .HasForeignKey(e => new { e.UserProfileId, e.ConversationId })
                  .HasPrincipalKey(c => new { c.UserProfileId, c.Id })
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Maps the single learner-memory table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One table, one row per remembered fact. There is deliberately no event table and no
    /// provenance table in v1: a fact has exactly one source, and adding history tables would
    /// create a second place a forgotten preference could survive a deletion.
    /// </para>
    /// <para>
    /// <c>ScopeKey</c> is a derived, non-null projection of the scope — <c>global</c> or
    /// <c>lang:{tag}</c>. It exists because PostgreSQL treats NULLs as distinct in a unique index,
    /// so a nullable language column could not express "one active fact per owner, kind, and
    /// scope" for globally scoped facts. The uniqueness rule is the whole point, so the schema
    /// carries a column that can actually enforce it.
    /// </para>
    /// <para>
    /// There is no foreign key to <c>CoachConversation</c>. The source reference is opaque
    /// metadata, and a database-level cascade would delete memory silently, behind the back of
    /// <c>ICoachMemoryChangedNotifier</c>, leaving a forgotten preference alive inside an already
    /// serialized session checkpoint. Source deletion is done explicitly instead.
    /// </para>
    /// </remarks>
    private static void ConfigureMemory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Memory.CoachMemoryFact>(entity =>
        {
            entity.ToTable("CoachMemoryFact");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(Memory.CoachMemoryLimits.IdMaxLength);

            entity.Property(e => e.UserProfileId).IsRequired().HasMaxLength(Memory.CoachMemoryLimits.UserProfileIdMaxLength);

            // Metadata only. Never part of a key, an index, or a protection purpose, so a tenant
            // rename can never orphan a learner's memory or unlock another learner's.
            entity.Property(e => e.TenantId).HasMaxLength(Memory.CoachMemoryLimits.TenantIdMaxLength);

            entity.Property(e => e.Kind).IsRequired().HasConversion<int>();
            entity.Property(e => e.Scope).IsRequired().HasConversion<int>();
            entity.Property(e => e.Status).IsRequired().HasConversion<int>();
            entity.Property(e => e.Provenance).IsRequired().HasConversion<int>();

            entity.Property(e => e.TargetLanguageCode).HasMaxLength(Memory.CoachMemoryLimits.LanguageCodeMaxLength);
            entity.Property(e => e.ScopeKey).IsRequired().HasMaxLength(Memory.CoachMemoryLimits.ScopeKeyMaxLength);

            // Ciphertext, not JSON. Never typed jsonb.
            entity.Property(e => e.ProtectedValue)
                  .IsRequired()
                  .HasMaxLength(Memory.CoachMemoryLimits.ProtectedValueMaxLength);
            entity.Property(e => e.ValueVersion).IsRequired();
            entity.Property(e => e.ProtectionVersion).IsRequired();

            // Opaque source references. Bounded, indexed, and never dereferenced as a foreign key.
            entity.Property(e => e.SourceConversationId).HasMaxLength(Memory.CoachMemoryLimits.IdMaxLength);
            entity.Property(e => e.SourceMessageId).HasMaxLength(Memory.CoachMemoryLimits.IdMaxLength);

            // Bounded counts and dates only. The learner's words are never stored as evidence.
            entity.Property(e => e.EvidenceCount).IsRequired();

            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.Property(e => e.SupersedesId).HasMaxLength(Memory.CoachMemoryLimits.IdMaxLength);

            entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();

            entity.HasIndex(e => new { e.UserProfileId, e.Status, e.Kind })
                  .HasDatabaseName("IX_CoachMemoryFact_UserProfileId_Status_Kind");
            entity.HasIndex(e => new { e.UserProfileId, e.UpdatedAt })
                  .HasDatabaseName("IX_CoachMemoryFact_UserProfileId_UpdatedAt");

            // Drives source-conversation deletion without a cascade.
            entity.HasIndex(e => new { e.UserProfileId, e.SourceConversationId })
                  .HasDatabaseName("IX_CoachMemoryFact_UserProfileId_SourceConversationId");

            // The single-valued invariant, enforced by the database rather than by every caller
            // remembering to check. Filtered on Active so superseded and expired rows accumulate
            // freely; the filter string is written so both PostgreSQL and the relational test
            // provider parse it.
            entity.HasIndex(e => new { e.UserProfileId, e.Kind, e.ScopeKey })
                  .IsUnique()
                  .HasFilter("\"Status\" = 1")
                  .HasDatabaseName("UX_CoachMemoryFact_UserProfileId_Kind_ScopeKey_Active");
        });
    }
    /// <summary>
    /// Maps the learner-owned write ledger and its append-only audit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called before the timestamp pin below, so both tables get the same
    /// <c>timestamp with time zone</c> treatment every other coach table gets.
    /// </para>
    /// <para>
    /// The two tables are separate on purpose. <c>CoachWriteOperation</c> carries the ciphertext
    /// needed to execute and reverse a write; <c>CoachWriteAudit</c> carries none at all, so the
    /// audit trail can be read, exported, or retained without ever exposing learner content.
    /// </para>
    /// </remarks>
    private static void ConfigureWriteOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Operations.CoachWriteOperation>(entity =>
        {
            entity.ToTable("CoachWriteOperation");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);

            entity.Property(e => e.UserProfileId)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.UserProfileIdMaxLength);
            entity.Property(e => e.TenantId).HasMaxLength(Operations.CoachWriteLimits.TenantIdMaxLength);
            entity.Property(e => e.ConversationId)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);
            entity.Property(e => e.TurnId).HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);

            entity.Property(e => e.ToolName)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.ToolNameMaxLength);
            entity.Property(e => e.RiskClass).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.UndoKind).HasConversion<int>();
            entity.Property(e => e.EntityKind).HasConversion<int>();
            entity.Property(e => e.EntityId).HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);

            entity.Property(e => e.IdempotencyKeyDigest)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.DigestMaxLength);

            // A digest of the one-use confirmation secret. The secret is never stored, so a
            // database copy cannot be replayed into a confirmed write.
            entity.Property(e => e.ConfirmationDigest)
                  .HasMaxLength(Operations.CoachWriteLimits.DigestMaxLength);

            // Ciphertext, not JSON. Never typed as jsonb.
            entity.Property(e => e.ProtectedArguments).IsRequired();
            entity.Property(e => e.ProtectedPreview).IsRequired();
            entity.Property(e => e.ProtectedPriorState);
            entity.Property(e => e.ProtectedReceipt);

            entity.Property(e => e.ContentProtectionVersion).IsRequired();
            entity.Property(e => e.ExpiresAtUtc).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.UpdatedAtUtc).IsRequired();
            entity.Property(e => e.UndoOperationId).HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);
            entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();

            // The idempotency invariant: one live proposal per (owner, conversation, tool+args).
            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId, e.IdempotencyKeyDigest })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachWriteOperation_UserProfileId_ConversationId_KeyDigest");

            // The surface invariant: one proposal per turn.
            //
            // The count in the ledger refuses the second call with a sentence the model can act
            // on; this is what makes that refusal true when two requests for one turn arrive at
            // once and both counts read zero. Without it the bound is advisory — correct for the
            // sequential tool loop, and silently wrong for the case a retried turn produces.
            //
            // Reversal rows carry a derived turn identity of their own, so they never contend for
            // the slot. Rows written before turn identity was required carry null, and PostgreSQL
            // treats nulls as distinct, so they neither collide nor block.
            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId, e.TurnId })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachWriteOperation_UserProfileId_ConversationId_TurnId");

            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId, e.Status })
                  .HasDatabaseName("IX_CoachWriteOperation_UserProfileId_ConversationId_Status");

            // Drives the retention sweep.
            entity.HasIndex(e => e.ExpiresAtUtc).HasDatabaseName("IX_CoachWriteOperation_ExpiresAtUtc");

            // Composite owner+conversation foreign key: a row whose owner column was corrupted
            // cannot resolve to a conversation that belongs to somebody else.
            entity.HasOne<History.CoachConversation>()
                  .WithMany()
                  .HasForeignKey(e => new { e.UserProfileId, e.ConversationId })
                  .HasPrincipalKey(c => new { c.UserProfileId, c.Id })
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Operations.CoachWriteAudit>(entity =>
        {
            entity.ToTable("CoachWriteAudit");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);

            entity.Property(e => e.OperationId)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);
            entity.Property(e => e.UserProfileId)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.UserProfileIdMaxLength);
            entity.Property(e => e.TenantId).HasMaxLength(Operations.CoachWriteLimits.TenantIdMaxLength);
            entity.Property(e => e.ConversationId)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);
            entity.Property(e => e.TurnId).HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);
            entity.Property(e => e.ToolName)
                  .IsRequired()
                  .HasMaxLength(Operations.CoachWriteLimits.ToolNameMaxLength);
            entity.Property(e => e.RiskClass).HasConversion<int>();
            entity.Property(e => e.Event).HasConversion<int>();
            entity.Property(e => e.EntityKind).HasConversion<int>();
            entity.Property(e => e.EntityId).HasMaxLength(Operations.CoachWriteLimits.IdMaxLength);
            entity.Property(e => e.FailureCode).HasMaxLength(Operations.CoachWriteLimits.FailureCodeMaxLength);
            entity.Property(e => e.CreatedAtUtc).IsRequired();

            entity.HasIndex(e => new { e.UserProfileId, e.CreatedAtUtc })
                  .HasDatabaseName("IX_CoachWriteAudit_UserProfileId_CreatedAtUtc");
            entity.HasIndex(e => new { e.UserProfileId, e.OperationId })
                  .HasDatabaseName("IX_CoachWriteAudit_UserProfileId_OperationId");

            // Deliberately no foreign key to CoachWriteOperation. The audit outlives the
            // operational row, and must still describe a refusal for an operation id that never
            // produced a row at all.
        });
    }

    /// <summary>
    /// Maps the content-free Sam opportunity ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every column is an identifier, an enum ordinal, a closed-vocabulary code, a timestamp, or
    /// a count. There is no <c>jsonb</c> column and no protected column here, deliberately: this
    /// table holds no payload of any kind, so nothing on it needs decrypting and nothing on it
    /// can leak.
    /// </para>
    /// <para>
    /// <b>No foreign key</b>, for the same reason <c>CoachWriteAudit</c> has none. The ledger has
    /// to keep describing a gap after the conversation that produced it was deleted; a cascade
    /// would erase the product signal along with the learner's transcript, and a restricted key
    /// would block the learner's own delete.
    /// </para>
    /// </remarks>
    private static void ConfigureOpportunities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Opportunities.CoachOpportunity>(entity =>
        {
            entity.ToTable("CoachOpportunity");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                  .ValueGeneratedNever()
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);

            entity.Property(e => e.UserProfileId)
                  .IsRequired()
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.UserProfileIdMaxLength);
            entity.Property(e => e.TenantId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.TenantIdMaxLength);
            entity.Property(e => e.ConversationId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);
            entity.Property(e => e.TurnId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);
            entity.Property(e => e.TurnOperationId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);

            entity.Property(e => e.Kind).HasConversion<int>();
            entity.Property(e => e.Disposition).HasConversion<int>();
            entity.Property(e => e.Surface).HasConversion<int>();
            entity.Property(e => e.OfferLink).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.RiskClass).HasConversion<int>();
            entity.Property(e => e.StopReason).HasConversion<int>();
            entity.Property(e => e.ReviewerNoteCode).HasConversion<int>();

            entity.Property(e => e.CapabilityCode)
                  .IsRequired()
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.CapabilityCodeMaxLength);
            entity.Property(e => e.ToolName)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.ToolNameMaxLength);
            entity.Property(e => e.FailureCode)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.FailureCodeMaxLength);

            entity.Property(e => e.EvidenceMessageId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);
            entity.Property(e => e.EvidenceOfferMessageId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);
            entity.Property(e => e.WriteOperationId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);
            entity.Property(e => e.RelatedOpportunityId)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.IdMaxLength);

            entity.Property(e => e.Fingerprint)
                  .IsRequired()
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.FingerprintMaxLength);
            entity.Property(e => e.DedupBucketDate).IsRequired();
            entity.Property(e => e.OccurrenceCount).IsRequired();
            entity.Property(e => e.FirstObservedAtUtc).IsRequired();
            entity.Property(e => e.LastObservedAtUtc).IsRequired();

            entity.Property(e => e.LinkedSpecPath)
                  .HasMaxLength(Opportunities.CoachOpportunityLimits.LinkedSpecPathMaxLength);
            entity.Property(e => e.EvidenceRevealCount).IsRequired();
            entity.Property(e => e.SchemaVersion).IsRequired();
            entity.Property(e => e.Version).IsRequired().IsConcurrencyToken();

            // The dedup invariant: one row per (learner, problem, UTC day). Unique rather than
            // advisory, because the upsert relies on it: the recorder issues a single
            // ON CONFLICT statement against this constraint, which is what makes a concurrent
            // second occurrence increment a count instead of inserting a duplicate row.
            entity.HasIndex(e => new { e.UserProfileId, e.Fingerprint, e.DedupBucketDate })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachOpportunity_UserProfileId_Fingerprint_DedupBucketDate");

            // Drives the operator triage list.
            entity.HasIndex(e => new { e.Status, e.LastObservedAtUtc })
                  .HasDatabaseName("IX_CoachOpportunity_Status_LastObservedAtUtc");

            // Drives the cross-learner rollup's grouping and its recency filter.
            entity.HasIndex(e => new { e.Kind, e.CapabilityCode, e.LastObservedAtUtc })
                  .HasDatabaseName("IX_CoachOpportunity_Kind_CapabilityCode_LastObservedAtUtc");

            // Drives owner-scoped reads, including the related-row lookup the referent-loss
            // detector uses to chain a follow-up to the refusal that preceded it.
            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId })
                  .HasDatabaseName("IX_CoachOpportunity_UserProfileId_ConversationId");

            // Drives the retention sweep.
            entity.HasIndex(e => e.LastObservedAtUtc)
                  .HasDatabaseName("IX_CoachOpportunity_LastObservedAtUtc");
        });

        modelBuilder.Entity<Reports.CoachResponseReport>(entity =>
        {
            entity.ToTable("CoachResponseReport");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                  .ValueGeneratedNever()
                  .HasMaxLength(Reports.CoachResponseReportLimits.IdMaxLength);

            entity.Property(e => e.UserProfileId)
                  .IsRequired()
                  .HasMaxLength(Reports.CoachResponseReportLimits.UserProfileIdMaxLength);
            entity.Property(e => e.TenantId)
                  .HasMaxLength(Reports.CoachResponseReportLimits.TenantIdMaxLength);
            entity.Property(e => e.ConversationId)
                  .IsRequired()
                  .HasMaxLength(Reports.CoachResponseReportLimits.IdMaxLength);

            entity.Property(e => e.CoachMessageId)
                  .IsRequired()
                  .HasMaxLength(Reports.CoachResponseReportLimits.IdMaxLength);
            entity.Property(e => e.RequestMessageId)
                  .IsRequired()
                  .HasMaxLength(Reports.CoachResponseReportLimits.IdMaxLength);

            entity.Property(e => e.Reason).HasConversion<int>();
            entity.Property(e => e.ResponseKind).HasConversion<int>();
            entity.Property(e => e.TurnStatus).HasConversion<int>();
            entity.Property(e => e.StopReason).HasConversion<int>();
            entity.Property(e => e.WriteStatus).HasConversion<int>();

            entity.Property(e => e.TurnOperationId)
                  .HasMaxLength(Reports.CoachResponseReportLimits.IdMaxLength);
            entity.Property(e => e.TurnErrorCode)
                  .HasMaxLength(Reports.CoachResponseReportLimits.FailureCodeMaxLength);
            entity.Property(e => e.InvokedToolNames)
                  .HasMaxLength(Reports.CoachResponseReportLimits.InvokedToolNamesMaxLength);
            entity.Property(e => e.WriteOperationId)
                  .HasMaxLength(Reports.CoachResponseReportLimits.IdMaxLength);
            entity.Property(e => e.WriteFailureCode)
                  .HasMaxLength(Reports.CoachResponseReportLimits.FailureCodeMaxLength);
            entity.Property(e => e.OpportunityId)
                  .HasMaxLength(Reports.CoachResponseReportLimits.IdMaxLength);

            entity.Property(e => e.ReportedAtUtc).IsRequired();
            entity.Property(e => e.SchemaVersion).IsRequired();

            // Grounding evidence, schema version 2. Nullable by design and by contract: null is
            // the reading for a rung of Off, for a row written before these columns, and for an
            // outcome this build could not read. Bounded on the one string.
            entity.Property(e => e.GroundingRuleCodes)
                  .HasMaxLength(Reports.CoachResponseReportLimits.GroundingRuleCodesMaxLength);

            // The idempotency invariant: one report per (learner, coach response), for the life of
            // the row. Unique rather than advisory, because that is what makes the guarantee hold
            // across instances: two replicas racing on the same response both attempt the insert
            // and the database — not a read-then-write in either process — decides that exactly
            // one of them wrote it. The loser reads the winner's row and answers AlreadyReported,
            // which is the same answer a learner gets after a reload.
            //
            // Rooted in UserProfileId, so it is also an ownership statement: two learners can
            // never contend for the same key, and a message id from another account cannot
            // collide with one of this learner's rows.
            entity.HasIndex(e => new { e.UserProfileId, e.CoachMessageId })
                  .IsUnique()
                  .HasDatabaseName("IX_CoachResponseReport_UserProfileId_CoachMessageId");

            // Drives the per-conversation read the chat pane makes on entry and after a resume,
            // so "which of these responses did I already report" is one indexed lookup rather
            // than a scan of the learner's whole report history.
            entity.HasIndex(e => new { e.UserProfileId, e.ConversationId })
                  .HasDatabaseName("IX_CoachResponseReport_UserProfileId_ConversationId");

            // Drives the retention sweep.
            entity.HasIndex(e => e.ReportedAtUtc)
                  .HasDatabaseName("IX_CoachResponseReport_ReportedAtUtc");
        });
    }
}
