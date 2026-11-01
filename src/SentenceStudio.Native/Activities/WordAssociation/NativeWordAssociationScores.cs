using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Source WordAssociationService high scores: one local
/// <see cref="WordAssociationScore"/> row per finished round (the table is
/// device-local and not synchronized), an all-time high and a weekly high
/// (Monday-anchored, local calendar). Unlike the source, every read and write
/// requires an owner and never uses an empty profile id.
/// </summary>
public sealed class NativeWordAssociationScores(
    IServiceScopeFactory scopes, ILogger<NativeWordAssociationScores> logger)
{
    /// <summary>Source CheckHighScoreAsync, evaluated before the round is saved.</summary>
    public async Task<NativeWordAssociationHighScore> CheckHighScoreAsync(
        string owner, int score, DateTime localNow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return NativeWordAssociationHighScore.None;
        using var scope = scopes.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .WordAssociationScores.AsNoTracking().Where(row => row.UserProfileId == owner);
        var allTime = await rows.Select(row => (int?)row.RoundScore).MaxAsync(cancellationToken) ?? 0;
        if (score > allTime)
            return NativeWordAssociationHighScore.AllTime;
        var weekStart = WeekStartUtc(localNow);
        var weekly = await rows.Where(row => row.CreatedAt >= weekStart)
            .Select(row => (int?)row.RoundScore).MaxAsync(cancellationToken) ?? 0;
        return score > weekly ? NativeWordAssociationHighScore.Weekly : NativeWordAssociationHighScore.None;
    }

    /// <summary>Source SaveRoundScoreAsync. Returns false when the row could not be written.</summary>
    public async Task<bool> SaveAsync(string owner, int score, int totalClues,
        IReadOnlyList<string> wordIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return false;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;
            db.WordAssociationScores.Add(new WordAssociationScore
            {
                UserProfileId = owner,
                RoundScore = score,
                TotalClues = totalClues,
                WordCount = wordIds.Count,
                WordIds = string.Join(",", wordIds),
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Word Association could not save a round score.");
            return false;
        }
    }

    /// <summary>Monday 00:00 of the local week, as the UTC instant the table stores.</summary>
    internal static DateTime WeekStartUtc(DateTime localNow)
    {
        var today = localNow.Date;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var start = DateTime.SpecifyKind(today.AddDays(-daysSinceMonday), DateTimeKind.Local);
        return start.ToUniversalTime();
    }
}
