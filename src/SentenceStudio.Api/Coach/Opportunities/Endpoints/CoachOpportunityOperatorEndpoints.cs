using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Coach.Endpoints;

namespace SentenceStudio.Api.Coach.Opportunities.Endpoints;

/// <summary>
/// The Development-only <c>/api/v1/coach/operator/opportunities</c> surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four gates, all fail-closed, in this order:</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>Not mapped at all outside Development.</b> The routes do not exist, so a request 404s
/// rather than 403s — the coach never confirms that something exists but is off-limits, and a
/// 403 on an operator route is an advertisement.
/// </item>
/// <item>
/// <c>Coach:Opportunities:OperatorSurface:Enabled</c> must be true, checked per request so the
/// flag can be flipped without a redeploy.
/// </item>
/// <item>
/// <c>CoachOpportunityOptionsValidator</c> fails host startup if that flag is true outside
/// Development. Gate 1 stops the request; this stops the <em>deployment</em>, which matters
/// because configuration reload does not re-run route mapping.
/// </item>
/// <item>
/// <c>RequireAuthorization()</c>, and the caller's <c>user_profile_id</c> must be in
/// <c>Coach:AllowedUserProfileIds</c>. The <c>__dev_all__</c> sentinel is <b>not</b> honoured.
/// </item>
/// </list>
/// <para>
/// This is fail-closed gating rather than a role check because the host has no admin
/// authorization primitive. When one exists, gates 1 and 3 become a policy and gates 2 and 4 stay
/// exactly as they are.
/// </para>
/// </remarks>
public static class CoachOpportunityOperatorEndpoints
{
    /// <summary>The route prefix. Present only in Development.</summary>
    public const string RoutePrefix = "/api/v1/coach/operator/opportunities";

    /// <summary>
    /// The options the NDJSON export serializes with.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonSerializerDefaults.Web"/>, so the export and the JSON <c>/rollup</c> route —
    /// which <c>Results.Ok</c> writes with the host's web defaults — produce the same property
    /// names. Anything else means one consumer has to know which route it read from.
    /// </remarks>
    internal static readonly JsonSerializerOptions ExportSerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps the operator group, or maps nothing at all outside Development.
    /// </summary>
    public static IEndpointRouteBuilder MapCoachOpportunityOperator(
        this IEndpointRouteBuilder app,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(environment);

        // Gate 1. Nothing below this line exists in any other environment.
        if (!environment.IsDevelopment())
        {
            return app;
        }

        var group = app.MapGroup(RoutePrefix).RequireAuthorization();

        // Every response on this group is no-store. The evidence route returns decrypted learner
        // messages and is the reason the rule exists, but the row, listing, rollup, and export
        // routes carry an operator's triage view of a learner's problems and a browser, a proxy,
        // or a shared-machine back button must not be able to re-serve any of them. Applied to
        // the group rather than per-route so a route added later is covered by construction.
        group.AddEndpointFilter(async (context, next) =>
        {
            var response = context.HttpContext.Response;
            response.OnStarting(static state =>
            {
                var headers = ((HttpResponse)state).Headers;
                headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
                headers.Pragma = "no-cache";
                return Task.CompletedTask;
            }, response);

            return await next(context);
        });

        group.MapGet("/", ListAsync).WithName("ListCoachOpportunities");
        group.MapGet("/rollup", RollupAsync).WithName("RollupCoachOpportunities");
        group.MapGet("/export", ExportAsync).WithName("ExportCoachOpportunities");
        group.MapGet("/{id}", GetAsync).WithName("GetCoachOpportunity");
        group.MapPost("/{id}/review", ReviewAsync).WithName("ReviewCoachOpportunity");

        // POST, not GET, and deliberately so: a reveal must not be linkable, prefetchable,
        // cacheable, or reachable from a browser history entry.
        group.MapPost("/{id}/evidence", RevealEvidenceAsync).WithName("RevealCoachOpportunityEvidence");

        return app;
    }

    private static async Task<IResult> ListAsync(
        [FromServices] CoachOpportunityOperatorService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        [FromQuery] string? status = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? capabilityCode = null,
        [FromQuery] DateTime? since = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 0,
        [FromQuery] string? groundingStage = null,
        [FromQuery] bool? groundingRefused = null,
        [FromQuery] string? groundingRuleCode = null,
        [FromQuery] string? groundingLimitationCode = null)
    {
        try
        {
            if (!TryParseEnum<CoachOpportunityStatus>(status, out var parsedStatus)
                || !TryParseEnum<CoachOpportunityKind>(kind, out var parsedKind))
            {
                return Results.BadRequest();
            }

            // Refused rather than ignored. A grounding filter nobody could parse would answer a
            // broader question than the operator asked, and they would read the result as the
            // narrower one — which is how a rollout gets declared clean on the wrong query.
            if (!CoachGroundingReportFilter.TryParse(
                    groundingStage, groundingRefused, groundingRuleCode, groundingLimitationCode,
                    out var groundingFilter))
            {
                return Results.BadRequest();
            }

            var result = await service.ListAsync(
                parsedStatus, parsedKind, capabilityCode, since, skip, take, cancellationToken,
                groundingFilter);

            return ToResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, $"GET {RoutePrefix}", ex);
            return Results.Problem(
                detail: "The operator request failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> RollupAsync(
        [FromServices] CoachOpportunityOperatorService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        [FromQuery] DateTime? since = null)
    {
        try
        {
            var result = await service.RollupAsync(since, cancellationToken);
            return ToResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, $"GET {RoutePrefix}/rollup", ex);
            return Results.Problem(
                detail: "The operator request failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Streams the rollup as newline-delimited JSON, for pasting into a spec.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The export is the <em>rollup</em>, never the row list. That is what makes it safe to save
    /// to a file and share: it carries counts and closed-vocabulary codes and no owner
    /// identifier, so an exported artifact cannot become a per-learner record by accident.
    /// </para>
    /// <para>
    /// <b>Serialized with the web defaults, so every line is camelCase — the same casing
    /// <c>Results.Ok</c> produces on the sibling <c>/rollup</c> route.</b> Serializing with
    /// <c>JsonSerializer</c>'s own defaults produced PascalCase here and camelCase there, which
    /// meant a tool written against the JSON endpoint silently read nothing but nulls out of the
    /// export.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ExportAsync(
        [FromServices] CoachOpportunityOperatorService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        [FromQuery] DateTime? since = null)
    {
        try
        {
            var result = await service.RollupAsync(since, cancellationToken);
            if (!result.IsOk || result.Value is null)
            {
                return ToResult(result);
            }

            var builder = new StringBuilder();
            foreach (var line in result.Value)
            {
                builder.AppendLine(JsonSerializer.Serialize(line, ExportSerializerOptions));
            }

            return Results.Text(builder.ToString(), "application/x-ndjson", Encoding.UTF8);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, $"GET {RoutePrefix}/export", ex);
            return Results.Problem(
                detail: "The operator request failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetAsync(
        string id,
        [FromServices] CoachOpportunityOperatorService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.GetAsync(id, cancellationToken);
            return ToResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, $"GET {RoutePrefix}/{{id}}", ex);
            return Results.Problem(
                detail: "The operator request failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ReviewAsync(
        string id,
        CoachOpportunityReviewRequest? request,
        [FromServices] CoachOpportunityOperatorService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.ReviewAsync(id, request, cancellationToken);
            return ToResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, $"POST {RoutePrefix}/{{id}}/review", ex);
            return Results.Problem(
                detail: "The operator request failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> RevealEvidenceAsync(
        string id,
        CoachOpportunityEvidenceRequest? request,
        [FromServices] CoachOpportunityOperatorService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.RevealEvidenceAsync(id, request, cancellationToken);
            return ToResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, $"POST {RoutePrefix}/{{id}}/evidence", ex);
            return Results.Problem(
                detail: "The operator request failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Maps an operator status to a response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CoachOpportunityOperatorStatus.NotAvailable"/> is 404 for the flag being off,
    /// the caller being outside the cohort, and the row not existing alike — those must be
    /// indistinguishable, or the surface becomes an oracle for which opportunity identifiers
    /// exist.
    /// </para>
    /// <para>
    /// <b><see cref="CoachOpportunityOperatorStatus.CrossOwnerRefused"/> collapses into the same
    /// 404.</b> A 403 answered only for identifiers that name a real row owned by somebody else,
    /// so probing the identifier space would separate "does not exist" from "exists, not yours" —
    /// the precise existence oracle the rest of this surface is built to deny. The refusal is
    /// still logged distinctly server-side; only the wire representation is collapsed.
    /// </para>
    /// </remarks>
    private static IResult ToResult<T>(CoachOpportunityOperatorResult<T> result) => result.Status switch
    {
        CoachOpportunityOperatorStatus.Success => Results.Ok(result.Value),
        CoachOpportunityOperatorStatus.NotAvailable => Results.NotFound(),
        CoachOpportunityOperatorStatus.InvalidRequest => Results.BadRequest(),

        // Indistinguishable from "no such row", on purpose. See the remarks above.
        CoachOpportunityOperatorStatus.CrossOwnerRefused => Results.NotFound(),

        // A lifecycle rule, not an authorization one: the row exists and the caller may review
        // it, but the transition they asked for would delete a decision. 409 says "your view of
        // this row is stale, re-read it", which is what a reviewer needs to do.
        CoachOpportunityOperatorStatus.TransitionRefused => Results.Problem(
            title: "Review transition refused",
            detail: "An accepted opportunity cannot be returned to a status the retention sweep "
                    + "would age out. Re-read the row before deciding again.",
            statusCode: StatusCodes.Status409Conflict),

        CoachOpportunityOperatorStatus.EphemeralKeyRing => Results.Problem(
            title: "Evidence unavailable",
            detail: "This host's Data Protection key ring is ephemeral, so stored coach messages "
                    + "cannot be decrypted reliably. Configure a durable key ring first.",
            statusCode: StatusCodes.Status409Conflict),

        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private static bool TryParseEnum<TEnum>(string? value, out TEnum? parsed) where TEnum : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result) || !Enum.IsDefined(result))
        {
            return false;
        }

        parsed = result;
        return true;
    }
}
