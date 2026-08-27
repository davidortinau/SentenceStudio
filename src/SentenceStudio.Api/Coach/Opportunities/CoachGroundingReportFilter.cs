using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// The four grounding filters the operator surface accepts, already validated.
/// </summary>
/// <remarks>
/// <para>
/// <b>A parsed value or nothing.</b> The type cannot hold a string a caller typed: every member is
/// an ordinal drawn from a closed enum, or the exact name of a declared rule. Parsing happens once,
/// at <see cref="TryParse"/>, and a value that does not parse never becomes a filter — it fails the
/// request instead, exactly as an unknown capability code already does.
/// </para>
/// <para>
/// <b>Development only, and that is enforced upstream rather than here.</b> The operator surface's
/// startup gate already refuses to register outside Development; duplicating the environment check
/// in the filter would create a second place that has to agree with the first.
/// </para>
/// </remarks>
public readonly record struct CoachGroundingReportFilter(
    int? Stage,
    bool? Refused,
    string? RuleCode,
    int? LimitationCode)
{
    /// <summary>True when the value is an integer literal rather than a member name.</summary>
    private static bool IsNumeric(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && int.TryParse(
            value.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);

    /// <summary>True when no filter was requested, so the query is left alone.</summary>
    public bool IsEmpty =>
        Stage is null && Refused is null && RuleCode is null && LimitationCode is null;

    /// <summary>
    /// Parses the four raw query values, or reports which one was not acceptable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Names in, ordinals out.</b> An operator types <c>Repair</c>, not <c>2</c>, for the same
    /// reason the stored rule codes are names: an ordinal in a URL keeps meaning something after a
    /// member is inserted, and it means the wrong thing.
    /// </para>
    /// <para>
    /// <b>Unparseable is a refusal, not an ignored filter.</b> Silently dropping a filter nobody
    /// could parse would answer a broader question than the operator asked and let them read the
    /// result as the narrower one.
    /// </para>
    /// </remarks>
    public static bool TryParse(
        string? stage,
        bool? refused,
        string? ruleCode,
        string? limitationCode,
        out CoachGroundingReportFilter filter)
    {
        filter = default;

        // An ordinal in a URL is refused before it is parsed. Enum.TryParse accepts "2" and
        // Enum.IsDefined agrees it is a member — but the number keeps binding after a member is
        // inserted, and then the saved query means a different rung than the one it was written
        // for. Operators filter by name, which is what the stored columns hold anyway.
        if (IsNumeric(stage) || IsNumeric(ruleCode) || IsNumeric(limitationCode))
        {
            return false;
        }

        int? stageOrdinal = null;
        if (!string.IsNullOrWhiteSpace(stage))
        {
            if (!Enum.TryParse<CoachGroundingStage>(stage.Trim(), ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed)
                || stage.Contains(',', StringComparison.Ordinal))
            {
                // The comma check is not paranoia: Enum.TryParse accepts a comma-separated list on
                // any enum and combines the members bitwise, so "Observe,Repair" would parse to
                // Enforce and filter on a rung nobody named.
                return false;
            }

            stageOrdinal = (int)parsed;
        }

        string? ruleName = null;
        if (!string.IsNullOrWhiteSpace(ruleCode))
        {
            if (!Enum.TryParse<CoachClaimRuleCode>(ruleCode.Trim(), ignoreCase: true, out var rule)
                || !Enum.IsDefined(rule)
                || rule == CoachClaimRuleCode.Unknown
                || ruleCode.Contains(',', StringComparison.Ordinal))
            {
                return false;
            }

            // The declared spelling, not the caller's casing. The stored column holds member names
            // exactly as the enum spells them, so a case-insensitive parse has to be normalised
            // before it can be compared against one.
            ruleName = rule.ToString();
        }

        int? limitationOrdinal = null;
        if (!string.IsNullOrWhiteSpace(limitationCode))
        {
            if (!Enum.TryParse<CoachLimitationCode>(limitationCode.Trim(), ignoreCase: true, out var limitation)
                || !Enum.IsDefined(limitation)
                || limitationCode.Contains(',', StringComparison.Ordinal))
            {
                return false;
            }

            limitationOrdinal = (int)limitation;
        }

        filter = new CoachGroundingReportFilter(stageOrdinal, refused, ruleName, limitationOrdinal);
        return true;
    }
}
