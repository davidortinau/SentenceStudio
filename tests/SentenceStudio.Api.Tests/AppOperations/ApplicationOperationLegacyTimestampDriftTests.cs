using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using Xunit;

namespace SentenceStudio.Api.Tests.AppOperations;

/// <summary>
/// The API, WebApp and Workers run with <c>Npgsql.EnableLegacyTimestampBehavior</c>, so the runtime
/// model maps every DateTime property to "timestamp without time zone" while the PostgreSQL
/// snapshot was scaffolded without the switch (timestamptz). Production columns are timestamptz and
/// must stay that way, which is why the hosts ignore PendingModelChangesWarning. This test keeps
/// that exception narrow: the only permitted difference between snapshot and runtime model is a
/// timestamptz to timestamp type-mapping alter. Any other pending model change fails here.
/// </summary>
public class ApplicationOperationLegacyTimestampDriftTests
{
    static ApplicationOperationLegacyTimestampDriftTests()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    [Fact]
    public void RuntimeModelDiffersFromSnapshotOnlyByLegacyTimestampTypeMapping()
    {
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only;Username=model")
                .Options);

        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var snapshotModel = migrationsAssembly.ModelSnapshot?.Model
            ?? throw new InvalidOperationException("No PostgreSQL model snapshot was found.");
        var finalSnapshot = db.GetService<IModelRuntimeInitializer>()
            .Initialize((IModel)snapshotModel);
        var runtimeModel = db.GetService<IDesignTimeModel>().Model;

        var operations = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(finalSnapshot.GetRelationalModel(), runtimeModel.GetRelationalModel());

        var unexpected = operations
            .Where(operation => !IsLegacyTimestampAlter(operation))
            .Select(operation => $"{operation.GetType().Name}: {Describe(operation)}")
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            "The runtime model differs from the PostgreSQL snapshot by more than the known legacy " +
            "timestamp mapping. Add a migration for:" + Environment.NewLine + string.Join(Environment.NewLine, unexpected));
    }

    private static bool IsLegacyTimestampAlter(MigrationOperation operation) =>
        operation is AlterColumnOperation alter
        && alter.OldColumn.ColumnType == "timestamp with time zone"
        && alter.ColumnType == "timestamp without time zone"
        && alter.ClrType == typeof(DateTime);

    private static string Describe(MigrationOperation operation) => operation switch
    {
        AlterColumnOperation alter => $"{alter.Table}.{alter.Name} {alter.OldColumn.ColumnType} -> {alter.ColumnType}",
        ITableMigrationOperation table => table.Table,
        _ => operation.ToString() ?? string.Empty
    };
}
