using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoreSync;
using CoreSync.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// <c>Sync:AllowHeaderlessClients</c> (Captain 2026-11-04, decision 1; GitHub #204). A sync
/// request without the owner header gets no owner checks, so with the switch closed (the
/// default in every environment) every CoreSync route refuses it, and the owner-checked path
/// keeps working. Runs against a throwaway PostgreSQL database.
/// </summary>
public sealed class HeaderlessSyncSwitchPostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public HeaderlessSyncSwitchPostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task HeaderlessRequests_AreRefusedOnEveryCoreSyncRoute_AndChangeNothing()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var foreignResource = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = foreignResource, UserProfileId = foreign, Title = "Foreign"
            });
            await db.SaveChangesAsync();
        }

        using var client = _fixture.CreateClient();
        var storeId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var stolenId = Guid.NewGuid().ToString();
        var stolen = new SyncItem("LearningResource", ChangeType.Insert,
            new Dictionary<string, object?>
            {
                ["Id"] = stolenId, ["UserProfileId"] = owner, ["Title"] = "Headerless write",
                ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow,
                ["IsSmartResource"] = false
            });

        var responses = new List<(string Route, HttpStatusCode Status)>();
        async Task Check(string route, Func<Task<HttpResponseMessage>> send)
        {
            using var response = await send();
            responses.Add((route, response.StatusCode));
        }

        await Check("store-id", () => _fixture.GetAsync(
            client, "/api/sync-agent/store-id", owner, nativeHeader: false));
        await Check("changes-bulk", () => _fixture.GetAsync(
            client, $"/api/sync-agent/changes-bulk/{storeId}", owner, nativeHeader: false));
        await Check("changes-bulk-item", () => _fixture.GetAsync(
            client, $"/api/sync-agent/changes-bulk-item/{sessionId}/0/100", owner,
            nativeHeader: false));
        await Check("changes-bulk-item-binary", () => _fixture.GetAsync(
            client, $"/api/sync-agent/changes-bulk-item-binary/{sessionId}/0/100", owner,
            nativeHeader: false));
        await Check("changes-bulk-begin", () => _fixture.BeginUploadAsync(
            client, owner, sessionId, storeId, 1, nativeHeader: false));
        await Check("changes-bulk-item", () => _fixture.UploadChunkAsync(
            client, owner, sessionId, [stolen], nativeHeader: false));
        await Check("changes-bulk-item-binary", () => _fixture.UploadBinaryChunkAsync(
            client, owner, sessionId, [stolen], nativeHeader: false));
        await Check("changes-bulk-complete", () => _fixture.CompleteUploadAsync(
            client, owner, sessionId, nativeHeader: false));
        await Check("changes-bulk-complete-binary", () => _fixture.CompleteUploadAsync(
            client, owner, sessionId, binary: true, nativeHeader: false));
        await Check("save-version", () => _fixture.SaveVersionAsync(
            client, owner, storeId, 1, nativeHeader: false));

        Assert.Equal(10, responses.Count);
        Assert.All(responses, response => Assert.True(
            response.Status == HttpStatusCode.Forbidden,
            $"{response.Route} returned {(int)response.Status}"));

        await using (var db = _fixture.NewContext())
            Assert.False(await db.LearningResources.AnyAsync(row => row.Id == stolenId));
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM __core_sync_remote_anchor WHERE id LIKE @store", connection);
        command.Parameters.AddWithValue("store", $"%{storeId:N}%");
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM __core_sync_remote_anchor WHERE id = @store";
        command.Parameters.AddWithValue("store", storeId.ToString());
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [ApplicationOperationPostgresFact]
    public async Task UnauthenticatedRequest_IsStillUnauthorized()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/api/sync-agent/store-id");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnerHeader_StillUploadsAndDownloads_OnlyTheOwnersRows()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var foreignResource = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = foreignResource, UserProfileId = foreign, Title = "Foreign"
            });
            await db.SaveChangesAsync();
        }

        var ownId = Guid.NewGuid().ToString();
        using (var uploaded = await _fixture.UploadAsync(owner, new SyncItem(
            "LearningResource", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = ownId, ["UserProfileId"] = owner, ["Title"] = "Mine",
                ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow,
                ["IsSmartResource"] = false
            })))
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        using var client = _fixture.CreateClient();
        var session = await _fixture.StartDownloadAsync(client, owner);
        using var chunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item/{session.SessionId}/0/{Math.Max(1, session.TotalChanges)}",
            owner);
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        var items = await chunk.Content.ReadFromJsonAsync<SyncItem[]>() ?? [];
        var resources = items.Where(item => item.TableName == "LearningResource")
            .Select(item => item.Values["Id"].Value?.ToString()).ToArray();
        Assert.Contains(ownId, resources);
        Assert.DoesNotContain(foreignResource, resources);
    }
}
