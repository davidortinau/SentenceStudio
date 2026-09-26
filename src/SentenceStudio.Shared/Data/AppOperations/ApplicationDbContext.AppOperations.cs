using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data.AppOperations;

namespace SentenceStudio.Data;

public partial class ApplicationDbContext
{
    public DbSet<ApplicationOperationRecord> ApplicationOperations =>
        Set<ApplicationOperationRecord>();

    public DbSet<ApplicationProtectedPayloadRecord> ApplicationProtectedPayloads =>
        Set<ApplicationProtectedPayloadRecord>();

    public DbSet<ApplicationOperationConfirmationRecord> ApplicationOperationConfirmations =>
        Set<ApplicationOperationConfirmationRecord>();

    public DbSet<ApplicationOperationReceiptRecord> ApplicationOperationReceipts =>
        Set<ApplicationOperationReceiptRecord>();

    public DbSet<ApplicationOperationContinuationRecord> ApplicationOperationContinuations =>
        Set<ApplicationOperationContinuationRecord>();

    public DbSet<ApplicationOperationEventRecord> ApplicationOperationEvents =>
        Set<ApplicationOperationEventRecord>();
}
