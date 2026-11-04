using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;
using SentenceStudio.Shared.Migrations;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// Applies the AddSyncDeletionLog migration's own operations to a database created with
/// EnsureCreated, which runs no migrations. The SQL is the migration's, so tests exercise the
/// triggers production gets.
/// </summary>
internal static class SyncDeletionLogSchema
{
    internal static async Task ApplyAsync(ApplicationDbContext db)
    {
        var migration = new AddSyncDeletionLog { ActiveProvider = db.Database.ProviderName };
        var commands = db.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.UpOperations, db.Model);
        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
    }
}
