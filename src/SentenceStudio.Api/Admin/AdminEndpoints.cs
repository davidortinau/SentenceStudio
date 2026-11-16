using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Admin;

/// <summary>
/// The <c>/api/admin</c> routes the admin pages and the maintenance agent call.
/// </summary>
/// <remarks>
/// <para>
/// Every route is mapped on one group, and the group carries the whole gate: the admin policy
/// (role in the database and email on <c>Admin:Emails</c>, checked on each request, 404 for
/// everyone else) and the marker <see cref="AdminRequestMiddleware"/> audits by. A route added
/// here is covered by construction, and must say what it records with
/// <see cref="AdminAuditRouteExtensions.WithAdminAction"/>.
/// </para>
/// <para>
/// Always mapped, in every environment. Who may use them is decided per request, so the same
/// build serves a host with no admins (an empty list) and one with several.
/// </para>
/// </remarks>
public static class AdminEndpoints
{
    public const string RoutePrefix = "/api/admin";

    private const int DefaultAuditPageSize = 50;
    private const int MaxAuditPageSize = 200;

    /// <summary>No real password is longer; a larger body is refused rather than hashed.</summary>
    private const int MaxPasswordLength = 256;

    /// <summary>
    /// The one group every admin route is mapped on. It carries the policy and the marker the
    /// audit middleware works by; a route mapped anywhere else is not an admin route.
    /// </summary>
    public static RouteGroupBuilder MapAdminGroup(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.MapGroup(RoutePrefix)
            .RequireAuthorization(AdminPolicy.Name)
            .WithMetadata(AdminRouteMetadata.Instance);
    }

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapAdminGroup();

        group.MapGet("/access", () => Results.NoContent())
            .WithName("ProbeAdminAccess")
            .WithoutAdminAudit("Answers only whether to show the Admin link; reads and changes nothing.");

        group.MapGet("/session", GetSession)
            .WithName("GetAdminSession")
            .WithAdminAction(AdminActions.SessionRead);

        group.MapPost("/step-up", StepUpAsync)
            .WithName("VerifyAdminPassword")
            .WithAdminAction(AdminActions.StepUpVerify);

        group.MapGet("/audit", ListAuditAsync)
            .WithName("ListAdminAudit")
            .WithAdminAction(AdminActions.AuditRead);

        group.MapAdminSyncHealthEndpoints();
        group.MapAdminUsageEndpoints();

        // Everything else under the prefix, for every method. Without it a wrong verb on a real
        // route would be answered 405 before authorization ran, which tells anyone the route exists.
        group.Map("/{**rest}", () => Results.NotFound())
            .WithoutAdminAudit("No such admin route.");

        return app;
    }

    private static IResult GetSession(HttpContext http, [FromServices] AdminStepUpService stepUp)
    {
        var actor = http.GetAdminActor();
        return Results.Ok(new AdminSessionResponse(
            actor.Id, actor.Email, actor.DisplayName, stepUp.Validate(http)));
    }

    /// <summary>
    /// Checks the admin's password again and issues the proof dangerous routes ask for.
    /// </summary>
    /// <remarks>
    /// A stolen session cannot be used to guess the password at speed: see
    /// <see cref="AdminPasswordCheck"/> for how the lockout holds under parallel requests.
    /// </remarks>
    private static async Task<IResult> StepUpAsync(
        [FromBody] AdminStepUpRequest request,
        HttpContext http,
        [FromServices] AdminPasswordCheck passwordCheck,
        [FromServices] AdminStepUpService stepUp,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var actor = http.GetAdminActor();
        if (actor.Kind != AdminActorKind.Admin)
        {
            return AdminRefusal.Create(http, StatusCodes.Status403Forbidden,
                AdminFailureCodes.NotAllowedForToken, "A token cannot confirm a password.");
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxPasswordLength)
        {
            return AdminRefusal.Create(http, StatusCodes.Status400BadRequest,
                AdminFailureCodes.InvalidRequest, "Password is required.");
        }

        var check = await passwordCheck.CheckAsync(actor.Id, request.Password, cancellationToken);
        switch (check.Verdict)
        {
            case AdminPasswordVerdict.Correct:
                // The check settles its count whatever the caller did meanwhile. A caller that
                // has left gets no proof, and the log says the attempt was abandoned, not that it
                // succeeded. Only here: a wrong guess or a lock is recorded as what it was, or
                // dropping the connection would hide guesses from the log.
                cancellationToken.ThrowIfCancellationRequested();
                break;

            case AdminPasswordVerdict.LockedOut:
                return AdminRefusal.Create(http, StatusCodes.Status429TooManyRequests,
                    AdminFailureCodes.LockedOut, "Too many failed attempts. Try again in a few minutes.");

            case AdminPasswordVerdict.NoAccount:
                // Admitted a moment ago and gone now. The same answer as never having been an admin.
                return Results.NotFound();

            default:
                return AdminRefusal.Create(http, StatusCodes.Status403Forbidden,
                    AdminFailureCodes.PasswordIncorrect, "That password is not correct.");
        }

        // Bound to the stamp read with the password just checked, not the one from the policy's
        // earlier read, so the proof belongs to exactly the credentials that earned it.
        var proof = stepUp.Issue(actor with { StampDigest = AdminStepUpService.DigestOf(check.SecurityStamp) });
        if (proof is null)
        {
            loggerFactory.CreateLogger("SentenceStudio.Api.Admin.StepUp")
                .LogError("Admin password proof could not be issued for user {UserId}", actor.Id);
            return AdminRefusal.Create(http, StatusCodes.Status409Conflict,
                AdminFailureCodes.StepUpUnavailable, "This account cannot confirm a password.");
        }

        return Results.Ok(new AdminStepUpResponse(proof.Token, proof.ExpiresAtUtc));
    }

    private static async Task<IResult> ListAuditAsync(
        HttpContext http,
        [FromServices] AdminDbContext adminDb,
        [FromServices] ApplicationDbContext db,
        CancellationToken cancellationToken,
        [FromQuery] string? before = null,
        [FromQuery] string? take = null)
    {
        // Bound as text and parsed here, so a malformed value is this route's own 400 in every
        // environment rather than the framework's binding failure.
        var pageSize = DefaultAuditPageSize;
        long? cursor = null;
        if ((take is not null && (!int.TryParse(take, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                                  || pageSize < 1 || pageSize > MaxAuditPageSize))
            || (before is not null && !TryParseCursor(before, out cursor)))
        {
            return AdminRefusal.Create(http, StatusCodes.Status400BadRequest,
                AdminFailureCodes.InvalidRequest,
                $"take must be 1 to {MaxAuditPageSize}; before must be a row id.");
        }

        var query = adminDb.AuditEntries.AsNoTracking();
        if (cursor is { } olderThan)
        {
            query = query.Where(e => e.Id < olderThan);
        }

        // This request's own row is open until the response is written. Listing it would show
        // every read of the log as a request whose ending was lost; it appears, completed, the
        // next time the log is read.
        if (http.FindAdminAuditEntryId() is { } ownEntry)
        {
            query = query.Where(e => e.Id != ownEntry);
        }

        // One more than asked for, to know whether an older page exists.
        var entries = await query
            .OrderByDescending(e => e.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = entries.Count > pageSize;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        var adminIds = entries
            .Where(e => e.ActorKind == AdminActorKind.Admin)
            .Select(e => e.ActorId)
            .Distinct()
            .ToList();

        var emails = adminIds.Count == 0
            ? new Dictionary<string, string?>()
            : await db.Users
                .AsNoTracking()
                .Where(u => adminIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        var items = entries
            .Select(e => new AdminAuditRow(
                e.Id,
                e.OccurredAtUtc,
                AdminAuditWire.Of(e.ActorKind),
                e.ActorId,
                e.ActorKind == AdminActorKind.Admin ? emails.GetValueOrDefault(e.ActorId) : null,
                e.Action,
                e.TargetKind,
                e.TargetId,
                AdminAuditWire.Of(e.Outcome),
                e.StatusCode,
                e.FailureCode))
            .ToList();

        return Results.Ok(new AdminAuditPage(items, hasMore ? items[^1].Id : null));
    }

    private static bool TryParseCursor(string value, out long? cursor)
    {
        var parsed = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
        cursor = parsed ? id : null;
        return parsed;
    }
}
