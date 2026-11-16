using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace SentenceStudio.Api.Admin;

/// <summary>A password-again proof and when it stops working.</summary>
public sealed record AdminStepUpProof(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Issues and checks the proof that an admin typed their password again a moment ago.
/// </summary>
/// <remarks>
/// <para>
/// Dangerous admin routes (delete, switch off, reset link, reading a learner's content) refuse
/// without one. It lasts <see cref="Lifetime"/>, names one account, and is bound to that
/// account's security stamp, so changing or resetting the password ends it early.
/// </para>
/// <para>
/// The proof is sealed with the API's Data Protection key ring, not the JWT signing key: the
/// web app holds the signing key too, and whoever holds it can sign in as anyone. A proof can
/// only come from <c>POST /api/admin/step-up</c> with the account's real password.
/// </para>
/// <para>
/// Only an admin account can hold one. The agent token has no password, so it passes a route
/// that asks for this only where the route also names a token permission and the token holds it
/// (reading a flagged exchange, with <see cref="AdminTokenPermissions.LearnerContent"/>). Every
/// other such route refuses a token outright.
/// </para>
/// </remarks>
public sealed class AdminStepUpService
{
    /// <summary>The request header a proof travels in.</summary>
    public const string HeaderName = "X-Admin-Proof";

    /// <summary>How long a proof lasts (the Captain's default, 2026-11-15).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string Purpose = "SentenceStudio.Api.Admin.StepUp.v1";
    private const int MaxProofLength = 2048;
    private const char Separator = '\n';

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public AdminStepUpService(IDataProtectionProvider dataProtection, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        _protector = dataProtection.CreateProtector(Purpose);
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Issues a proof for <paramref name="actor"/>, whose password the caller has just checked.
    /// Returns null when the actor cannot hold one.
    /// </summary>
    public AdminStepUpProof? Issue(AdminActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!CanHoldProof(actor, out var stampDigest))
        {
            return null;
        }

        // Whole seconds, so the expiry handed back here is the one Validate reads out again.
        var expiresUnix = _time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var payload = string.Join(
            Separator,
            actor.Id,
            stampDigest,
            expiresUnix.ToString(CultureInfo.InvariantCulture));

        return new AdminStepUpProof(_protector.Protect(payload), DateTimeOffset.FromUnixTimeSeconds(expiresUnix));
    }

    /// <summary>
    /// When <paramref name="proof"/> is a live proof for <paramref name="actor"/>, the moment it
    /// expires; otherwise null. Never throws for a bad proof.
    /// </summary>
    public DateTimeOffset? Validate(AdminActor actor, string? proof)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrEmpty(proof) || proof.Length > MaxProofLength
            || !CanHoldProof(actor, out var stampDigest))
        {
            return null;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(proof);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }

        var parts = payload.Split(Separator);
        if (parts.Length != 3
            || !string.Equals(parts[0], actor.Id, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(parts[1]), Encoding.ASCII.GetBytes(stampDigest))
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
        return expiresAt > _time.GetUtcNow() ? expiresAt : null;
    }

    /// <summary>The live proof on this request for its admitted caller, if any.</summary>
    public DateTimeOffset? Validate(HttpContext http) =>
        http.FindAdminActor() is { } actor
            ? Validate(actor, http.Request.Headers[HeaderName].ToString())
            : null;

    /// <summary>
    /// What a proof is bound to in place of the security stamp itself, or null for an account
    /// that has none (which can therefore hold no proof).
    /// </summary>
    public static string? DigestOf(string? securityStamp) =>
        string.IsNullOrEmpty(securityStamp)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp)));

    private static bool CanHoldProof(AdminActor actor, out string stampDigest)
    {
        stampDigest = actor.StampDigest ?? string.Empty;
        return actor.Kind == AdminActorKind.Admin
            && !string.IsNullOrEmpty(actor.Id)
            && !actor.Id.Contains(Separator)
            && stampDigest.Length > 0;
    }
}

/// <summary>Marks a route that refuses without a live password-again proof.</summary>
public sealed class AdminStepUpMetadata
{
    public static readonly AdminStepUpMetadata Instance = new();
}

/// <summary>Refuses the request unless it carries a live proof for its admitted caller.</summary>
internal sealed class AdminStepUpFilter(AdminStepUpService stepUp) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // A token has no password. Where a route that asks for the password again also admits a
        // token, the permission the route names stands in for the proof: the audit middleware has
        // already refused a token without it, and this checks again rather than trust the order.
        if (http.FindAdminActor() is { Kind: AdminActorKind.AgentToken } token
            && AdminTokenAccess.IsAllowed(http.GetEndpoint(), token))
        {
            return await next(context);
        }

        if (stepUp.Validate(http) is null)
        {
            return AdminRefusal.Create(
                http,
                StatusCodes.Status403Forbidden,
                AdminFailureCodes.StepUpRequired,
                "Enter your password again to continue.");
        }

        return await next(context);
    }
}

public static class AdminStepUpRouteExtensions
{
    /// <summary>The route refuses unless the caller typed their password in the last ten minutes.</summary>
    public static RouteHandlerBuilder RequireAdminStepUp(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<AdminStepUpFilter>().WithMetadata(AdminStepUpMetadata.Instance);
}
