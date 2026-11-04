using System.Net.Http.Headers;

namespace SentenceStudio.Services;

/// <summary>The signed-in learner a sync request runs for, with the token that proves it.</summary>
public sealed record SyncOwnerCredentials(string Owner, string AccessToken);

/// <summary>
/// Supplies the verified owner for owner-checked sync on a client that keeps one database for
/// every account (the MAUI heads). Returns null when no learner is signed in, or when the
/// token's owner and the active profile disagree; sync then sends nothing (fail closed).
/// Native heads use their readiness lease instead and do not register this.
/// </summary>
public interface ISyncOwnerSource
{
    Task<SyncOwnerCredentials?> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Adds the bearer and <see cref="SharedSyncRegistration.NativeOwnerHeader"/> from the same
/// verified token to every sync request, so the server applies its owner checks
/// (docs/sync-all-tables-plan.md, "Owner-checked sync for every client"). Without a verified
/// owner the request is never sent: a headerless sync request is refused by the server and
/// would carry no owner checks.
/// </summary>
public sealed class SyncOwnerHeaderHandler(ISyncOwnerSource owners) : DelegatingHandler
{
    /// <summary>When set on a request, the owner the caller prepared it for.</summary>
    public static readonly HttpRequestOptionsKey<string> ExpectedOwner = new("SentenceStudio.SyncOwner");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = await owners.GetAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("Sync requires a signed-in learner.");
        if (request.Options.TryGetValue(ExpectedOwner, out var expected)
            && !string.Equals(expected, current.Owner, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The signed-in learner changed before sending.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        request.Headers.Remove(SharedSyncRegistration.NativeOwnerHeader);
        request.Headers.Add(SharedSyncRegistration.NativeOwnerHeader, current.Owner);
        return await base.SendAsync(request, cancellationToken);
    }
}
