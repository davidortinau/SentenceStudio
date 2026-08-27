using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Tests.Infrastructure;

/// <summary>
/// Mints a one-time AutoSignIn token against a RUNNING WebApp's user store and key ring.
/// </summary>
/// <remarks>
/// <para>
/// Live verification only. The reproduced defect is about what a browser holds after signing in,
/// and the only way to check that against the running stack without driving a browser is to
/// complete the same <c>/account-action/AutoSignIn</c> hand-off the login page performs. The token
/// is produced by Identity's data-protection token provider, so this has to share two things with
/// the running app: its user store, and its Data Protection key ring.
/// </para>
/// <para>
/// The key ring is matched by content root — ASP.NET Core derives the default application
/// discriminator from <c>IHostEnvironment.ContentRootPath</c> — so this points at the real
/// project directory rather than the test output directory.
/// </para>
/// <para>
/// Opt-in and skipped by default: it needs a live database and a live host, neither of which CI
/// has. Set <c>WEBAPP_LIVE_CONNECTION</c> and <c>WEBAPP_LIVE_CONTENT_ROOT</c> to enable it.
/// </para>
/// </remarks>
public static class LiveWebAppSession
{
    public const string ConnectionVariable = "WEBAPP_LIVE_CONNECTION";
    public const string ContentRootVariable = "WEBAPP_LIVE_CONTENT_ROOT";

    /// <summary>Why this is unavailable, or null when both variables are set.</summary>
    public static string? SkipReason =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable))
        || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ContentRootVariable))
            ? $"Set {ConnectionVariable} and {ContentRootVariable} to verify against a running host."
            : null;

    /// <summary>Returns <c>userId|token</c> for <paramref name="email"/> on the live host.</summary>
    public static async Task<string> MintAutoSignInPairAsync(string email)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable)!;
        var contentRoot = Environment.GetEnvironmentVariable(ContentRootVariable)!;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot,
            EnvironmentName = Environments.Development
        });

        builder.Services.AddDataProtection();
        builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"No live account for {email}.");

        var token = await users.GenerateUserTokenAsync(
            user, TokenOptions.DefaultProvider, "AutoSignIn");

        return $"{user.Id}|{token}";
    }
}
/// <summary>
/// A fact that skips itself unless a running WebApp has been pointed at.
/// </summary>
/// <remarks>
/// The live variables are deliberately required rather than defaulted: a live verification that
/// quietly passed without a host would be worse than no verification at all.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveWebAppFactAttribute : FactAttribute
{
    public LiveWebAppFactAttribute()
    {
        var reason = LiveWebAppSession.SkipReason
            ?? (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBAPP_LIVE_BASE_HTTPS"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBAPP_LIVE_BASE_HTTP"))
                    ? "Set WEBAPP_LIVE_BASE_HTTPS and WEBAPP_LIVE_BASE_HTTP to verify a running host."
                    : null);

        if (reason is not null)
        {
            Skip = reason;
        }
    }
}
