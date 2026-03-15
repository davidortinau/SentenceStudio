using System.Security.Claims;
using SentenceStudio.Domain.Abstractions;

namespace SentenceStudio.Api.Auth;

public sealed class TenantContextMiddleware
{
    private readonly RequestDelegate _next;

    public TenantContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (tenantContext is TenantContext mutableContext && context.User.Identity?.IsAuthenticated == true)
        {
            // Entra ID uses "tid" for tenant, "oid" for object/user ID,
            // and "preferred_username" or "name" for display info.
            // Identity JWTs use standard ClaimTypes: NameIdentifier, Email, Name, plus "tenant_id".
            // DevAuthHandler uses "tenant_id", NameIdentifier, Name, Email.
            // The fallback chain below covers all three auth paths.
            mutableContext.TenantId =
                context.User.FindFirstValue("tid")
                ?? context.User.FindFirstValue("tenant_id");

            mutableContext.UserId =
                context.User.FindFirstValue("oid")
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);

            mutableContext.DisplayName =
                context.User.FindFirstValue("name")
                ?? context.User.FindFirstValue("preferred_username")
                ?? context.User.FindFirstValue(ClaimTypes.Name);

            mutableContext.Email =
                context.User.FindFirstValue(ClaimTypes.Email)
                ?? context.User.FindFirstValue("preferred_username");
        }

        await _next(context);
    }
}
