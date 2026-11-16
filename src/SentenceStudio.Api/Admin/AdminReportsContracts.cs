using System.Text;
using System.Text.Json;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Admin;

/// <summary>
/// The feedback inbox's wire codes. Fixed lowercase strings, so renaming a C# member cannot
/// silently change what the inbox page and the maintenance agent read.
/// </summary>
public static class AdminReportCodes
{
    public const string StatusNew = "new";
    public const string StatusReviewed = "reviewed";
    public const string StatusAccepted = "accepted";
    public const string StatusDeferred = "deferred";
    public const string StatusDismissed = "dismissed";

    public const string ReasonDidNotAnswer = "did_not_answer";
    public const string ReasonIncorrectOrMisleading = "incorrect_or_misleading";
    public const string ReasonExpectedAppAction = "expected_app_action";
    public const string ReasonConfusing = "confusing";
    public const string ReasonOther = "other";

    /// <summary>The question, the answer or both were read.</summary>
    public const string ExchangeAvailable = "available";

    /// <summary>The report points at messages that no longer resolve, usually because the learner deleted the conversation.</summary>
    public const string ExchangeUnavailable = "unavailable";

    /// <summary>The report points at no messages.</summary>
    public const string ExchangeNotApplicable = "not_applicable";

    /// <summary>The attempt was claimed and never settled: an issue may or may not exist.</summary>
    public const string GitHubClaimed = "claimed";

    public const string GitHubSubmitted = "submitted";

    /// <summary>The attempt closed and no issue was created.</summary>
    public const string GitHubFailed = "failed";

    /// <summary>An issue exists and its number was not recorded.</summary>
    public const string GitHubCommitted = "committed";

    /// <summary>A stored value this build has no code for.</summary>
    public const string Unknown = "unknown";

    public static string Of(CoachOpportunityStatus status) => status switch
    {
        CoachOpportunityStatus.New => StatusNew,
        CoachOpportunityStatus.Reviewed => StatusReviewed,
        CoachOpportunityStatus.Accepted => StatusAccepted,
        CoachOpportunityStatus.Deferred => StatusDeferred,
        CoachOpportunityStatus.Dismissed => StatusDismissed,
        _ => Unknown
    };

    public static bool TryParseStatus(string? code, out CoachOpportunityStatus status)
    {
        CoachOpportunityStatus? parsed = code switch
        {
            StatusNew => CoachOpportunityStatus.New,
            StatusReviewed => CoachOpportunityStatus.Reviewed,
            StatusAccepted => CoachOpportunityStatus.Accepted,
            StatusDeferred => CoachOpportunityStatus.Deferred,
            StatusDismissed => CoachOpportunityStatus.Dismissed,
            _ => null
        };

        status = parsed ?? default;
        return parsed is not null;
    }

    public static string Of(CoachResponseReportReason? reason) => reason switch
    {
        CoachResponseReportReason.DidNotAnswer => ReasonDidNotAnswer,
        CoachResponseReportReason.IncorrectOrMisleading => ReasonIncorrectOrMisleading,
        CoachResponseReportReason.ExpectedAppAction => ReasonExpectedAppAction,
        CoachResponseReportReason.Confusing => ReasonConfusing,
        CoachResponseReportReason.Other => ReasonOther,
        _ => Unknown
    };

    public static string Of(FeedbackSubmissionStatus status) => status switch
    {
        FeedbackSubmissionStatus.Claimed => GitHubClaimed,
        FeedbackSubmissionStatus.Submitted => GitHubSubmitted,
        FeedbackSubmissionStatus.Failed => GitHubFailed,
        FeedbackSubmissionStatus.Committed => GitHubCommitted,
        _ => Unknown
    };

    /// <summary>
    /// A descriptive stored value (response kind, stop reason, turn status, platform, screen) as
    /// a lowercase code: <c>PedagogicalAnswer</c> is <c>pedagogical_answer</c>. A value the enum
    /// does not declare is <see cref="Unknown"/>, never a bare number.
    /// </summary>
    /// <remarks>
    /// These describe; nothing branches on them. <c>AdminReportCodeTests</c> pins every code this
    /// produces, so renaming a member is a failing test rather than a silent wire change.
    /// </remarks>
    public static string Describe<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            return Unknown;
        }

        var name = value.ToString();
        var code = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                code.Append('_');
            }

            code.Append(char.ToLowerInvariant(name[i]));
        }

        return code.ToString();
    }

    /// <inheritdoc cref="Describe{TEnum}(TEnum)"/>
    public static string? Describe<TEnum>(TEnum? value) where TEnum : struct, Enum =>
        value is { } present ? Describe(present) : null;
}

/// <summary>
/// One flagged Sam answer as the inbox lists it: a learner said one of Sam's answers did not
/// serve them. Ids, codes, counts and times only.
/// </summary>
/// <remarks>
/// A row is one learner, one reason and one UTC day. A learner who flags three answers for the
/// same reason on the same day makes one row with <paramref name="FlagCount"/> 3; the detail
/// lists each flag.
/// </remarks>
/// <param name="Id">The report's id. Use it in the routes below.</param>
/// <param name="LearnerId">The learner's profile id.</param>
/// <param name="Status">A status code from <see cref="AdminReportCodes"/>.</param>
/// <param name="Reason">A reason code from <see cref="AdminReportCodes"/>: why the learner flagged it.</param>
/// <param name="FailureCode">The turn's or the write's own failure code, when it failed.</param>
/// <param name="StopReason">How the turn ended, for example <c>completed</c>.</param>
/// <param name="IssueNumber">The linked GitHub issue, when one is linked.</param>
/// <param name="NeedsAttention">
/// True while nobody has looked at the newest flag: the report is <c>new</c>, or the learner
/// flagged again after it was last triaged. A learner's later flag lands on the same report and
/// does not change its status, so this, not the status, says whether there is something unread.
/// </param>
/// <param name="HasExchange">Whether there is a question and answer to open.</param>
/// <param name="ExchangeOpenedCount">How many times anyone has opened it.</param>
public sealed record AdminSamReportRow(
    string Id,
    string LearnerId,
    string Status,
    bool NeedsAttention,
    string Reason,
    int FlagCount,
    DateTime FirstFlaggedAtUtc,
    DateTime LastFlaggedAtUtc,
    string? FailureCode,
    string? StopReason,
    int? IssueNumber,
    string? IssueUrl,
    bool HasExchange,
    int ExchangeOpenedCount,
    DateTime? ExchangeLastOpenedAtUtc,
    DateTime? ReviewedAtUtc);

/// <summary>What <c>GET /api/admin/reports/sam</c> returns, newest first.</summary>
/// <param name="Learners">Names for the learners in <paramref name="Items"/>. Empty for an agent token, which gets ids only.</param>
/// <param name="AttentionCount">How many reports need attention, whatever the filter.</param>
/// <param name="UnlistedFlagCount">
/// Flags that were recorded without an inbox row, so they are in no list. Normally zero.
/// </param>
/// <param name="Truncated">True when more rows matched than were returned.</param>
public sealed record AdminSamReportList(
    IReadOnlyList<AdminSamReportRow> Items,
    IReadOnlyList<AdminLearnerSummary> Learners,
    int AttentionCount,
    int UnlistedFlagCount,
    bool Truncated);

/// <summary>One flag on a report: one answer the learner flagged, in codes.</summary>
/// <param name="ResponseKind">How the flagged answer was shown, for example <c>pedagogical_answer</c>.</param>
/// <param name="ToolsInvoked">The registered tools the turn ran, comma-separated.</param>
public sealed record AdminSamReportFlag(
    DateTime FlaggedAtUtc,
    string Reason,
    string ResponseKind,
    string? TurnStatus,
    int? TurnAttempts,
    string? TurnErrorCode,
    string? ToolsInvoked,
    string? WriteStatus,
    string? WriteFailureCode,
    string? GroundingStage,
    bool? GroundingRefused,
    bool? GroundingAltered,
    bool? GroundingRepairSuppressed,
    int? GroundingFindingCount,
    string? GroundingRuleCodes,
    string? GroundingLimitation);

/// <summary>What <c>GET /api/admin/reports/sam/{reportId}</c> returns.</summary>
/// <param name="Learner">The learner's name and email. Null for an agent token.</param>
/// <param name="Flags">Each flag on the report, newest first.</param>
public sealed record AdminSamReportDetail(
    AdminSamReportRow Report,
    AdminLearnerSummary? Learner,
    IReadOnlyList<AdminSamReportFlag> Flags,
    bool FlagsTruncated);

/// <summary>
/// The body of <c>POST /api/admin/reports/sam/{reportId}/triage</c>. What is left out is left as
/// it is, so a caller working from an older copy of the report cannot undo what it never saw.
/// At least one of the three must be given.
/// </summary>
/// <param name="Status">A status code from <see cref="AdminReportCodes"/>, or left out to keep the status.</param>
/// <param name="IssueNumber">The GitHub issue to link, or left out to keep the link as it is.</param>
/// <param name="UnlinkIssue">True to remove the linked issue. Not together with <paramref name="IssueNumber"/>.</param>
public sealed record AdminSamTriageRequest(string? Status, int? IssueNumber = null, bool? UnlinkIssue = null);

/// <summary>One flagged question and the answer the learner flagged. Learner content.</summary>
public sealed record AdminSamExchange(DateTime? FlaggedAtUtc, string? Question, string? Answer);

/// <summary>What <c>POST /api/admin/reports/sam/{reportId}/exchange</c> returns.</summary>
/// <param name="State">An exchange code from <see cref="AdminReportCodes"/>.</param>
/// <param name="OpenedCount">How many times this report's exchange has been opened, this time included.</param>
public sealed record AdminSamExchangeResponse(
    string ReportId,
    string State,
    IReadOnlyList<AdminSamExchange> Exchanges,
    bool Truncated,
    int OpenedCount);

/// <summary>One report a learner filed to GitHub through the app. Public issue facts and codes only.</summary>
/// <param name="Status">A GitHub code from <see cref="AdminReportCodes"/>.</param>
/// <param name="ScreenStateReference">The saved screen state's reference, when one was saved.</param>
public sealed record AdminGitHubReportRow(
    string Status,
    int? IssueNumber,
    string? IssueUrl,
    string? IssueTitle,
    string? FailureCode,
    string LearnerId,
    string Platform,
    string AppVersion,
    string Screen,
    DateTime CreatedAtUtc,
    string? ScreenStateReference);

/// <summary>What <c>GET /api/admin/reports/github</c> returns, newest first.</summary>
public sealed record AdminGitHubReportList(
    IReadOnlyList<AdminGitHubReportRow> Items,
    IReadOnlyList<AdminLearnerSummary> Learners,
    bool Truncated);

/// <summary>The private screen state saved with one GitHub report. Learner content.</summary>
public sealed record AdminScreenStateResponse(
    string Reference,
    string LearnerId,
    DateTime CreatedAtUtc,
    JsonElement Context);

/// <summary>What <c>GET /api/admin/reports/attention-count</c> returns: flagged Sam answers nobody has looked at yet.</summary>
public sealed record AdminReportAttentionCount(int Sam);
