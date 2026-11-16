using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Admin;

/// <summary>
/// AI usage and jobs, read-only: Sam's runs, tokens and estimated cost; YouTube imports that
/// failed or are stuck; and where the part-of-speech backfill stands.
/// </summary>
/// <remarks>
/// <para>
/// Counts, timestamps, ids and fixed codes only. No video title, URL or transcript, no Sam
/// conversation, no word. These routes do not ask for the password again, for that reason.
/// </para>
/// <para>
/// Other AI calls are not recorded per learner, and the backfill's own report goes to the
/// Workers log; the responses say so in <c>notRecorded</c>.
/// </para>
/// </remarks>
internal static class AdminUsageEndpoints
{
    private const string Prefix = "/usage";

    private const int DefaultWeeks = 4;
    private const int MaxWeeks = 13;
    private const int MaxUsageRows = 5000;
    private const int MaxDayRowsReturned = 2000;

    private const int DefaultImportPage = 50;
    private const int MaxImportPage = 200;
    private const int MaxReasonRows = 5000;
    internal const int StuckAfterMinutes = 10;

    private const int MaxLearnerRows = 100;

    public static RouteGroupBuilder MapAdminUsageEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet($"{Prefix}/sam", GetSamUsageAsync)
            .WithName("GetAdminSamUsage")
            .WithAdminAction(AdminActions.UsageSamRead);

        group.MapGet($"{Prefix}/video-imports", GetVideoImportsAsync)
            .WithName("GetAdminVideoImports")
            .WithAdminAction(AdminActions.UsageVideoImportsRead);

        group.MapGet($"{Prefix}/part-of-speech", GetPartOfSpeechAsync)
            .WithName("GetAdminPartOfSpeech")
            .WithAdminAction(AdminActions.UsagePartOfSpeechRead);

        return group;
    }

    // ------------------------------------------------------------------ Sam

    private static async Task<IResult> GetSamUsageAsync(
        HttpContext http,
        [FromServices] CoachDbContext coachDb,
        [FromServices] ApplicationDbContext db,
        [FromServices] TimeProvider time,
        CancellationToken cancellationToken,
        [FromQuery] string? weeks = null)
    {
        // Bound as text and parsed here, so a malformed value is this route's own 400 in every
        // environment rather than the framework's binding failure.
        var weekCount = DefaultWeeks;
        if (weeks is not null && (!int.TryParse(weeks, NumberStyles.None, CultureInfo.InvariantCulture, out weekCount)
                                  || weekCount < 1 || weekCount > MaxWeeks))
        {
            return AdminRefusal.Create(http, StatusCodes.Status400BadRequest,
                AdminFailureCodes.InvalidRequest, $"weeks must be 1 to {MaxWeeks}.");
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var from = thisMonday.AddDays(-7 * (weekCount - 1));

        // One more than the cap, to know whether anything was left out.
        var rows = await coachDb.CoachUsages.AsNoTracking()
            .Where(u => u.LocalDate >= from)
            .OrderByDescending(u => u.LocalDate)
            .ThenBy(u => u.UserProfileId)
            .Take(MaxUsageRows + 1)
            .Select(u => new UsageRow(
                u.UserProfileId, u.LocalDate, u.WeekKey, u.RunCount, u.InputTokens, u.OutputTokens, u.EstimatedCostUsd))
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > MaxUsageRows;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var dayRows = rows
            .Take(MaxDayRowsReturned)
            .Select(r => new AdminSamUsageDay(
                r.ProfileId, r.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Runs, r.InputTokens, r.OutputTokens, r.EstimatedCostUsd))
            .ToList();
        truncated |= rows.Count > MaxDayRowsReturned;

        var weekRows = rows
            .GroupBy(r => (r.ProfileId, r.WeekKey))
            .Select(g => new AdminSamUsageWeek(
                g.Key.ProfileId, g.Key.WeekKey,
                g.Sum(r => r.Runs), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens),
                g.Sum(r => r.EstimatedCostUsd), g.Count()))
            .OrderByDescending(w => w.WeekKey, StringComparer.Ordinal)
            .ThenBy(w => w.ProfileId, StringComparer.Ordinal)
            .ToList();

        var learners = await NamesAsync(db, rows.Select(r => r.ProfileId).Distinct().ToList(), cancellationToken);

        return Results.Ok(new AdminSamUsage(
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            weekCount,
            learners,
            dayRows,
            weekRows,
            truncated,
            [AdminUsageNotRecorded.OtherAiCalls]));
    }

    private sealed record UsageRow(
        string ProfileId, DateOnly LocalDate, string WeekKey, int Runs, long InputTokens, long OutputTokens,
        decimal EstimatedCostUsd);

    // ------------------------------------------------------------------ video imports

    internal static async Task<IResult> GetVideoImportsAsync(
        HttpContext http,
        [FromServices] ApplicationDbContext db,
        [FromServices] TimeProvider time,
        CancellationToken cancellationToken,
        [FromQuery] string? take = null)
    {
        var pageSize = DefaultImportPage;
        if (take is not null && (!int.TryParse(take, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                                 || pageSize < 1 || pageSize > MaxImportPage))
        {
            return AdminRefusal.Create(http, StatusCodes.Status400BadRequest,
                AdminFailureCodes.InvalidRequest, $"take must be 1 to {MaxImportPage}.");
        }

        var cutoff = time.GetUtcNow().UtcDateTime.AddMinutes(-StuckAfterMinutes);
        var failed = db.VideoImports.AsNoTracking().Where(v => v.Status == VideoImportStatus.Failed);
        var stuck = db.VideoImports.AsNoTracking().Where(v =>
            v.Status != VideoImportStatus.Failed && v.Status != VideoImportStatus.Completed && v.CreatedAt < cutoff);

        var failedCount = await failed.CountAsync(cancellationToken);
        var stuckCount = await stuck.CountAsync(cancellationToken);

        // The stored text is read here only to be sorted into a code; it is never returned.
        var messages = await failed
            .Select(v => v.ErrorMessage)
            .Take(MaxReasonRows + 1)
            .ToListAsync(cancellationToken);
        var truncated = messages.Count > MaxReasonRows;
        if (truncated)
        {
            messages.RemoveAt(messages.Count - 1);
        }

        var reasons = messages
            .GroupBy(ReasonCodeOf)
            .Select(g => new AdminVideoImportReasonCount(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.ReasonCode, StringComparer.Ordinal)
            .ToList();

        var page = await db.VideoImports.AsNoTracking()
            .Where(v => v.Status == VideoImportStatus.Failed
                        || (v.Status != VideoImportStatus.Completed && v.CreatedAt < cutoff))
            .OrderByDescending(v => v.CreatedAt)
            .Take(pageSize)
            .Select(v => new ImportRow(
                v.Id, v.UserProfileId, v.Status, v.ErrorMessage, v.CreatedAt, v.CompletedAt, v.LearningResourceId != null))
            .ToListAsync(cancellationToken);

        var items = page
            .Select(v => new AdminVideoImportRow(
                v.Id,
                v.ProfileId,
                StatusCode(v.Status),
                v.Status == VideoImportStatus.Failed ? AdminVideoImportProblems.Failed : AdminVideoImportProblems.Stuck,
                v.Status == VideoImportStatus.Failed ? ReasonCodeOf(v.ErrorMessage) : null,
                AdminSyncHealthReader.AsUtc(v.CreatedAt)!.Value,
                AdminSyncHealthReader.AsUtc(v.CompletedAt),
                v.HasResource))
            .ToList();

        var learners = await NamesAsync(
            db, items.Where(i => i.ProfileId is not null).Select(i => i.ProfileId!).Distinct().ToList(), cancellationToken);

        return Results.Ok(new AdminVideoImports(
            items, failedCount, stuckCount, reasons, StuckAfterMinutes, learners, truncated));
    }

    private sealed record ImportRow(
        string Id, string? ProfileId, VideoImportStatus Status, string? ErrorMessage,
        DateTime CreatedAt, DateTime? CompletedAt, bool HasResource);

    /// <summary>Sorts a stored failure text into one of the fixed codes. The text itself never leaves.</summary>
    internal static string ReasonCodeOf(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return AdminVideoImportReasons.NoneStored;
        }

        if (errorMessage.StartsWith("No ", StringComparison.Ordinal)
            && errorMessage.EndsWith(" transcript available for this video.", StringComparison.Ordinal))
        {
            return AdminVideoImportReasons.NoTranscript;
        }

        if (errorMessage.StartsWith("Transcript too short", StringComparison.Ordinal))
        {
            return AdminVideoImportReasons.TranscriptTooShort;
        }

        if (errorMessage.StartsWith("Import timed out", StringComparison.Ordinal))
        {
            return AdminVideoImportReasons.TimedOut;
        }

        return errorMessage switch
        {
            "Import was cancelled." => AdminVideoImportReasons.Cancelled,
            "An unexpected error occurred during import." => AdminVideoImportReasons.UnexpectedError,
            _ => AdminVideoImportReasons.Other
        };
    }

    internal static string StatusCode(VideoImportStatus status) => status switch
    {
        VideoImportStatus.Pending => AdminVideoImportStatusCodes.Pending,
        VideoImportStatus.FetchingTranscript => AdminVideoImportStatusCodes.FetchingTranscript,
        VideoImportStatus.CleaningTranscript => AdminVideoImportStatusCodes.CleaningTranscript,
        VideoImportStatus.GeneratingVocabulary => AdminVideoImportStatusCodes.GeneratingVocabulary,
        VideoImportStatus.SavingResource => AdminVideoImportStatusCodes.SavingResource,
        VideoImportStatus.Completed => AdminVideoImportStatusCodes.Completed,
        VideoImportStatus.Failed => AdminVideoImportStatusCodes.Failed,
        _ => AdminVideoImportStatusCodes.Unknown
    };

    // ------------------------------------------------------------------ part of speech

    internal static async Task<IResult> GetPartOfSpeechAsync(
        [FromServices] ApplicationDbContext db, CancellationToken cancellationToken)
    {
        // Counts only: grouped in the database, no word is read.
        var groups = await db.VocabularyWords.AsNoTracking()
            .GroupBy(w => w.UserProfileId)
            .Select(g => new WordGroup(g.Key, g.Count(), g.Count(w => w.PartOfSpeech != null)))
            .ToListAsync(cancellationToken);

        var owned = groups.Where(g => g.ProfileId != null).ToList();
        var unowned = groups.Where(g => g.ProfileId == null).Sum(g => g.Words);
        var listed = owned
            .OrderByDescending(g => g.Words - g.Classified)
            .ThenBy(g => g.ProfileId, StringComparer.Ordinal)
            .Take(MaxLearnerRows)
            .ToList();

        var names = await NamesAsync(db, listed.Select(g => g.ProfileId!).ToList(), cancellationToken);
        var learners = listed
            .Select(g => new AdminPartOfSpeechLearner(
                names.Single(n => n.ProfileId == g.ProfileId), g.Words, g.Classified, g.Words - g.Classified))
            .ToList();

        var total = groups.Sum(g => g.Words);
        var classified = groups.Sum(g => g.Classified);
        return Results.Ok(new AdminPartOfSpeech(
            total, classified, total - classified, unowned, learners, owned.Count > MaxLearnerRows,
            [AdminUsageNotRecorded.BackfillLastRun, AdminUsageNotRecorded.BackfillEnabled]));
    }

    private sealed record WordGroup(string? ProfileId, long Words, long Classified);

    // ------------------------------------------------------------------ learners

    /// <summary>
    /// Profile id, display name and email for each id, in the order given. A learner with no
    /// account or profile row still appears, with no name or email.
    /// </summary>
    private static async Task<IReadOnlyList<AdminLearnerSummary>> NamesAsync(
        ApplicationDbContext db, IReadOnlyList<string> profileIds, CancellationToken cancellationToken)
    {
        if (profileIds.Count == 0)
        {
            return [];
        }

        var profiles = await db.UserProfiles.AsNoTracking()
            .Where(p => profileIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Email })
            .ToListAsync(cancellationToken);
        var accounts = await db.Users.AsNoTracking()
            .Where(u => u.UserProfileId != null && profileIds.Contains(u.UserProfileId))
            .Select(u => new { u.UserProfileId, u.Email, u.DisplayName })
            .ToListAsync(cancellationToken);

        return profileIds
            .Select(id =>
            {
                var profile = profiles.FirstOrDefault(p => p.Id == id);
                var account = accounts.FirstOrDefault(a => a.UserProfileId == id);
                return new AdminLearnerSummary(id, account?.DisplayName ?? profile?.Name, account?.Email ?? profile?.Email);
            })
            .ToList();
    }
}
