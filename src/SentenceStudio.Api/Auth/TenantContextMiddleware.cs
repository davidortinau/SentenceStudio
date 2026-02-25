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
            mutableContext.TenantId = context.User.FindFirstValue("tenant_id");
            mutableContext.UserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            mutableContext.DisplayName = context.User.FindFirstValue(ClaimTypes.Name);
            mutableContext.Email = context.User.FindFirstValue(ClaimTypes.Email);
        }

        await _next(context);
    }
}
