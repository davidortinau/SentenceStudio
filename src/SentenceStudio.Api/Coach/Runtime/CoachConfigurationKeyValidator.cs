using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Validation.Claims;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Rejects deprecated flat spellings of the coach feature switches at startup.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CoachOptionsValidator"/> checks bound values. It cannot catch this class of
/// mistake, because a flat <c>Coach:DurableHistory=true</c> binds to nothing at all: the binder
/// sees a value node where it wants an object, finds no <c>Enabled</c> child, and leaves the
/// switch false. The operator's intent is lost without a single warning.
/// </para>
/// <para>
/// That silence is what makes the flat key dangerous rather than merely wrong. Durable history
/// once read the flat key while the Data Protection guard read the nested one, so the two
/// disagreed about whether the host was storing content that had to survive a restart — the
/// ledger wrote encrypted rows while the guard permitted a key ring that would not outlive the
/// process. Either spelling silently winning is a data-loss path, so neither is allowed to win
/// silently.
/// </para>
/// <para>
/// This validator therefore reads raw configuration rather than bound options, and fails the
/// host with the exact key to change. It refuses the flat key even when the canonical key is
/// also present: a manifest carrying both spellings is ambiguous about intent, and guessing is
/// how the original defect survived review.
/// </para>
/// </remarks>
public sealed class CoachConfigurationKeyValidator : IValidateOptions<CoachOptions>
{
    private readonly IConfiguration _configuration;

    /// <summary>Creates the validator over the host configuration.</summary>
    public CoachConfigurationKeyValidator(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// The switches that must be spelled as a nested <c>:Enabled</c> key, paired with the flat
    /// path that is no longer accepted.
    /// </summary>
    public static IReadOnlyList<(string FlatKey, string CanonicalKey)> RetiredFlatKeys { get; } =
    [
        ("Coach:DurableHistory", "Coach:DurableHistory:Enabled"),
        ("Coach:Memory",         "Coach:Memory:Enabled"),

        // The opportunity ledger's two switches. Added the day the feature shipped rather than
        // after somebody flipped the flat spelling in a deployment and wondered why nothing was
        // captured — which is exactly how the durable-history switch earned its entry here.
        ("Coach:Opportunities",  "Coach:Opportunities:Enabled"),
        ("Coach:Opportunities:OperatorSurface", "Coach:Opportunities:OperatorSurface:Enabled"),

        // The learner-report switch. Deliberately its own section rather than a child of
        // Opportunities: a learner pressing Report is not automatic capture, and the two must be
        // separately flippable or turning heuristics off would silently throw away the one signal
        // that arrived with a human's intent behind it.
        ("Coach:Reports",        "Coach:Reports:Enabled"),

        // W8. Off is a total bypass, so a flat spelling here is the most expensive kind of typo:
        // the deployment believes disputes are tracked, no dispute is ever opened, and the metric
        // that would have shown it reads zero because nothing is being counted.
        ("Coach:CorrectionState", "Coach:CorrectionState:Enabled")
    ];

    /// <summary>
    /// The grounding stage key. Its value must name a <see cref="CoachGroundingStage"/> member.
    /// </summary>
    public const string GroundingStageKey = "Coach:Grounding:Stage";

    /// <summary>The flat spelling that binds to nothing.</summary>
    public const string GroundingFlatKey = "Coach:Grounding";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CoachOptions options)
    {
        var failures = new List<string>();

        foreach (var (flatKey, canonicalKey) in RetiredFlatKeys)
        {
            // Non-null only when configuration holds a *value* at this path. A properly nested
            // switch is a section with children and no value of its own, so it never trips here.
            var flatValue = _configuration[flatKey];
            if (string.IsNullOrWhiteSpace(flatValue))
            {
                continue;
            }

            failures.Add(
                $"'{flatKey}' is not a supported configuration key. Use '{canonicalKey}' instead " +
                $"(environment variable '{canonicalKey.Replace(':', '_').Replace("_", "__")}'). " +
                "The flat spelling binds to nothing, so the feature would stay off while the " +
                "deployment believed it was on.");
        }

        failures.AddRange(ValidateGroundingStage());
        failures.AddRange(ValidateCorrectionStateSwitch());

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The correction-state switch key. Its value must be a boolean.</summary>
    public const string CorrectionStateEnabledKey = "Coach:CorrectionState:Enabled";

    /// <summary>
    /// Refuses a correction-state value the binder would silently discard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read raw, for the reason the bound value cannot express.
    /// <c>Coach:CorrectionState:Enabled=yes</c> fails to parse, the binder leaves the property
    /// false, and the bound options are indistinguishable from a deployment that never set the key.
    /// The operator gets a host that starts and a feature that is off while every dashboard reads
    /// as though it is on and finding nothing.
    /// </para>
    /// <para>
    /// <c>1</c> and <c>0</c> are refused alongside <c>yes</c> and <c>on</c>. They read as true to a
    /// human and bind to false, which is the worst combination available.
    /// </para>
    /// </remarks>
    private IEnumerable<string> ValidateCorrectionStateSwitch()
    {
        var raw = _configuration[CorrectionStateEnabledKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Absent is legal and means off, which is the fail-safe value.
            yield break;
        }

        if (!bool.TryParse(raw.Trim(), out _))
        {
            yield return
                $"'{CorrectionStateEnabledKey}' must be 'true' or 'false'; '{raw.Trim()}' binds to "
                + "false. A deployment that believes correction state is on while it is off gets a "
                + "dispute metric of zero that reads as 'no learner ever corrected the coach'.";
        }
    }

    /// <summary>
    /// Refuses a grounding stage the binder would silently discard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read raw, for a reason the bound value cannot express. <c>Coach:Grounding:Stage=Repare</c>
    /// fails to parse, the binder leaves the property at its default, and the bound options are
    /// indistinguishable from a deployment that never set the key. The operator gets a host that
    /// starts, a stage of <see cref="CoachGroundingStage.Off"/>, and a dashboard of zeros that
    /// means "nothing was measured" while reading as "nothing was wrong".
    /// </para>
    /// <para>
    /// Numeric spellings are refused for the same reason a wire enum pins its ordinals: <c>2</c>
    /// binds today and means something else the moment a rung is inserted. The stage is named in a
    /// deployment manifest by a human, so it is spelled the way a human reads it.
    /// </para>
    /// </remarks>
    private IEnumerable<string> ValidateGroundingStage()
    {
        var flat = _configuration[GroundingFlatKey];
        if (!string.IsNullOrWhiteSpace(flat))
        {
            yield return
                $"'{GroundingFlatKey}' is not a supported configuration key. Use "
                + $"'{GroundingStageKey}' instead (environment variable 'Coach__Grounding__Stage'). "
                + "The flat spelling binds to nothing, so the grounding ladder would stay Off while "
                + "the deployment believed it was on.";
        }

        var raw = _configuration[GroundingStageKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Absent is legal and means Off. An operator who has not thought about the ladder gets
            // the fail-safe rung, which is the one that changes nothing.
            yield break;
        }

        var trimmed = raw.Trim();

        if (trimmed.Contains(',', StringComparison.Ordinal))
        {
            // Refused ahead of the parse, because the parse succeeds. Enum.TryParse accepts a
            // comma-separated list on any enum, flags or not, and combines the members bitwise —
            // so 'Observe,Repair' parses to 1 | 2 = 3, which is Enforce. A deployment typo would
            // silently promote the ladder two rungs past what was asked for and land on the only
            // rung that refuses learner answers. The stage is one value; a list is never one.
            yield return
                $"'{GroundingStageKey}' is '{trimmed}'. The grounding stage is a single rung, not a "
                + $"combination. Use one of {DefinedStages()}. A comma-separated value parses to "
                + "the bitwise union of its members, so it would silently select a rung nobody "
                + "named — including Enforce.";
            yield break;
        }

        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            yield return
                $"'{GroundingStageKey}' is '{trimmed}'. Name the stage — {DefinedStages()} — rather "
                + "than its number. An ordinal in a deployment manifest keeps binding after a rung "
                + "is inserted, and then it means a different rung than the one that was reviewed.";
            yield break;
        }

        if (!Enum.TryParse<CoachGroundingStage>(trimmed, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            yield return
                $"'{GroundingStageKey}' is '{trimmed}', which is not a grounding stage. Use one of "
                + $"{DefinedStages()}. An unrecognised value binds to Off, so the host would start "
                + "with the honesty layer disabled and no indication that it had been asked for.";
        }
    }

    private static string DefinedStages() =>
        string.Join(", ", Enum.GetNames<CoachGroundingStage>());
}
