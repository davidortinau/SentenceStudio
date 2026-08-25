using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Opportunities;

namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// A capability code drawn from the server's closed set, or the unknown bucket.
/// </summary>
/// <remarks>
/// <para>
/// This type exists so the observation can carry the one model-influenced fact the opportunity
/// ledger genuinely needs — <em>which</em> preference a learner keeps asking for — without carrying
/// a string. The distinction is not cosmetic. A <c>string</c> member on the observation is an
/// unbounded channel that a later change can widen without anything failing; a value type whose
/// only constructor runs the closed-set gate is bounded by construction, and the cardinality of the
/// family is <c>CoachPreferenceChangeHandler.CandidateNames</c> plus one.
/// </para>
/// <para>
/// It is also what keeps the shape rule honest. The trace shape test forbids <c>string</c> and
/// <c>object</c> members; if the code rode along as a raw string this seam would either fail that
/// test or force an exception to it, and an exception to a no-leak rule is how the rule stops
/// being one.
/// </para>
/// <para>
/// <b>In-memory only.</b> Like <c>CoachResultScope</c> on the same record, this never crosses the
/// persistence boundary from here. W4b projects closed codes out of the observation; it does not
/// serialize the observation.
/// </para>
/// </remarks>
public readonly record struct CoachToolSubjectCode
{
    private readonly string? _candidate;

    private CoachToolSubjectCode(string? candidate) => _candidate = candidate;

    /// <summary>
    /// The matched server-owned candidate name, or null when the request named nothing the server
    /// owns.
    /// </summary>
    /// <remarks>
    /// A member of <c>CoachPreferenceChangeHandler.CandidateNames</c> — a compile-time constant array —
    /// or null. It is not the model's string: a name the model invented does not match a candidate
    /// and lands here as null.
    /// </remarks>
    public string? Value => _candidate;

    /// <summary>True when the request named a setting the server owns.</summary>
    public bool IsKnown => _candidate is not null;

    /// <summary>The opportunity-ledger capability code this collapses to.</summary>
    public string CapabilityCode =>
        CoachOpportunityCapabilityCodes.ForPreferenceSetting(_candidate);

    /// <summary>
    /// Collapses a model-supplied setting name to a member of the closed candidate set, or to the
    /// unknown bucket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model's string is read, matched, and discarded inside this call. What is stored is the
    /// <em>constant that matched</em>, never the input — so a caller holding the result cannot
    /// recover what was asked for beyond "one of the six settings the server owns", and an invented
    /// name is indistinguishable from an absent one.
    /// </para>
    /// <para>
    /// Matching mirrors <c>CoachOpportunityCapabilityCodes.ForPreferenceSetting</c> exactly — trim,
    /// lower-case, ordinal compare — so the code this collapses to is the same code the ledger
    /// recorded before the seam was generalized. Behaviour preserved, channel closed.
    /// </para>
    /// </remarks>
    public static CoachToolSubjectCode ForPreferenceSetting(string? settingName)
    {
        if (string.IsNullOrWhiteSpace(settingName))
        {
            return new CoachToolSubjectCode(null);
        }

        var normalized = settingName.Trim().ToLowerInvariant();

        foreach (var candidate in CoachPreferenceChangeHandler.CandidateNames)
        {
            if (string.Equals(candidate, normalized, StringComparison.Ordinal))
            {
                return new CoachToolSubjectCode(candidate);
            }
        }

        return new CoachToolSubjectCode(null);
    }

    /// <inheritdoc />
    public override string ToString() => CapabilityCode;
}
