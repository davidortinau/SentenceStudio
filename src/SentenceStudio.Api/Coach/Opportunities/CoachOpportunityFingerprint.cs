using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// The stable identity of a <em>problem</em>, computed from closed-vocabulary inputs only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not salted with the owner.</b> The fingerprint answers "which gap is this", so
/// <c>GROUP BY Fingerprint</c> gives the cross-learner rollup a reviewer needs. Per-learner
/// deduplication comes from the unique index on
/// <c>(UserProfileId, Fingerprint, DedupBucketDate)</c>, not from the digest.
/// </para>
/// <para>
/// Every input is a closed enum or a closed-vocabulary constant, so the digest is itself
/// content-free: it is safe to log, safe to paste into a markdown decision record, and cannot be
/// inverted into anything a learner typed because nothing a learner typed was ever an input.
/// </para>
/// <para>
/// <see cref="CoachOpportunityLimits.SchemaVersion"/> is the first field on purpose. A change to
/// the tuple's meaning must produce new fingerprints rather than quietly merging rows recorded
/// under different semantics.
/// </para>
/// </remarks>
public static class CoachOpportunityFingerprint
{
    /// <summary>The scheme prefix used when a fingerprint is rendered for a human.</summary>
    public const string DisplayScheme = "coach-opportunity://";

    /// <summary>Computes the fingerprint for one signal's content-free tuple.</summary>
    public static string Compute(
        CoachOpportunityKind kind,
        string capabilityCode,
        string? toolName,
        string? failureCode,
        CoachStopReason? stopReason,
        CoachOpportunityOfferLink offerLink)
    {
        // Ordinals and invariant formatting: a fingerprint must not change with the host's
        // locale, and the enum names must not become part of the digest, because renaming a
        // member for clarity would then orphan every row already written.
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{CoachOpportunityLimits.SchemaVersion}|{(int)kind}|{capabilityCode}|{toolName ?? string.Empty}|{failureCode ?? string.Empty}|{(stopReason.HasValue ? ((int)stopReason.Value).ToString(CultureInfo.InvariantCulture) : string.Empty)}|{(int)offerLink}");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Computes the fingerprint for a signal, after the recorder has normalized it.</summary>
    public static string Compute(in CoachOpportunitySignal signal) =>
        Compute(
            signal.Kind,
            signal.CapabilityCode,
            signal.ToolName,
            signal.FailureCode,
            signal.StopReason,
            signal.OfferLink);

    /// <summary>
    /// A shortened, scheme-prefixed rendering for a decision record.
    /// </summary>
    /// <remarks>
    /// Truncated for readability only. The full value stays in the column, and the operator
    /// surface always filters on the full value, so two problems that share a 12-character prefix
    /// can never be conflated by a query.
    /// </remarks>
    public static string Describe(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return DisplayScheme + "unknown";
        }

        return fingerprint.Length <= 12
            ? DisplayScheme + fingerprint
            : string.Concat(DisplayScheme, fingerprint.AsSpan(0, 12), "\u2026");
    }
}
