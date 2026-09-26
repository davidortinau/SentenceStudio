using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SentenceStudio.Data;
using SentenceStudio.Data.AppOperations;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationProviderModelParityTests
{
    private static readonly Type[] EntityTypes =
    [
        typeof(ApplicationOperationRecord),
        typeof(ApplicationProtectedPayloadRecord),
        typeof(ApplicationOperationConfirmationRecord),
        typeof(ApplicationOperationReceiptRecord),
        typeof(ApplicationOperationContinuationRecord),
        typeof(ApplicationOperationEventRecord)
    ];

    [Fact]
    public void SqliteAndPostgresModelsHaveEquivalentLedgerShape()
    {
        using var sqlite = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        using var postgres = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only;Username=model")
                .Options);

        foreach (var clrType in EntityTypes)
        {
            var sqliteShape = Shape(
                sqlite.GetService<IDesignTimeModel>().Model.FindEntityType(clrType)!);
            var postgresShape = Shape(
                postgres.GetService<IDesignTimeModel>().Model.FindEntityType(clrType)!);

            sqliteShape.Should().BeEquivalentTo(
                postgresShape,
                options => options.WithStrictOrdering(),
                clrType.Name);
        }
    }

    private static object Shape(IEntityType entity) =>
        new
        {
            entity.Name,
            Table = entity.GetTableName(),
            Properties = entity.GetProperties()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new
                {
                    property.Name,
                    ClrType = property.ClrType.FullName,
                    property.IsNullable,
                    property.IsConcurrencyToken,
                    MaxLength = property.GetMaxLength()
                })
                .ToArray(),
            Key = entity.FindPrimaryKey()!.Properties
                .Select(property => property.Name)
                .ToArray(),
            Indexes = entity.GetIndexes()
                .Select(index => new
                {
                    Properties = index.Properties.Select(property => property.Name).ToArray(),
                    index.IsUnique,
                    Filter = index.GetFilter()
                })
                .OrderBy(index => string.Join(",", index.Properties), StringComparer.Ordinal)
                .ToArray(),
            ForeignKeys = entity.GetForeignKeys()
                .Select(key => new
                {
                    Properties = key.Properties.Select(property => property.Name).ToArray(),
                    Principal = key.PrincipalEntityType.ClrType.FullName,
                    PrincipalProperties = key.PrincipalKey.Properties
                        .Select(property => property.Name)
                        .ToArray(),
                    key.DeleteBehavior
                })
                .OrderBy(key => string.Join(",", key.Properties), StringComparer.Ordinal)
                .ToArray(),
            Checks = entity.GetCheckConstraints()
                .Select(check => new { check.Name, check.Sql })
                .OrderBy(check => check.Name, StringComparer.Ordinal)
                .ToArray()
        };
}
