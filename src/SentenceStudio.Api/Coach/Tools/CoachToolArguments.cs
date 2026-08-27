using System.ComponentModel;
using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// The windows the practice-balance tool supports. The set is closed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachPracticeWindow
{
    /// <summary>The last seven days.</summary>
    SevenDays = 0,

    /// <summary>The last fourteen days.</summary>
    FourteenDays,

    /// <summary>The last thirty days.</summary>
    ThirtyDays
}

/// <summary>The day counts for each supported practice window.</summary>
public static class CoachPracticeWindows
{
    /// <summary>The only day counts the coach can ask for.</summary>
    public static IReadOnlyList<int> AllowedDays { get; } = [7, 14, 30];

    /// <summary>Maps a window to its day count.</summary>
    public static int ToDays(this CoachPracticeWindow window) => window switch
    {
        CoachPracticeWindow.SevenDays => 7,
        CoachPracticeWindow.FourteenDays => 14,
        CoachPracticeWindow.ThirtyDays => 30,
        _ => throw new CoachToolException(
            CoachToolFailureKind.InvalidArgument,
            CoachToolNames.GetPracticeBalance,
            "The window is not one of the seven, fourteen, or thirty day windows.")
    };
}

/// <summary>
/// The constraints for a plan preview. The model can set these fields only.
/// The application validates and clamps every value before it builds the preview.
/// </summary>
/// <remarks>
/// <para>
/// Every optional field is a nullable type, including the flags and the energy level. A
/// model routinely answers a closed schema by sending <c>null</c> for a field it has no
/// value for, and a non-nullable value type cannot bind that: the live harness run failed
/// with <c>JsonException: Cannot convert null to Boolean. Path $.speechAllowed</c> before
/// the tool body ever ran. Nullable types make an explicit null and a missing field mean
/// the same safe thing.
/// </para>
/// <para>
/// A null flag means "the learner did not say", which the tool reads as the permissive
/// default (audio, speech, and typing all allowed) so no modality is removed on silence.
/// A null energy level reads as <see cref="CoachEnergyLevel.Normal"/>. The tool applies
/// these defaults; no null reaches <c>PlanConstraints</c>.
/// </para>
/// </remarks>
public sealed class CoachPlanPreviewArguments
{
    /// <summary>The permissive default for a modality flag the model left unset.</summary>
    public const bool DefaultModalityAllowed = true;

    /// <summary>The default energy level for an unset field.</summary>
    public const CoachEnergyLevel DefaultEnergyLevel = CoachEnergyLevel.Normal;

    [Description("The minutes the learner has for this session. The range is 3 to 90. Leave empty to use the preferred session length.")]
    public int? AvailableMinutes { get; set; }

    [Description("True if the learner can listen to audio. Leave empty if the learner did not say; the plan then allows audio.")]
    public bool? AudioAllowed { get; set; }

    [Description("True if the learner can speak. Leave empty if the learner did not say; the plan then allows speech.")]
    public bool? SpeechAllowed { get; set; }

    [Description("True if the learner can type. Leave empty if the learner did not say; the plan then allows typing.")]
    public bool? TypingAllowed { get; set; }

    [Description("The skill to emphasize. Leave empty for no emphasis.")]
    public CoachSkillEmphasis? SkillEmphasis { get; set; }

    [Description("A goal tag from the resource catalog, or the value other. Leave empty for no goal.")]
    public string? GoalTag { get; set; }

    [Description("The days until the goal. The range is 1 to 180. Leave empty for no goal date.")]
    public int? GoalHorizonDays { get; set; }

    [Description("The energy level of the learner. Leave empty if the learner did not say; the plan then uses the normal level.")]
    public CoachEnergyLevel? EnergyLevel { get; set; }
}
