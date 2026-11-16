namespace SentenceStudio.Api.Admin;

/// <summary>Codes for what the server keeps no record of in the AI usage and jobs pages.</summary>
public static class AdminUsageNotRecorded
{
    /// <summary>AI calls other than Sam's runs: nothing records them per learner.</summary>
    public const string OtherAiCalls = "other_ai_calls";

    /// <summary>The part-of-speech backfill's last run: its report goes to the Workers log only.</summary>
    public const string BackfillLastRun = "backfill_last_run";

    /// <summary>Whether the backfill is switched on: that setting belongs to the Workers host.</summary>
    public const string BackfillEnabled = "backfill_enabled";
}

/// <summary>One learner's Sam usage for one learner-local day (one <c>CoachUsage</c> row).</summary>
/// <param name="LocalDate">The learner-local date, <c>yyyy-MM-dd</c>.</param>
public sealed record AdminSamUsageDay(
    string ProfileId,
    string LocalDate,
    int Runs,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCostUsd);

/// <summary>One learner's Sam usage for one ISO week, summed from the daily rows read.</summary>
/// <param name="WeekKey">The ISO week, <c>yyyy-Www</c>.</param>
/// <param name="DaysWithRuns">Days in the week that have a usage row.</param>
public sealed record AdminSamUsageWeek(
    string ProfileId,
    string WeekKey,
    int Runs,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCostUsd,
    int DaysWithRuns);

/// <summary>Sam runs, tokens and estimated cost per learner per day and week.</summary>
/// <param name="FromLocalDate">The first day read: the Monday of the oldest week asked for.</param>
/// <param name="Learners">Everyone who appears in <paramref name="Days"/> or <paramref name="Weeks"/>.</param>
/// <param name="Days">Newest first, then by profile id.</param>
/// <param name="Weeks">Newest first, then by profile id.</param>
/// <param name="Truncated">More rows matched than were read; the weeks may be short.</param>
/// <param name="NotRecorded">Codes from <see cref="AdminUsageNotRecorded"/>.</param>
public sealed record AdminSamUsage(
    string FromLocalDate,
    int WeeksAsked,
    IReadOnlyList<AdminLearnerSummary> Learners,
    IReadOnlyList<AdminSamUsageDay> Days,
    IReadOnlyList<AdminSamUsageWeek> Weeks,
    bool Truncated,
    IReadOnlyList<string> NotRecorded);

/// <summary>Codes for <see cref="AdminVideoImportRow.Problem"/>.</summary>
public static class AdminVideoImportProblems
{
    public const string Failed = "failed";
    public const string Stuck = "stuck";
}

/// <summary>
/// Reason codes for a failed import. The stored error text can carry content (a language, a
/// transcript length, an exception's words), so only one of these fixed codes leaves the server.
/// </summary>
public static class AdminVideoImportReasons
{
    public const string NoTranscript = "no_transcript";
    public const string TranscriptTooShort = "transcript_too_short";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
    public const string UnexpectedError = "unexpected_error";

    /// <summary>Failed with a message this code list does not know.</summary>
    public const string Other = "other";

    /// <summary>Failed with no message stored.</summary>
    public const string NoneStored = "none_stored";
}

/// <summary>Wire codes for a video import's pipeline status.</summary>
public static class AdminVideoImportStatusCodes
{
    public const string Pending = "pending";
    public const string FetchingTranscript = "fetching_transcript";
    public const string CleaningTranscript = "cleaning_transcript";
    public const string GeneratingVocabulary = "generating_vocabulary";
    public const string SavingResource = "saving_resource";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

/// <summary>One failed or stuck import. No title, URL, video id or transcript.</summary>
/// <param name="Id">The import's id.</param>
/// <param name="ProfileId">The owning learner; null for an import recorded to no one.</param>
/// <param name="Status">A code from <see cref="AdminVideoImportStatusCodes"/>.</param>
/// <param name="Problem">A code from <see cref="AdminVideoImportProblems"/>.</param>
/// <param name="ReasonCode">A code from <see cref="AdminVideoImportReasons"/> for a failed import; null for a stuck one.</param>
public sealed record AdminVideoImportRow(
    string Id,
    string? ProfileId,
    string Status,
    string Problem,
    string? ReasonCode,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    bool HasResource);

/// <summary>How many failed imports carry one reason code.</summary>
public sealed record AdminVideoImportReasonCount(string ReasonCode, int Count);

/// <summary>YouTube imports that failed or are stuck.</summary>
/// <param name="StuckAfterMinutes">An import not finished this long after it was created counts as stuck.</param>
/// <param name="Truncated">The reasons were counted over the first rows only.</param>
public sealed record AdminVideoImports(
    IReadOnlyList<AdminVideoImportRow> Items,
    int FailedCount,
    int StuckCount,
    IReadOnlyList<AdminVideoImportReasonCount> Reasons,
    int StuckAfterMinutes,
    IReadOnlyList<AdminLearnerSummary> Learners,
    bool Truncated);

/// <summary>One learner's words and how many carry a part of speech.</summary>
public sealed record AdminPartOfSpeechLearner(
    AdminLearnerSummary Learner,
    long Words,
    long Classified,
    long Unclassified);

/// <summary>
/// Where the part-of-speech backfill stands, from the data: how many words carry a part of speech.
/// </summary>
/// <param name="UnownedWords">Words recorded to no learner. The backfill never touches them.</param>
/// <param name="Learners">Learners with at least one word, most unclassified first.</param>
/// <param name="Truncated">More learners matched than are listed.</param>
/// <param name="NotRecorded">Codes from <see cref="AdminUsageNotRecorded"/>.</param>
public sealed record AdminPartOfSpeech(
    long TotalWords,
    long Classified,
    long Unclassified,
    long UnownedWords,
    IReadOnlyList<AdminPartOfSpeechLearner> Learners,
    bool Truncated,
    IReadOnlyList<string> NotRecorded);
