using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin.Persistence;

namespace SentenceStudio.Api.Admin;

/// <summary>The closed set of things an admin request can be. One per audited route.</summary>
public static class AdminActions
{
    public const string SessionRead = "session.read";
    public const string StepUpVerify = "step_up.verify";
    public const string AuditRead = "audit.read";
    public const string SyncHealthAppRead = "sync_health.app_read";
    public const string SyncHealthLearnersRead = "sync_health.learners_read";
    public const string SyncHealthLearnerRead = "sync_health.learner_read";
}

/// <summary>The closed set of things an admin request can act on.</summary>
public static class AdminTargetKinds
{
    public const string Learner = "learner";
    public const string Report = "report";
}

/// <summary>
/// How the audit log's two enums are written on the wire. Fixed strings, not the C# member names,
/// so renaming a member cannot silently change what the Audit page and the agent read.
/// </summary>
public static class AdminAuditWire
{
    public const string ActorAdmin = "admin";
    public const string ActorAgentToken = "agent_token";
    public const string OutcomeStarted = "started";
    public const string OutcomeSucceeded = "succeeded";
    public const string OutcomeFailed = "failed";
    public const string Unknown = "unknown";

    public static string Of(AdminActorKind kind) => kind switch
    {
        AdminActorKind.Admin => ActorAdmin,
        AdminActorKind.AgentToken => ActorAgentToken,
        _ => Unknown
    };

    public static string Of(AdminAuditOutcome outcome) => outcome switch
    {
        AdminAuditOutcome.Started => OutcomeStarted,
        AdminAuditOutcome.Succeeded => OutcomeSucceeded,
        AdminAuditOutcome.Failed => OutcomeFailed,
        _ => Unknown
    };
}

/// <summary>Marks every route under the admin prefix. Set once, on the group.</summary>
public sealed class AdminRouteMetadata
{
    public static readonly AdminRouteMetadata Instance = new();
}

/// <summary>What an admin route records in the audit log.</summary>
/// <param name="Action">A code from <see cref="AdminActions"/>.</param>
/// <param name="TargetKind">A code from <see cref="AdminTargetKinds"/>, when the route acts on one thing.</param>
/// <param name="TargetRouteValue">The route parameter that carries the target's id.</param>
public sealed record AdminActionMetadata(string Action, string? TargetKind = null, string? TargetRouteValue = null);

/// <summary>
/// Marks an admin route that deliberately leaves no audit row, and says why. The complete list is
/// pinned by <c>AdminEndpointInventoryTests</c>.
/// </summary>
public sealed record AdminUnauditedMetadata(string Reason);

public static class AdminAuditRouteExtensions
{
    /// <summary>Every request the route admits leaves an audit row with this action.</summary>
    public static TBuilder WithAdminAction<TBuilder>(
        this TBuilder builder, string action, string? targetKind = null, string? targetRouteValue = null)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AdminActionMetadata(action, targetKind, targetRouteValue));

    /// <summary>The route leaves no audit row. Only for routes that read and change nothing.</summary>
    public static TBuilder WithoutAdminAudit<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AdminUnauditedMetadata(reason));
}

/// <summary>
/// Writes the audit row for one admitted request: inserted before the handler runs, completed
/// after it.
/// </summary>
/// <remarks>
/// Each call uses its own context, so the audit never shares a change tracker or a failed
/// command with the handler it is recording.
/// </remarks>
internal sealed class AdminAuditWriter(DbContextOptions<AdminDbContext> options, TimeProvider time)
{
    public async Task<long> BeginAsync(
        AdminActor actor, AdminActionMetadata action, string? targetId, CancellationToken cancellationToken)
    {
        var entry = new AdminAuditEntry
        {
            OccurredAtUtc = time.GetUtcNow().UtcDateTime,
            ActorKind = actor.Kind,
            ActorId = actor.Id,
            Action = action.Action,
            TargetKind = action.TargetKind,
            TargetId = targetId,
            Outcome = AdminAuditOutcome.Started
        };

        await using var db = new AdminDbContext(options);
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry.Id;
    }

    public async Task CompleteAsync(
        long id, AdminAuditOutcome outcome, int statusCode, string? failureCode, CancellationToken cancellationToken)
    {
        await using var db = new AdminDbContext(options);
        await db.AuditEntries
            .Where(e => e.Id == id && e.Outcome == AdminAuditOutcome.Started)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Outcome, outcome)
                    .SetProperty(e => e.StatusCode, statusCode)
                    .SetProperty(e => e.FailureCode, failureCode),
                cancellationToken);
    }
}

/// <summary>
/// Wraps every admin route: refuses a route the policy did not admit, forbids caching, and writes
/// the audit row around the handler.
/// </summary>
/// <remarks>
/// <para>
/// Runs after authorization. A refused caller never reaches it (the result handler has already
/// answered 404), so a request here with no admitted caller means a route was mapped under the
/// admin group without the policy, and it is answered 404 as well rather than served.
/// </para>
/// <para>
/// <b>Fail closed.</b> The audit row is inserted before the handler runs; if that insert fails the
/// request fails and nothing is read or changed. A route with neither an action nor an explicit
/// no-audit marker is refused for the same reason.
/// </para>
/// </remarks>
internal sealed class AdminRequestMiddleware(RequestDelegate next, ILogger<AdminRequestMiddleware> logger)
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    public async Task InvokeAsync(HttpContext http)
    {
        var endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AdminRouteMetadata>() is null)
        {
            await next(http);
            return;
        }

        var actor = http.FindAdminActor();
        if (actor is null)
        {
            logger.LogError(
                "Admin route {Endpoint} was reached without the admin policy; refusing.", endpoint.DisplayName);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Nothing an admin route answers may be re-served by a browser, a proxy or a back button.
        http.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";

        var action = endpoint.Metadata.GetMetadata<AdminActionMetadata>();
        if (action is null)
        {
            if (endpoint.Metadata.GetMetadata<AdminUnauditedMetadata>() is null)
            {
                logger.LogError(
                    "Admin route {Endpoint} declares neither an audit action nor a no-audit reason; refusing.",
                    endpoint.DisplayName);
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            await next(http);
            return;
        }

        string? targetId = null;
        if (action.TargetRouteValue is { } routeValue)
        {
            targetId = http.GetRouteValue(routeValue)?.ToString();

            // Every real learner and report id fits the column. One that does not names nothing.
            if (string.IsNullOrEmpty(targetId) || targetId.Length > AdminAuditLimits.MaxIdLength)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        var audit = http.RequestServices.GetRequiredService<AdminAuditWriter>();
        var auditId = await audit.BeginAsync(actor, action, targetId, http.RequestAborted);
        http.Features.Set(new AdminAuditFeature(auditId));

        try
        {
            await next(http);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested;
            await CompleteAsync(
                audit, auditId, AdminAuditOutcome.Failed,
                cancelled ? StatusCodes.Status499ClientClosedRequest : StatusCodes.Status500InternalServerError,
                cancelled ? AdminFailureCodes.Cancelled : AdminFailureCodes.Exception);
            throw;
        }

        var status = http.Response.StatusCode;
        await CompleteAsync(
            audit, auditId,
            status < StatusCodes.Status400BadRequest ? AdminAuditOutcome.Succeeded : AdminAuditOutcome.Failed,
            status,
            AdminRefusal.FindCode(http));
    }

    /// <summary>
    /// Records how the request ended. A failure here is logged and not rethrown: the request is
    /// already on record as started, and what it did has already happened.
    /// </summary>
    /// <remarks>
    /// Awaited before the response ends rather than deferred to after it, so a caller that has
    /// its answer can rely on the row being complete: the next read of the log shows this request
    /// with its ending, not as one whose ending was lost.
    /// </remarks>
    private async Task CompleteAsync(
        AdminAuditWriter audit, long auditId, AdminAuditOutcome outcome, int statusCode, string? failureCode)
    {
        try
        {
            // Not the request's token: a caller who disconnects must not erase the ending.
            using var timeout = new CancellationTokenSource(CompletionTimeout);
            await audit.CompleteAsync(auditId, outcome, statusCode, failureCode, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin audit row {AuditId} could not be completed; it stays Started.", auditId);
        }
    }
}
