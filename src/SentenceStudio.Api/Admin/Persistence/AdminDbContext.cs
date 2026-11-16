using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SentenceStudio.Api.Admin.Persistence;

/// <summary>
/// Server-only PostgreSQL context for the admin portal's own records, starting with the audit log.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>ApplicationDbContext</c> for the same reasons the coach and feedback contexts
/// are: nothing here may sync to a device, so it must not be enrolled in CoreSync, and a
/// single-provider schema needs no SQLite twin migration. It keeps its own
/// <c>__AdminMigrationsHistory</c> table.
/// </para>
/// <para>
/// <c>PendingModelChangesWarning</c> is not suppressed for this context anywhere;
/// <c>AdminModelMigrationParityTests</c> fails first when the model and the migrations disagree.
/// </para>
/// </remarks>
public sealed class AdminDbContext : DbContext
{
    public AdminDbContext(DbContextOptions<AdminDbContext> options) : base(options)
    {
    }

    public DbSet<AdminAuditEntry> AuditEntries => Set<AdminAuditEntry>();

    public DbSet<AdminAgentToken> AgentTokens => Set<AdminAgentToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AdminAuditEntry>(entity =>
        {
            entity.ToTable("AdminAuditEntry");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ActorKind).HasConversion<int>();
            entity.Property(e => e.ActorId).IsRequired().HasMaxLength(AdminAuditLimits.MaxIdLength);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(AdminAuditLimits.MaxCodeLength);
            entity.Property(e => e.TargetKind).HasMaxLength(AdminAuditLimits.MaxTargetKindLength);
            entity.Property(e => e.TargetId).HasMaxLength(AdminAuditLimits.MaxIdLength);
            entity.Property(e => e.Outcome).HasConversion<int>();
            entity.Property(e => e.FailureCode).HasMaxLength(AdminAuditLimits.MaxCodeLength);

            // Retention prunes on this, and the Audit page reads newest first.
            entity.HasIndex(e => e.OccurredAtUtc)
                  .HasDatabaseName("IX_AdminAuditEntry_OccurredAtUtc");

            // "Who has looked at this learner?"
            entity.HasIndex(e => new { e.TargetKind, e.TargetId })
                  .HasDatabaseName("IX_AdminAuditEntry_Target");

            // "What has this admin or token done?"
            entity.HasIndex(e => e.ActorId)
                  .HasDatabaseName("IX_AdminAuditEntry_ActorId");
        });

        modelBuilder.Entity<AdminAgentToken>(entity =>
        {
            entity.ToTable("AdminAgentToken");

            // Assigned by the server when the token is created, never by the database: it is half
            // of the token the admin is shown.
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(AdminAgentTokenLimits.IdLength);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(AdminAgentTokenLimits.MaxNameLength);
            entity.Property(e => e.SecretHash).IsRequired().HasMaxLength(AdminAgentTokenLimits.SecretHashLength);
            entity.Property(e => e.Permissions).HasConversion<int>();
            entity.Property(e => e.CreatedByUserId).IsRequired().HasMaxLength(AdminAuditLimits.MaxIdLength);
            entity.Property(e => e.CreatedByStampDigest).IsRequired().HasMaxLength(AdminAgentTokenLimits.SecretHashLength);
        });

        PinTimestampsToUtc(modelBuilder);
    }

    /// <summary>
    /// Pins every <see cref="DateTime"/> to <c>timestamp with time zone</c>, read and written as UTC.
    /// </summary>
    /// <remarks>
    /// The API sets <c>Npgsql.EnableLegacyTimestampBehavior</c> for the whole process, which would
    /// otherwise map these columns to <c>timestamp without time zone</c> (not what the migration
    /// creates) and read them back in the host's local zone. See the same pin on
    /// <c>FeedbackDbContext</c> for the long form.
    /// </remarks>
    private void PinTimestampsToUtc(ModelBuilder modelBuilder)
    {
        if (!Database.IsNpgsql())
        {
            return;
        }

        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
        {
            property.SetColumnType("timestamp with time zone");
            property.SetValueConverter(UtcDateTimeConverter);
        }
    }

    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter = new(
        value => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime(),
        value => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());
}

/// <summary>Schema-level constants shared by the host, the design-time factory, and the tests.</summary>
public static class AdminSchema
{
    /// <summary>The admin migration set's own history table.</summary>
    public const string MigrationsHistoryTable = "__AdminMigrationsHistory";
}

/// <summary>Column bounds for <see cref="AdminAuditEntry"/>.</summary>
public static class AdminAuditLimits
{
    public const int MaxIdLength = 64;
    public const int MaxCodeLength = 64;
    public const int MaxTargetKindLength = 32;
}
