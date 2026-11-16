using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace SentenceStudio.Api.Admin;

/// <summary>The one policy on every admin route.</summary>
public static class AdminPolicy
{
    public const string Name = "Admin";

    /// <remarks>
    /// One requirement and no "signed in" rule beside it: an agent token arrives with no account
    /// sign-in at all. <see cref="AdminAccessEvaluator"/> refuses a caller that is neither a
    /// signed-in admin nor a live token.
    /// </remarks>
    internal static AuthorizationPolicy Build() =>
        new AuthorizationPolicyBuilder()
            .AddRequirements(new AdminRequirement())
            .Build();
}

internal sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Runs <see cref="AdminAccessEvaluator"/> once per request and leaves the admitted caller on the
/// request for the audit middleware and the handlers.
/// </summary>
/// <remarks>
/// Authorization builds every registered handler for every authorized request, admin route or
/// not. The evaluator is therefore taken from the request only when an admin route asks, so no
/// other request pays for it.
/// </remarks>
internal sealed class AdminAuthorizationHandler : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        // Endpoint authorization passes the HttpContext as the resource. Anything else is not a
        // request this policy knows how to admit.
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var evaluator = http.RequestServices.GetRequiredService<AdminAccessEvaluator>();

        // A request that carries the token header is judged as a token and nothing else. One that
        // repeats the header names no single token and is refused.
        var result = http.Request.Headers.TryGetValue(AdminAgentTokenService.HeaderName, out var presented)
            ? await evaluator.EvaluateTokenAsync(presented.Count == 1 ? presented[0] : null, http.RequestAborted)
            : await evaluator.EvaluateAsync(context.User, http.RequestAborted);
        if (result.Actor is null)
        {
            return;
        }

        http.Features.Set<IAdminActorFeature>(new AdminActorFeature(result.Actor));
        context.Succeed(requirement);
    }
}

/// <summary>
/// Answers every refused admin request with a bare 404, signed in or not.
/// </summary>
/// <remarks>
/// The default handler challenges (401) or forbids (403), and either one tells the caller the
/// route exists and is off-limits to them. A refusal here is the same response the API gives a
/// path nobody mapped. Every other policy keeps the default behaviour.
/// </remarks>
internal sealed class AdminNotFoundAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && policy.Requirements.OfType<AdminRequirement>().Any())
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
