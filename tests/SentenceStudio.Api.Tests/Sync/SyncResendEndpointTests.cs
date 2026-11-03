using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Sync;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>POST /api/v1/sync/resend re-marks only the caller's rows of newly synced tables.</summary>
public sealed class SyncResendEndpointTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Fact]
    public async Task RefusesWithoutOwner_AndRefusesTablesThatAreNotNewlySynced()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);

        Assert.IsType<UnauthorizedHttpResult>(await SyncResendEndpoint.HandleAsync(
            null, new SyncResendRequest(["StreamHistory"]), db, NullLogger.Instance, default));
        Assert.IsType<UnauthorizedHttpResult>(await SyncResendEndpoint.HandleAsync(
            " ", new SyncResendRequest(["StreamHistory"]), db, NullLogger.Instance, default));
        foreach (var tables in new[] { Array.Empty<string>(), ["DiaryEntry"], ["StreamHistory", "RefreshTokens"] })
            Assert.Equal(StatusCodes.Status400BadRequest, Status(await SyncResendEndpoint.HandleAsync(
                Owner, new SyncResendRequest(tables), db, NullLogger.Instance, default)));
    }

    [Fact]
    public async Task TouchesTheOwnersRowsOfTheRequestedTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateAsync(connection);
        db.StreamHistories.AddRange(
            new StreamHistory { Id = "mine", UserProfileId = Owner },
            new StreamHistory { Id = "theirs", UserProfileId = "someone-else" });
        await db.SaveChangesAsync();

        Assert.IsType<NoContent>(await SyncResendEndpoint.HandleAsync(Owner,
            new SyncResendRequest(["StreamHistory", "MinimalPair"]), db, NullLogger.Instance, default));
        // Rows still exist unchanged (a touch writes no values).
        Assert.Equal(2, await db.StreamHistories.CountAsync());
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static async Task<ApplicationDbContext> CreateAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
