using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

/// <summary>What the learner chose for a word (docs/known-words-plan.md).</summary>
public enum VocabularyLearnerAction
{
    /// <summary>Mark Known (Learner-led only).</summary>
    MarkKnown,
    /// <summary>Practice again: Learning at typing level, due now.</summary>
    PracticeAgain,
    /// <summary>Start over: from a score of zero.</summary>
    StartOver,
}

/// <summary>How many of the learner's words an action changed, or why none did.</summary>
/// <param name="RefusalKey">The resource key of the reason nothing changed, or null.</param>
public sealed record VocabularyMarkResult(int Changed, string? RefusalKey = null)
{
    public static VocabularyMarkResult Refused(string key) => new(0, key);
}

/// <summary>
/// The learner's marks (docs/known-words-plan.md): Mark Known, Practice again and Start over, for
/// one word or many in one save. Only words recorded to the learner are touched (the one ownership
/// rule); an absent learner writes nothing.
/// </summary>
public partial class VocabularyProgressRepository
{
    /// <summary>The learner's mode, from their own profile.</summary>
    public async Task<LearningMode> GetLearningModeAsync(string userId = "", CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GetLearningModeAsync(string.IsNullOrEmpty(userId) ? ActiveUserId : userId, ct);
    }

    /// <summary>
    /// Applies <paramref name="action"/> to each of <paramref name="wordIds"/> that is the
    /// learner's own word, creating its progress row when it has none. Mark Known is refused in
    /// Practice-led, where marks are not read. Words that are not the learner's are left out.
    /// </summary>
    public async Task<VocabularyMarkResult> ApplyLearnerActionAsync(
        IReadOnlyCollection<string> wordIds, VocabularyLearnerAction action, string userId = "",
        CancellationToken ct = default)
    {
        var owner = string.IsNullOrEmpty(userId) ? ActiveUserId : userId;
        var ids = wordIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrEmpty(owner) || ids.Length == 0)
        {
            _logger.LogWarning("ApplyLearnerActionAsync called without a learner or a word; nothing written.");
            return VocabularyMarkResult.Refused("Vocabulary_MarkRefusedNotYours");
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (action == VocabularyLearnerAction.MarkKnown
            && await db.GetLearningModeAsync(owner, ct) != LearningMode.LearnerLed)
        {
            _logger.LogWarning("Mark Known refused: the learner is in Practice-led, where marks are not read.");
            return VocabularyMarkResult.Refused("Vocabulary_MarkKnownPracticeLed");
        }

        var owned = await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
            .Where(word => ids.Contains(word.Id))
            .Select(word => word.Id)
            .ToListAsync(ct);
        if (owned.Count == 0)
        {
            _logger.LogWarning("ApplyLearnerActionAsync: none of {Count} words is the learner's own; nothing written.", ids.Length);
            return VocabularyMarkResult.Refused("Vocabulary_MarkRefusedNotYours");
        }

        // A word can hold more than one of the learner's progress rows until sync reconciles
        // them; each gets the same mark, so every reader agrees.
        var rows = (await db.VocabularyProgresses
                .Where(progress => progress.UserId == owner && owned.Contains(progress.VocabularyWordId))
                .ToListAsync(ct))
            .ToLookup(progress => progress.VocabularyWordId, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        foreach (var wordId in owned)
        {
            var existing = rows[wordId].ToList();
            if (existing.Count == 0)
            {
                var created = new VocabularyProgress
                {
                    VocabularyWordId = wordId, UserId = owner, FirstSeenAt = now, LastPracticedAt = now,
                    CreatedAt = now, UpdatedAt = now, ReviewInterval = 1, EaseFactor = 2.5f,
                };
                db.VocabularyProgresses.Add(created);
                existing.Add(created);
            }
            foreach (var progress in existing)
            {
                switch (action)
                {
                    case VocabularyLearnerAction.MarkKnown:
                        VocabularyKnownMarks.MarkKnown(progress, now);
                        break;
                    case VocabularyLearnerAction.PracticeAgain:
                        VocabularyKnownMarks.PracticeAgain(progress, now);
                        break;
                    default:
                        VocabularyKnownMarks.StartOver(progress, now);
                        break;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        _logger.LogInformation("Learner action {Action} applied to {Count} of {Asked} words.",
            action, owned.Count, ids.Length);
        return new VocabularyMarkResult(owned.Count);
    }
}
