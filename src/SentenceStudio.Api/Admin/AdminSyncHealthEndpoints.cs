using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Admin;

/// <summary>
/// Sync and data health, read-only: what the server holds for a learner, which device stores it
/// has synced with, and what is deployed. Counts, timestamps and ids; no row's content is read.
/// </summary>
/// <remarks>
/// These routes do not ask for the password again: they return numbers and no content. Each is
/// audited; a per-learner read names the learner as its target.
/// </remarks>
internal static class AdminSyncHealthEndpoints
{
    private const string Prefix = "/sync-health";
    private const int DefaultLearnerPageSize = 50;
    private const int MaxLearnerPageSize = 200;
    private const int MaxSearchLength = 100;
    private const int MaxIdLength = 64;

    public static RouteGroupBuilder MapAdminSyncHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet($"{Prefix}/app", GetAppAsync)
            .WithName("GetAdminSyncHealthApp")
            .WithAdminAction(AdminActions.SyncHealthAppRead);

        group.MapGet($"{Prefix}/learners", ListLearnersAsync)
            .WithName("ListAdminSyncHealthLearners")
            .WithAdminAction(AdminActions.SyncHealthLearnersRead);

        group.MapGet($"{Prefix}/learners/{{learnerId}}", GetLearnerAsync)
            .WithName("GetAdminSyncHealthLearner")
            .WithAdminAction(AdminActions.SyncHealthLearnerRead, AdminTargetKinds.Learner, "learnerId");

        return group;
    }

    private static async Task<IResult> GetAppAsync(
        [FromServices] ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        var (newest, oldest) = await AdminSyncHealthReader.ReadChangeLogRangeAsync(db, cancellationToken);

        return Results.Ok(new AdminSyncHealthApp(
            AdminSyncHealthReader.ApiVersion(),
            applied.Count == 0 ? null : applied[^1],
            applied.Count,
            pending,
            newest,
            oldest));
    }

    private static async Task<IResult> ListLearnersAsync(
        HttpContext http,
        [FromServices] ApplicationDbContext db,
        CancellationToken cancellationToken,
        [FromQuery] string? after = null,
        [FromQuery] string? take = null,
        [FromQuery] string? q = null)
    {
        // Bound as text and parsed here, so a malformed value is this route's own 400 in every
        // environment rather than the framework's binding failure.
        var pageSize = DefaultLearnerPageSize;
        if ((take is not null && (!int.TryParse(take, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                                  || pageSize < 1 || pageSize > MaxLearnerPageSize))
            || (after is not null && (after.Length == 0 || after.Length > MaxIdLength))
            || (q is not null && q.Length > MaxSearchLength))
        {
            return AdminRefusal.Create(http, StatusCodes.Status400BadRequest,
                AdminFailureCodes.InvalidRequest,
                $"take must be 1 to {MaxLearnerPageSize}; after must be a profile id; q is at most {MaxSearchLength} characters.");
        }

        var profiles = db.UserProfiles.AsNoTracking().AsQueryable();
        if (after is not null)
        {
            profiles = profiles.Where(p => p.Id.CompareTo(after) > 0);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLowerInvariant();
            profiles = profiles.Where(p =>
                (p.Email != null && p.Email.ToLower().Contains(needle))
                || (p.Name != null && p.Name.ToLower().Contains(needle))
                || db.Users.Any(u => u.UserProfileId == p.Id && u.Email != null && u.Email.ToLower().Contains(needle)));
        }

        // One more than asked for, to know whether a later page exists.
        var page = await profiles
            .OrderBy(p => p.Id)
            .Take(pageSize + 1)
            .Select(p => new { p.Id, p.Name, p.Email })
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > pageSize;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var ids = page.Select(p => p.Id).ToList();
        var accounts = ids.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(u => u.UserProfileId != null && ids.Contains(u.UserProfileId))
                .Select(u => new { u.UserProfileId, u.Email, u.DisplayName })
                .ToListAsync(cancellationToken);

        var items = page
            .Select(p =>
            {
                var account = accounts.FirstOrDefault(a => a.UserProfileId == p.Id);
                return new AdminLearnerSummary(p.Id, account?.DisplayName ?? p.Name, account?.Email ?? p.Email);
            })
            .ToList();

        return Results.Ok(new AdminLearnerPage(items, hasMore ? items[^1].ProfileId : null));
    }

    private static async Task<IResult> GetLearnerAsync(
        string learnerId,
        [FromServices] ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        // An unknown learner is the same answer as an unknown route.
        if (learnerId.Length > MaxIdLength)
        {
            return Results.NotFound();
        }

        var profile = await db.UserProfiles.AsNoTracking()
            .Where(p => p.Id == learnerId)
            .Select(p => new { p.Id, p.Name, p.Email })
            .SingleOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var account = await db.Users.AsNoTracking()
            .Where(u => u.UserProfileId == learnerId)
            .Select(u => new { u.Email, u.DisplayName })
            .FirstOrDefaultAsync(cancellationToken);
        var learner = new AdminLearnerSummary(
            profile.Id, account?.DisplayName ?? profile.Name, account?.Email ?? profile.Email);

        var (serverVersion, _) = await AdminSyncHealthReader.ReadChangeLogRangeAsync(db, cancellationToken);
        var tables = await AdminSyncHealthReader.ReadTablesAsync(db, learnerId, cancellationToken);
        var devices = await AdminSyncHealthReader.ReadDevicesAsync(db, learnerId, serverVersion, cancellationToken);
        DateTime? newest = tables.Where(t => t.NewestChangeUtc is not null).Select(t => t.NewestChangeUtc).Max();

        return Results.Ok(new AdminSyncHealthLearner(
            learner,
            tables,
            newest,
            devices,
            serverVersion,
            [AdminSyncNotRecorded.LastSyncTime, AdminSyncNotRecorded.RefusedRows]));
    }
}
