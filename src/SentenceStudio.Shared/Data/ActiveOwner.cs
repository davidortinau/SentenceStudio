using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Data;

/// <summary>
/// The learner a shared repository acts for, from the trusted scope, for tables that gained an
/// owner in phase 3 of docs/sync-all-tables-plan.md (scene images, custom scenarios, example
/// sentences). Same rules as <see cref="StreamHistoryRepository"/>: API requests use the
/// claim-backed scope, WebApp circuits the authenticated <c>active_profile_id</c> preference,
/// native and MAUI devices the login-selected <c>active_profile_id</c>. A missing or conflicting
/// identity resolves to nobody, so callers return no data and write nothing.
/// </summary>
public static class ActiveOwner
{
    public const string PreferenceKey = "active_profile_id";

    public static bool TryResolve(
        IServiceProvider services, ILogger logger, string operation, out string userId)
    {
        var scopedOwner = services.GetService<IUserScopeProvider>();
        var scopedId = scopedOwner is not null
            && scopedOwner.TryGetUserProfileId(out var resolvedId)
                ? resolvedId
                : string.Empty;
        var preferenceId = services.GetService<IPreferencesService>()
            ?.Get(PreferenceKey, string.Empty) ?? string.Empty;

        userId = scopedOwner switch
        {
            DeviceUserScopeProvider => preferenceId,
            null => string.Empty,
            _ => scopedId
        };
        if (string.IsNullOrWhiteSpace(userId)
            || (!string.IsNullOrWhiteSpace(scopedId)
                && !string.IsNullOrWhiteSpace(preferenceId)
                && !string.Equals(scopedId, preferenceId, StringComparison.Ordinal)))
        {
            logger.LogWarning(
                "{Operation} called without an active user or with conflicting identities; refusing access.",
                operation);
            userId = string.Empty;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Rows saved before their table had an owner (a null owner) stay visible only on the client
    /// that holds them (D3, Captain 2026-11-02): a device's own SQLite database shows them to its
    /// signed-in learner, the multi-tenant server's PostgreSQL never shows them to anyone. They
    /// never sync.
    /// </summary>
    public static bool ShowsUnownedRows(ApplicationDbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true;
}
