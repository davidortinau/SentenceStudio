using Microsoft.AspNetCore.Builder;

namespace SentenceStudio.Infrastructure;

/// <summary>
/// Adds standard security response headers to every HTTP response.
/// Shared across all web services via linked source file.
/// </summary>
public static class SecurityHeadersExtensions
{
    /// <summary>Denies camera, microphone, and geolocation to every origin.</summary>
    public const string DefaultPermissionsPolicy = "camera=(), microphone=(), geolocation=()";

    /// <param name="app">The application builder.</param>
    /// <param name="permissionsPolicy">
    /// The Permissions-Policy value. Hosts that need a browser capability for a feature (for example
    /// the web app's Conversation voice mode needs the microphone) widen only that one, to <c>self</c>.
    /// </param>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, string permissionsPolicy = DefaultPermissionsPolicy)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = permissionsPolicy;
            await next();
        });
    }
}
