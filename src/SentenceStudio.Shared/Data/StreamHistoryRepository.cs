using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Audio history is local to the active learner. An absent or unknown owner
/// never turns a query into an unfiltered read or a write into an orphan row.
/// Legacy rows without per-row ownership evidence remain inaccessible.
/// </summary>
public class StreamHistoryRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<StreamHistoryRepository> _logger;
    private int _reportedUnownedLegacy;

    public StreamHistoryRepository(
        IServiceProvider serviceProvider,
        ILogger<StreamHistoryRepository> logger,
        ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
    }

    private bool TryResolveOwner(IServiceProvider services, string operation, out string userId)
    {
        var scopedOwner = services.GetService<IUserScopeProvider>();
        var scopedId = scopedOwner is not null
            && scopedOwner.TryGetUserProfileId(out var resolvedId)
                ? resolvedId
                : string.Empty;
        var preferenceId = services.GetService<IPreferencesService>()
            ?.Get("active_profile_id", string.Empty) ?? string.Empty;

        // The native head's active_profile_id is set by the authenticated login
        // flow. WebApp registers the same device provider but leaves it unset;
        // its preference implementation instead derives this key from the
        // authenticated request/circuit. API scopes use claim-backed providers.
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
            _logger.LogWarning(
                "StreamHistoryRepository.{Operation} called without an active user or with conflicting identities — refusing access.",
                operation);
            userId = string.Empty;
            return false;
        }

        return true;
    }

    private async Task<TResult> WithOwnerAsync<TResult>(
        string operation,
        TResult denied,
        Func<ApplicationDbContext, string, Task<TResult>> action,
        bool reportUnownedLegacy = false)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!TryResolveOwner(scope.ServiceProvider, operation, out var userId))
            return denied;

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await db.UserProfiles.AsNoTracking().AnyAsync(p => p.Id == userId))
        {
            _logger.LogWarning(
                "StreamHistoryRepository.{Operation} refused: active user has no local profile.",
                operation);
            return denied;
        }

        if (reportUnownedLegacy && Volatile.Read(ref _reportedUnownedLegacy) == 0
            && await db.StreamHistories.AsNoTracking().AnyAsync(h => h.UserProfileId == null)
            && Interlocked.Exchange(ref _reportedUnownedLegacy, 1) == 0)
        {
            _logger.LogWarning(
                "StreamHistory legacy rows remain unowned: stored metadata has no per-row owner identity or verifiable profile link; manual verified recovery is required.");
        }
        return await action(db, userId);
    }

    public Task<List<StreamHistory>> GetAllStreamHistoryAsync() =>
        WithOwnerAsync(
            nameof(GetAllStreamHistoryAsync),
            new List<StreamHistory>(),
            (db, userId) => db.StreamHistories.AsNoTracking()
                .Where(h => h.UserProfileId == userId)
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync(),
            reportUnownedLegacy: true);

    public Task<StreamHistory?> GetStreamHistoryAsync(int id) =>
        WithOwnerAsync<StreamHistory?>(
            nameof(GetStreamHistoryAsync),
            null,
            (db, userId) => db.StreamHistories.AsNoTracking()
                .FirstOrDefaultAsync(h => h.Id == id && h.UserProfileId == userId),
            reportUnownedLegacy: true);

    public Task<int> SaveStreamHistoryAsync(StreamHistory streamHistory)
    {
        ArgumentNullException.ThrowIfNull(streamHistory);
        return WithOwnerAsync(nameof(SaveStreamHistoryAsync), 0, async (db, userId) =>
        {
            if (!string.IsNullOrWhiteSpace(streamHistory.UserProfileId)
                && !string.Equals(streamHistory.UserProfileId, userId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "SaveStreamHistoryAsync refused: the supplied owner differs from the active user.");
                return 0;
            }

            try
            {
                if (streamHistory.Id == 0)
                {
                    streamHistory.UserProfileId = userId;
                    if (streamHistory.CreatedAt == default)
                        streamHistory.CreatedAt = DateTime.UtcNow;
                    streamHistory.UpdatedAt = DateTime.UtcNow;
                    db.StreamHistories.Add(streamHistory);
                }
                else
                {
                    var existing = await db.StreamHistories
                        .FirstOrDefaultAsync(h => h.Id == streamHistory.Id
                            && h.UserProfileId == userId);
                    if (existing is null)
                    {
                        _logger.LogWarning(
                            "SaveStreamHistoryAsync refused: history entry is absent or owned by another user.");
                        return 0;
                    }

                    existing.Phrase = streamHistory.Phrase;
                    existing.Duration = streamHistory.Duration;
                    existing.VoiceId = streamHistory.VoiceId;
                    existing.AudioFilePath = streamHistory.AudioFilePath;
                    existing.FileName = streamHistory.FileName;
                    existing.Title = streamHistory.Title;
                    existing.Source = streamHistory.Source;
                    existing.SourceUrl = streamHistory.SourceUrl;
                    existing.UpdatedAt = DateTime.UtcNow;
                    streamHistory.UserProfileId = userId;
                }

                var result = await db.SaveChangesAsync();
                if (result > 0)
                    _syncService?.TriggerSyncAsync().ConfigureAwait(false);
                return result;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error occurred in SaveStreamHistoryAsync");
                return -1;
            }
            catch (DbException ex)
            {
                _logger.LogError(ex, "Error occurred in SaveStreamHistoryAsync");
                return -1;
            }
        });
    }

    public Task<int> DeleteStreamHistoryAsync(StreamHistory streamHistory)
    {
        ArgumentNullException.ThrowIfNull(streamHistory);
        return WithOwnerAsync(nameof(DeleteStreamHistoryAsync), 0, async (db, userId) =>
        {
            if (!string.IsNullOrWhiteSpace(streamHistory.UserProfileId)
                && !string.Equals(streamHistory.UserProfileId, userId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "DeleteStreamHistoryAsync refused: the supplied owner differs from the active user.");
                return 0;
            }

            try
            {
                var existing = await db.StreamHistories
                    .FirstOrDefaultAsync(h => h.Id == streamHistory.Id && h.UserProfileId == userId);
                if (existing is null)
                {
                    _logger.LogWarning(
                        "DeleteStreamHistoryAsync refused: history entry is absent or owned by another user.");
                    return 0;
                }

                db.StreamHistories.Remove(existing);
                var result = await db.SaveChangesAsync();
                if (result > 0)
                    _syncService?.TriggerSyncAsync().ConfigureAwait(false);
                return result;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error occurred in DeleteStreamHistoryAsync");
                return -1;
            }
            catch (DbException ex)
            {
                _logger.LogError(ex, "Error occurred in DeleteStreamHistoryAsync");
                return -1;
            }
        });
    }

    public Task<List<StreamHistory>> SearchStreamHistoryAsync(string query) =>
        WithOwnerAsync(
            nameof(SearchStreamHistoryAsync),
            new List<StreamHistory>(),
            (db, userId) => db.StreamHistories.AsNoTracking()
                .Where(h => h.UserProfileId == userId && h.Phrase != null
                    && h.Phrase.Contains(query))
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync(),
            reportUnownedLegacy: true);

    public Task<List<StreamHistory>> GetStreamHistoryByVoiceAsync(string voiceId) =>
        WithOwnerAsync(
            nameof(GetStreamHistoryByVoiceAsync),
            new List<StreamHistory>(),
            (db, userId) => db.StreamHistories.AsNoTracking()
                .Where(h => h.UserProfileId == userId && h.VoiceId == voiceId)
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync(),
            reportUnownedLegacy: true);

    public Task<StreamHistory?> GetStreamHistoryByPhraseAndVoiceAsync(
        string phrase, string voiceId) =>
        WithOwnerAsync<StreamHistory?>(
            nameof(GetStreamHistoryByPhraseAndVoiceAsync),
            null,
            (db, userId) => db.StreamHistories.AsNoTracking()
                .Where(h => h.UserProfileId == userId
                    && h.Phrase == phrase && h.VoiceId == voiceId)
                .OrderByDescending(h => h.CreatedAt)
                .FirstOrDefaultAsync(),
            reportUnownedLegacy: true);
}
