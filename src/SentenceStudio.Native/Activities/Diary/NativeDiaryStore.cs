using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Owner-bound DiaryEntry reads and writes. Same rules as the source
/// DiaryEntryRepository (UTC-midnight EntryDate, whitespace word count,
/// UpdatedAt/CreatedAt in UTC, a best-effort sync trigger after each write)
/// but every call takes the owner explicitly and refuses an empty owner or a
/// row owned by anyone else; the source update and SaveFeedbackAsync paths did
/// not check ownership. DiaryEntry is in the CoreSync set
/// (SharedSyncRegistration) and the server attributes it by UserProfileId.
/// </summary>
public sealed class NativeDiaryStore(IServiceScopeFactory scopes, NativePendingWrites pendingWrites)
{
    public async Task<List<DiaryEntry>> ListAsync(string owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return [];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DiaryEntries.AsNoTracking()
            .Where(entry => entry.UserProfileId == owner)
            .OrderByDescending(entry => entry.EntryDate)
            .ThenByDescending(entry => entry.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<DiaryEntry?> GetByDateAsync(string owner, DateTime date, string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return null;
        var normalized = DiaryEntryRepository.NormalizeEntryDate(date);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DiaryEntries.AsNoTracking().FirstOrDefaultAsync(entry =>
            entry.UserProfileId == owner && entry.EntryDate == normalized
            && entry.Language == language, cancellationToken);
    }

    public async Task<DiaryEntry?> GetByIdAsync(string owner, string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id))
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DiaryEntries.AsNoTracking().FirstOrDefaultAsync(entry =>
            entry.Id == id && entry.UserProfileId == owner, cancellationToken);
    }

    /// <summary>Source UpsertAsync for the owner. Returns a detached copy of the saved row.</summary>
    public async Task<DiaryEntry> UpsertAsync(string owner, DiaryEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrEmpty(entry.UserProfileId) && entry.UserProfileId != owner)
            throw new UnauthorizedAccessException("A diary entry cannot change owners.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var existing = string.IsNullOrEmpty(entry.Id) ? null
            : await db.DiaryEntries.FirstOrDefaultAsync(row => row.Id == entry.Id, cancellationToken);
        if (existing is not null && existing.UserProfileId != owner)
            throw new UnauthorizedAccessException("The diary entry belongs to another owner.");
        var row = existing ?? new DiaryEntry
        {
            Id = string.IsNullOrEmpty(entry.Id) ? Guid.NewGuid().ToString() : entry.Id,
            CreatedAt = entry.CreatedAt == default ? now : entry.CreatedAt,
        };
        row.UserProfileId = owner;
        row.EntryDate = DiaryEntryRepository.NormalizeEntryDate(entry.EntryDate);
        row.Language = entry.Language;
        row.PromptText = entry.PromptText;
        row.PromptHint = entry.PromptHint;
        row.Content = entry.Content ?? string.Empty;
        row.WordCount = DiaryEntryRepository.CountWords(row.Content, row.Language);
        row.WordGoal = entry.WordGoal;
        row.UpdatedAt = now;
        if (existing is null)
            db.DiaryEntries.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        AfterWrite(scope.ServiceProvider, owner);
        return Copy(row);
    }

    /// <summary>Source SaveFeedbackAsync, restricted to the owner's row.</summary>
    public async Task<DiaryEntry?> SaveFeedbackAsync(string owner, string id, string? recommended,
        string? notes, string? strengths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id))
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.DiaryEntries.FirstOrDefaultAsync(
            entry => entry.Id == id && entry.UserProfileId == owner, cancellationToken);
        if (row is null)
            return null;
        row.FeedbackRecommended = recommended;
        row.FeedbackNotes = notes;
        row.FeedbackStrengths = strengths;
        row.FeedbackAt = DateTime.UtcNow;
        row.UpdatedAt = row.FeedbackAt.Value;
        await db.SaveChangesAsync(cancellationToken);
        AfterWrite(scope.ServiceProvider, owner);
        return Copy(row);
    }

    private void AfterWrite(IServiceProvider provider, string owner)
    {
        pendingWrites.MarkPending(owner);
        // Source parity: the repository fires a best-effort sync after each
        // save; the native trigger runs it under the verified owner's lease.
        _ = provider.GetService<ISyncService>()?.TriggerSyncAsync();
    }

    private static DiaryEntry Copy(DiaryEntry row) => new()
    {
        Id = row.Id,
        UserProfileId = row.UserProfileId,
        EntryDate = row.EntryDate,
        Language = row.Language,
        PromptText = row.PromptText,
        PromptHint = row.PromptHint,
        Content = row.Content,
        WordCount = row.WordCount,
        WordGoal = row.WordGoal,
        FeedbackRecommended = row.FeedbackRecommended,
        FeedbackNotes = row.FeedbackNotes,
        FeedbackStrengths = row.FeedbackStrengths,
        FeedbackAt = row.FeedbackAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}
