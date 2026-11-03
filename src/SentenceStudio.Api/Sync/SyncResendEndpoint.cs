using System.Security.Claims;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// POST /api/v1/sync/resend: records the caller's own rows of late-joined tables as server
/// changes again, so a device whose older app version moved past them downloads them
/// (see <see cref="ISyncResendRequester"/>). Only the authenticated owner's rows of tables in
/// <see cref="SyncLateTables.OwnerColumns"/> are touched; nothing else is read or written.
/// </summary>
public static class SyncResendEndpoint
{
    public static IEndpointRouteBuilder MapSyncResend(this IEndpointRouteBuilder app)
    {
        app.MapPost(SyncResendQueue.Route, async (
                SyncResendRequest request, ClaimsPrincipal user, ApplicationDbContext db,
                ILoggerFactory loggers, CancellationToken cancellationToken) =>
            await HandleAsync(user.FindFirstValue(AuthClaimTypes.UserProfileId), request, db,
                loggers.CreateLogger("SyncResend"), cancellationToken))
            .RequireAuthorization();
        return app;
    }

    internal static async Task<IResult> HandleAsync(
        string? owner, SyncResendRequest? request, ApplicationDbContext db, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return Results.Unauthorized();
        var tables = request?.Tables?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (tables.Length == 0 || tables.Any(table => !SyncLateTables.OwnerColumns.ContainsKey(table)))
            return Results.BadRequest(new { error = "Only newly synced tables can be re-sent." });
        // Parents first, in the order the server's first-time touch uses.
        var ordered = SyncLateTables.OwnerColumns.Keys.Where(tables.Contains).ToArray();
        await SyncLateTables.TouchAsync(db, ordered, owner, logger, cancellationToken);
        return Results.NoContent();
    }
}
