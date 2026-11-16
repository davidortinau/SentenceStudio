using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Admin;

/// <summary>Codes for <see cref="AdminTokenRow.State"/>.</summary>
public static class AdminTokenStates
{
    public const string Live = "live";
    public const string Expired = "expired";
    public const string Revoked = "revoked";

    /// <summary>
    /// Neither revoked nor expired, and refused all the same while the admin who created it is
    /// not an admin. It is accepted again if they become one again.
    /// </summary>
    public const string CreatorNotAdmin = "creator_not_admin";

    /// <summary>
    /// Neither revoked nor expired, and refused for good: the admin who created it has changed
    /// their password since.
    /// </summary>
    public const string CreatorPasswordChanged = "creator_password_changed";
}

/// <summary>One agent token as the Tokens page lists it. Never the token or its digest.</summary>
/// <param name="Permissions">Codes from <see cref="AdminTokenPermissionWire"/>.</param>
/// <param name="State">A code from <see cref="AdminTokenStates"/>.</param>
/// <param name="CreatedByEmail">Looked up for display when that account still exists.</param>
public sealed record AdminTokenRow(
    string Id,
    string Name,
    IReadOnlyList<string> Permissions,
    string State,
    DateTime CreatedAtUtc,
    string CreatedByUserId,
    string? CreatedByEmail,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    DateTime? LastUsedAtUtc);

/// <summary>What <c>GET /api/admin/tokens</c> returns, newest first.</summary>
public sealed record AdminTokenList(IReadOnlyList<AdminTokenRow> Items, int LifetimeDays, int MaxLive);

/// <summary>The body of <c>POST /api/admin/tokens</c>.</summary>
/// <param name="Name">What to call the token, for example <c>weekday check</c>.</param>
/// <param name="Permissions">One or more codes from <see cref="AdminTokenPermissionWire"/>.</param>
public sealed record AdminTokenCreateRequest(string? Name, IReadOnlyList<string?>? Permissions);

/// <summary>A new token. <paramref name="Token"/> is shown this once and cannot be read again.</summary>
public sealed record AdminTokenCreated(string Token, AdminTokenRow Row);

/// <summary>What a token learns about itself from <c>GET /api/admin/token</c>.</summary>
public sealed record AdminTokenSelf(
    string Id,
    string Name,
    IReadOnlyList<string> Permissions,
    DateTime ExpiresAtUtc,
    DateTime NowUtc);

/// <summary>
/// Agent tokens: an admin creates, lists and revokes them; a token can ask what it is.
/// </summary>
/// <remarks>
/// No token can create, list or revoke a token: those three routes carry no token permission, so
/// the audit middleware refuses every token on them. Creating one asks for the password again,
/// because the result is a credential.
/// </remarks>
internal static class AdminTokenEndpoints
{
    public static RouteGroupBuilder MapAdminTokenEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/tokens", ListAsync)
            .WithName("ListAdminTokens")
            .WithAdminAction(AdminActions.TokensList);

        group.MapPost("/tokens", CreateAsync)
            .WithName("CreateAdminToken")
            .WithAdminAction(AdminActions.TokensCreate)
            .RequireAdminStepUp();

        group.MapPost("/tokens/{tokenId}/revoke", RevokeAsync)
            .WithName("RevokeAdminToken")
            .WithAdminAction(AdminActions.TokensRevoke, AdminTargetKinds.Token, "tokenId");

        // The token's own check: is it live, what may it do, when does it run out. The weekday
        // task reads this first, and says so before the token expires.
        group.MapGet("/token", GetSelf)
            .WithName("GetAdminTokenSelf")
            .WithAdminAction(AdminActions.TokenSelfRead)
            .AllowAdminToken(AdminTokenPermissions.None);

        return group;
    }

    private static async Task<IResult> ListAsync(
        [FromServices] AdminAgentTokenService tokens,
        [FromServices] AdminAccessEvaluator evaluator,
        [FromServices] ApplicationDbContext db,
        [FromServices] TimeProvider time,
        CancellationToken cancellationToken)
    {
        var rows = await tokens.ListAsync(cancellationToken);
        var creators = rows.Select(r => r.CreatedByUserId).Distinct().ToList();
        var emails = creators.Count == 0
            ? new Dictionary<string, string?>()
            : await db.Users.AsNoTracking()
                .Where(u => creators.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        // A token that is live by its dates can still be one the API refuses, because of its
        // creator. The list says so, or it would show "live" beside a token that does not work.
        var now = time.GetUtcNow().UtcDateTime;
        var items = new List<AdminTokenRow>(rows.Count);
        foreach (var row in rows)
        {
            var denial = row.RevokedAtUtc is null && row.ExpiresAtUtc > now
                ? await evaluator.EvaluateTokenCreatorAsync(row, cancellationToken, forDisplay: true)
                : AdminDenial.None;
            items.Add(Project(row, now, emails.GetValueOrDefault(row.CreatedByUserId), denial));
        }

        return Results.Ok(new AdminTokenList(
            items, (int)AdminAgentTokenLimits.Lifetime.TotalDays, AdminAgentTokenLimits.MaxLiveTokens));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        [FromServices] AdminAgentTokenService tokens,
        [FromServices] TimeProvider time,
        CancellationToken cancellationToken)
    {
        var request = await AdminRequestBody.ReadAsync<AdminTokenCreateRequest>(http, cancellationToken);
        var name = request?.Name?.Trim();
        if (request is null
            || string.IsNullOrEmpty(name)
            || name.Length > AdminAgentTokenLimits.MaxNameLength
            || name.Any(char.IsControl)
            || !AdminTokenPermissionWire.TryParse(request.Permissions, out var permissions))
        {
            return AdminRefusal.Create(http, StatusCodes.Status400BadRequest, AdminFailureCodes.InvalidRequest,
                $"name is 1 to {AdminAgentTokenLimits.MaxNameLength} characters; permissions is one or more of "
                + $"{AdminTokenPermissionWire.Reports}, {AdminTokenPermissionWire.LearnerContent}, {AdminTokenPermissionWire.Triage}.");
        }

        if (await tokens.CountLiveAsync(cancellationToken) >= AdminAgentTokenLimits.MaxLiveTokens)
        {
            return AdminRefusal.Create(http, StatusCodes.Status409Conflict, AdminFailureCodes.TooManyTokens,
                $"There are already {AdminAgentTokenLimits.MaxLiveTokens} live tokens. Revoke one first.");
        }

        // The password-again filter has just accepted a proof bound to this digest, so there is one.
        var actor = http.GetAdminActor();
        if (string.IsNullOrEmpty(actor.StampDigest))
        {
            return AdminRefusal.Create(http, StatusCodes.Status409Conflict,
                AdminFailureCodes.StepUpUnavailable, "This account cannot create a token.");
        }

        var issued = await tokens.CreateAsync(actor.Id, actor.StampDigest, name, permissions, cancellationToken);
        return Results.Ok(new AdminTokenCreated(
            issued.Token, Project(issued.Row, time.GetUtcNow().UtcDateTime, actor.Email, AdminDenial.None)));
    }

    private static async Task<IResult> RevokeAsync(
        string tokenId,
        [FromServices] AdminAgentTokenService tokens,
        [FromServices] TimeProvider time,
        CancellationToken cancellationToken) =>
        await tokens.RevokeAsync(tokenId, cancellationToken) is { } row
            ? Results.Ok(Project(row, time.GetUtcNow().UtcDateTime, createdByEmail: null, AdminDenial.None))
            : Results.NotFound();

    private static IResult GetSelf(HttpContext http, [FromServices] TimeProvider time)
    {
        var actor = http.GetAdminActor();

        // An admin account is not a token; there is nothing here for it.
        return actor is { Kind: AdminActorKind.AgentToken, TokenExpiresAtUtc: { } expires }
            ? Results.Ok(new AdminTokenSelf(
                actor.Id, actor.DisplayName ?? string.Empty, AdminTokenPermissionWire.Of(actor.TokenPermissions),
                expires, time.GetUtcNow().UtcDateTime))
            : Results.NotFound();
    }

    private static AdminTokenRow Project(AdminAgentToken row, DateTime now, string? createdByEmail, AdminDenial creator) =>
        new(row.Id,
            row.Name,
            AdminTokenPermissionWire.Of(row.Permissions),
            row.RevokedAtUtc is not null ? AdminTokenStates.Revoked
                : row.ExpiresAtUtc <= now ? AdminTokenStates.Expired
                : creator == AdminDenial.TokenCreatorNotAdmin ? AdminTokenStates.CreatorNotAdmin
                : creator == AdminDenial.TokenCreatorPasswordChanged ? AdminTokenStates.CreatorPasswordChanged
                : AdminTokenStates.Live,
            row.CreatedAtUtc,
            row.CreatedByUserId,
            createdByEmail,
            row.ExpiresAtUtc,
            row.RevokedAtUtc,
            row.LastUsedAtUtc);
}
