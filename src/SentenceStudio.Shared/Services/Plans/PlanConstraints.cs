namespace SentenceStudio.Services.Plans;

/// <summary>
/// Skill the learner wants weighted more heavily for a single session.
/// Emphasis is a weighting hint only — it can never remove due vocabulary
/// review from a plan, and it can never authorize an activity the learner's
/// modality constraints have excluded.
/// </summary>
public enum PlanSkillEmphasis
{
    Listening,
    Speaking,
    Reading,
    Writing,
    Vocabulary
}

/// <summary>
/// Learner-reported energy for a single session. <see cref="Low"/> may shorten
/// the session or change modality; it must never lower the deterministic
/// difficulty floor (no substituting an easier activity for a harder one, and
/// no dropping the production/output block while budget remains).
/// </summary>
public enum PlanEnergyLevel
{
    Normal,
    Low
}

/// <summary>
/// Typed, validated per-session constraints for deterministic plan generation.
/// </summary>
/// <remarks>
/// <para>
/// This is the complete constraint surface. Callers that map free-form learner
/// text onto constraints (the Learning Coach) may only populate these fields;
/// they cannot introduce new constraint dimensions, and they can never supply a
/// user identity — user scope stays with the trusted caller
/// (<c>IUserScopeProvider</c> / an explicit <c>userProfileId</c>).
/// </para>
/// <para>
/// A <c>null</c> <see cref="PlanConstraints"/> means "no constraints" and must
/// produce byte-identical planner output to the pre-constraint planner.
/// </para>
/// </remarks>
public sealed record PlanConstraints
{
    public const int MinAvailableMinutes = 3;
    public const int MaxAvailableMinutes = 90;
    public const int MinGoalHorizonDays = 1;
    public const int MaxGoalHorizonDays = 180;

    /// <summary>
    /// Session budget in minutes. When supplied it is authoritative and
    /// replaces <c>UserProfile.PreferredSessionMinutes</c> as the planner
    /// budget. <c>null</c> keeps the profile preference.
    /// </summary>
    public int? AvailableMinutes { get; init; }

    /// <summary>Learner can play audio. When false, audio-required activities are excluded.</summary>
    public bool AudioAllowed { get; init; } = true;

    /// <summary>Learner can speak aloud. When false, speech-required activities are excluded.</summary>
    public bool SpeechAllowed { get; init; } = true;

    /// <summary>Learner can type. When false, typing-required activities are excluded.</summary>
    public bool TypingAllowed { get; init; } = true;

    /// <summary>Optional weighting hint. Never removes due review.</summary>
    public PlanSkillEmphasis? SkillEmphasis { get; init; }

    /// <summary>
    /// Server-owned goal tag (or "other"). Metadata / eligibility hint only —
    /// it never selects plan items in this lane.
    /// </summary>
    public string? GoalTag { get; init; }

    /// <summary>
    /// Goal horizon in days. Metadata / eligibility hint only — it never
    /// selects plan items in this lane.
    /// </summary>
    public int? GoalHorizonDays { get; init; }

    /// <summary>Learner-reported energy. Low may shorten, never soften.</summary>
    public PlanEnergyLevel EnergyLevel { get; init; } = PlanEnergyLevel.Normal;

    /// <summary>
    /// Validates every supplied field against its documented bounds. Returns
    /// false with a stable, non-empty error list when any field is out of
    /// range. Fields left <c>null</c> are "unconstrained" and always valid.
    /// </summary>
    public bool TryValidate(out IReadOnlyList<string> errors)
    {
        var found = new List<string>();

        if (AvailableMinutes is { } minutes &&
            (minutes < MinAvailableMinutes || minutes > MaxAvailableMinutes))
        {
            found.Add(
                $"{nameof(AvailableMinutes)} must be between {MinAvailableMinutes} and {MaxAvailableMinutes}; got {minutes}.");
        }

        if (GoalHorizonDays is { } horizon &&
            (horizon < MinGoalHorizonDays || horizon > MaxGoalHorizonDays))
        {
            found.Add(
                $"{nameof(GoalHorizonDays)} must be between {MinGoalHorizonDays} and {MaxGoalHorizonDays}; got {horizon}.");
        }

        if (SkillEmphasis is { } emphasis && !Enum.IsDefined(emphasis))
        {
            found.Add($"{nameof(SkillEmphasis)} '{(int)emphasis}' is not a defined {nameof(PlanSkillEmphasis)}.");
        }

        if (!Enum.IsDefined(EnergyLevel))
        {
            found.Add($"{nameof(EnergyLevel)} '{(int)EnergyLevel}' is not a defined {nameof(PlanEnergyLevel)}.");
        }

        if (GoalTag is not null && string.IsNullOrWhiteSpace(GoalTag))
        {
            found.Add($"{nameof(GoalTag)} must be null or a non-blank tag.");
        }

        errors = found;
        return found.Count == 0;
    }

    /// <summary>
    /// True when at least one response modality remains available. All three
    /// disallowed still leaves recognition-only activities (vocabulary review,
    /// reading, matching), so this is informational rather than fatal.
    /// </summary>
    public bool HasAnyProductionModality => SpeechAllowed || TypingAllowed;
}
