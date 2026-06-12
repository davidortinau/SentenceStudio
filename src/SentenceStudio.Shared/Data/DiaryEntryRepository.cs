using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Repository for diary entries (one freeform entry per user per day per language).
/// Today's entry is editable; entries for prior days are read-only at the UI layer
/// but the repository itself does not enforce that — callers are responsible for
/// gating writes by date.
/// </summary>
public class DiaryEntryRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<DiaryEntryRepository> _logger;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences;

    public DiaryEntryRepository(
        IServiceProvider serviceProvider,
        ILogger<DiaryEntryRepository> logger,
        ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
        _preferences = serviceProvider.GetService<SentenceStudio.Abstractions.IPreferencesService>();
    }

    private string ActiveUserId => _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;

    /// <summary>
    /// Normalize an arbitrary DateTime to the UTC midnight that represents its calendar day.
    /// </summary>
    public static DateTime NormalizeEntryDate(DateTime date)
        => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

    /// <summary>
    /// All entries for the active user, newest first. Returns the full entity (Content can be
    /// large — callers needing list-only fields should project at the call site).
    /// </summary>
    public async Task<List<DiaryEntry>> ListAsync()
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("DiaryEntryRepository.ListAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<DiaryEntry>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DiaryEntries
            .Where(e => e.UserProfileId == userId)
            .OrderByDescending(e => e.EntryDate)
            .ThenByDescending(e => e.UpdatedAt)
            .ToListAsync();
    }

    public async Task<DiaryEntry?> GetByDateAsync(DateTime date, string language)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var normalized = NormalizeEntryDate(date);
        return await db.DiaryEntries.FirstOrDefaultAsync(e =>
            e.UserProfileId == userId &&
            e.EntryDate == normalized &&
            e.Language == language);
    }

    public async Task<DiaryEntry?> GetByIdAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("DiaryEntryRepository.GetByIdAsync called without an active user — returning null to prevent cross-tenant data leak.");
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DiaryEntries.FirstOrDefaultAsync(e =>
            e.Id == id && e.UserProfileId == userId);
    }

    /// <summary>
    /// Insert or update an entry. Enforces UserProfileId from the active profile, normalizes
    /// EntryDate, and recomputes WordCount from Content.
    /// </summary>
    public async Task<DiaryEntry> UpsertAsync(DiaryEntry entry)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            if (string.IsNullOrEmpty(entry.UserProfileId) && !string.IsNullOrEmpty(ActiveUserId))
                entry.UserProfileId = ActiveUserId;

            entry.EntryDate = NormalizeEntryDate(entry.EntryDate);
            entry.WordCount = CountWords(entry.Content, entry.Language);

            var now = DateTime.UtcNow;
            entry.UpdatedAt = now;

            var exists = !string.IsNullOrEmpty(entry.Id)
                && await db.DiaryEntries.AnyAsync(e => e.Id == entry.Id);

            if (exists)
            {
                db.DiaryEntries.Update(entry);
            }
            else
            {
                if (string.IsNullOrEmpty(entry.Id))
                    entry.Id = Guid.NewGuid().ToString();
                if (entry.CreatedAt == default)
                    entry.CreatedAt = now;
                db.DiaryEntries.Add(entry);
            }

            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving diary entry {Id}", entry.Id);
            throw;
        }
    }

    public async Task<DiaryEntry?> SaveFeedbackAsync(string id, string? recommended, string? notes, string? strengths)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await db.DiaryEntries.FirstOrDefaultAsync(e => e.Id == id);
        if (entry == null) return null;
        entry.FeedbackRecommended = recommended;
        entry.FeedbackNotes = notes;
        entry.FeedbackStrengths = strengths;
        entry.FeedbackAt = DateTime.UtcNow;
        entry.UpdatedAt = entry.FeedbackAt.Value;
        await db.SaveChangesAsync();
        _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// Word count that treats whitespace-separated tokens as words. Adequate for Korean
    /// (which uses whitespace between eojeol units), English, and most European languages.
    /// TODO: switch to grapheme-cluster counting for Japanese/Chinese (no whitespace).
    /// </summary>
    public static int CountWords(string? content, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        return content.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
