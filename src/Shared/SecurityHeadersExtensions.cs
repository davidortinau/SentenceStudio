using Microsoft.AspNetCore.Builder;

namespace SentenceStudio.Infrastructure;

/// <summary>
/// Adds standard security response headers to every HTTP response.
/// Shared across all web services via linked source file.
/// </summary>
public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next();
        });
    }
}
