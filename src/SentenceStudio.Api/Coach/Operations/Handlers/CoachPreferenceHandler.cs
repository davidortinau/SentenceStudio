using System.ComponentModel;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Operations.Handlers;

/// <summary>Arguments for changing one of the learner's own settings.</summary>
/// <remarks>
/// One setting per call, named from a closed list. A general "apply this object to my profile"
/// shape would let the model reach any column on <see cref="UserProfile"/>, including the email
/// address and the stored API key, and no amount of downstream filtering would make that shape
/// safe to expose.
/// </remarks>
public sealed record CoachPreferenceChangeArgs(
    [property: Description(
        "Which setting to change. No setting is currently approved for change, so this tool "
        + "declines every value and nothing is written. Tell the learner to change the setting "
        + "in the app's own settings screen.")]
    string Setting,
    [property: Description("The new value, as text. Numbers and true/false are given as text too.")]
    string Value);

/// <summary>The single setting an update replaced.</summary>
public sealed record CoachPreferencePriorState(string Setting, string? Value);

/// <summary>
/// Changes one learner-owned setting.
/// </summary>
/// <remarks>
/// <para>
/// The settable list is closed, and everything not on it is unreachable rather than merely
/// discouraged: email, the OpenAI key, the identifier, and the creation timestamp are absent from
/// the switch, so there is no argument that reaches them.
/// </para>
/// <para>
/// The list is currently empty, which is what RFC §6.5 specifies for V1: no setting ships as
/// changeable by the coach until Captain approves that specific setting. The tool stays
/// registered and refuses, rather than being removed, so a model that asks gets a stable, typed
/// "not available" instead of an unknown-tool error, and so the approved-field machinery below
/// stays reviewed and tested against the day a field is approved.
/// </para>
/// <para>
/// Two of the candidate settings would be protected rather than soft if approved. Changing the
/// target or native language re-points generation, planning, and every future review at a
/// different language, which is a consequence broad enough that a one-tap accept would
/// understate it.
/// </para>
/// </remarks>
public sealed class CoachPreferenceChangeHandler : CoachWriteHandlerBase<CoachPreferenceChangeArgs>
{
    private const string TargetLanguage = "target_language";
    private const string NativeLanguage = "native_language";
    private const string DisplayLanguage = "display_language";
    private const string SessionMinutes = "session_minutes";
    private const string CefrLevel = "cefr_level";
    private const string QuizShowTextWithPhoto = "quiz_show_text_with_photo";

    private const int ValueMaxLength = 60;
    private const int SessionMinutesMin = 5;
    private const int SessionMinutesMax = 180;

    /// <summary>
    /// The settings the coach may change. Empty until Captain approves a specific setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC §6.5: "V1 allowed fields: empty set until Captain approves specific fields. Unknown
    /// field → validation error, no mutation." An empty allow-list is the strongest form of that
    /// rule — there is no name the model can send that reaches a write, so the refusal cannot be
    /// wrong about which settings are safe.
    /// </para>
    /// <para>
    /// It also keeps <see cref="QuizShowTextWithPhoto"/> unreachable. That setting decides whether
    /// a quiz can hide the target-language term next to a photo, which is a product-pedagogy
    /// question the Learning Value Gate has to answer before anything — least of all a model — can
    /// flip it on a learner's behalf.
    /// </para>
    /// <para>
    /// Adding a name here is the whole of approving a setting: the normalizer, the applier, and
    /// the label already exist for each candidate below and are covered by tests, so an approval
    /// is a one-line change that a reviewer can read in full.
    /// </para>
    /// </remarks>
    private static readonly string[] SettableNames = [];

    /// <summary>
    /// The candidate settings, in the order RFC §6.5 lists them. Not settable — see
    /// <see cref="SettableNames"/>.
    /// </summary>
    /// <remarks>
    /// Kept as a named set so the refusal tests can prove every candidate is refused, rather than
    /// proving it for whichever names somebody remembered to type into a test.
    /// </remarks>
    public static readonly string[] CandidateNames =
    [
        TargetLanguage, NativeLanguage, DisplayLanguage, SessionMinutes, CefrLevel, QuizShowTextWithPhoto
    ];

    /// <summary>True when no setting is approved for change, so every request is refused.</summary>
    public static bool IsClosed => SettableNames.Length == 0;

    /// <summary>
    /// The settings whose consequences reach past the setting itself.
    /// </summary>
    /// <remarks>
    /// Consulted by the registry to decide this tool's risk class, and by the ledger to decide
    /// which approval channel a given proposal needs.
    /// </remarks>
    private static readonly string[] ProtectedNames = [TargetLanguage, NativeLanguage];

    private static readonly string[] CefrLevels = ["A1", "A2", "B1", "B2", "C1", "C2"];

    private readonly UserProfileRepository _profiles;
    private readonly CoachWriteOwnership _ownership;

    public CoachPreferenceChangeHandler(UserProfileRepository profiles, CoachWriteOwnership ownership)
    {
        _profiles = profiles;
        _ownership = ownership;
    }

    public override string ToolName => CoachToolNames.ProposePreferenceChange;

    /// <summary>
    /// Protected, because the tool can carry a language change.
    /// </summary>
    /// <remarks>
    /// The risk class is per tool, not per call, and a tool that can reach a broad-consequence
    /// setting has to be classified by its most consequential reachable outcome. Splitting the
    /// languages into their own tool would let the rest be soft, and is the obvious refinement if
    /// the extra confirmation proves to be friction; classifying the whole tool downward would
    /// not be.
    /// </remarks>
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteHard;

    /// <inheritdoc />
    /// <remarks>
    /// Confirmed because it changes how the whole app behaves, reversible because the previous
    /// value is a single field that was captured before the change. A learner who confirms a
    /// language switch and immediately regrets it should not have to go and find the setting.
    /// </remarks>
    public override CoachWriteUndoKind UndoKind => CoachWriteUndoKind.RestoreFields;

    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.UserProfile;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachPreferenceChangeArgs args, CancellationToken cancellationToken)
    {
        var (profile, setting, value, current) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        var lines = new List<string> { $"{Label(setting)}: {Display(current)} \u2192 {Display(value)}" };
        if (ProtectedNames.Contains(setting, StringComparer.Ordinal))
        {
            lines.Add("This changes what future practice and plans are generated in.");
        }

        return new CoachWritePreview(
            $"Change {Label(setting).ToLowerInvariant()} to {Display(value)}",
            lines,
            profile.Id,
            Canonical(new CoachPreferenceChangeArgs(setting, value)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachPreferenceChangeArgs args, CancellationToken cancellationToken)
    {
        var (profile, setting, value, current) = await ValidateAsync(userProfileId, args, cancellationToken)
            .ConfigureAwait(false);

        var prior = new CoachPreferencePriorState(setting, current);
        await ApplyAsync(profile, setting, value).ConfigureAwait(false);

        return new CoachWriteExecution(
            $"Changed {Label(setting).ToLowerInvariant()} to {Display(value)}",
            new[] { $"{Label(setting)}: {Display(current)} \u2192 {Display(value)}" },
            profile.Id,
            Canonical(prior));
    }

    private async Task<(UserProfile Profile, string Setting, string Value, string? Current)> ValidateAsync(
        string userProfileId, CoachPreferenceChangeArgs args, CancellationToken cancellationToken)
    {
        var setting = Clean(args.Setting, 40).ToLowerInvariant();

        // Refused before the profile is loaded, so a closed tool issues no query about the learner
        // at all. The message names no candidate settings: listing what is not settable would read
        // as a menu, and the model would work through it.
        if (IsClosed)
        {
            throw InvalidArgument(
                "Changing settings from here is not available. Ask the learner to change it in the app's settings.");
        }

        if (!SettableNames.Contains(setting, StringComparer.Ordinal))
        {
            throw InvalidArgument($"A setting must be one of: {string.Join(", ", SettableNames)}.");
        }

        // Loaded through the ownership helper, which filters on the authenticated identity. The
        // repository's own SaveAsync takes no user id and would happily update any row it is
        // handed, so this load is the ownership check.
        var profile = await _ownership.FindProfileAsync(userProfileId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var value = Clean(args.Value, ValueMaxLength);
        if (value.Length == 0)
        {
            throw InvalidArgument("A setting needs a value.");
        }

        var (normalized, current) = Normalize(profile, setting, value);
        if (string.Equals(current ?? string.Empty, normalized, StringComparison.Ordinal))
        {
            throw InvalidArgument("That setting is already set to that value.");
        }

        return (profile, setting, normalized, current);
    }

    private (string Value, string? Current) Normalize(UserProfile profile, string setting, string value) =>
        setting switch
        {
            TargetLanguage => (Language(value), profile.TargetLanguage),
            NativeLanguage => (Language(value), profile.NativeLanguage),
            DisplayLanguage => (Language(value), profile.DisplayLanguage),
            SessionMinutes => (Minutes(value), profile.PreferredSessionMinutes.ToString()),
            CefrLevel => (Cefr(value), profile.TargetCEFRLevel),
            QuizShowTextWithPhoto => (Boolean(value),
                profile.VocabQuizShowTextWithPhoto ? "true" : "false"),
            _ => throw InvalidArgument("That setting cannot be changed here.")
        };

    private async Task ApplyAsync(UserProfile profile, string setting, string value)
    {
        // The narrow setters are preferred where they exist: they issue a single-property update
        // and cannot clobber a concurrent change to an unrelated column, which the whole-entity
        // save can.
        if (setting == QuizShowTextWithPhoto)
        {
            if (!await _profiles
                    .SaveVocabQuizShowTextWithPhotoAsync(profile.Id, value == "true")
                    .ConfigureAwait(false))
            {
                throw DataAccessFailure(new InvalidOperationException("The setting was not saved."));
            }

            return;
        }

        switch (setting)
        {
            case TargetLanguage:
                profile.TargetLanguage = value;
                break;
            case NativeLanguage:
                profile.NativeLanguage = value;
                break;
            case DisplayLanguage:
                profile.DisplayLanguage = value;
                break;
            case SessionMinutes:
                profile.PreferredSessionMinutes = int.Parse(value);
                break;
            case CefrLevel:
                profile.TargetCEFRLevel = value;
                break;
            default:
                throw InvalidArgument("That setting cannot be changed here.");
        }

        // The repository answers with the number of rows it wrote and -1 when it failed. Zero is
        // treated as a failure too: this path always issues an update for a detached profile, so
        // zero rows means the row was not there to write, and reporting that as a saved setting
        // would put a receipt in front of the learner for a change nothing kept.
        if (await _profiles.SaveAsync(profile).ConfigureAwait(false) <= 0)
        {
            throw DataAccessFailure(new InvalidOperationException("The setting was not saved."));
        }
    }

    protected override async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        CoachPreferenceChangeArgs args,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        var prior = BindPriorState<CoachPreferencePriorState>(priorStateJson);

        // Reloaded through the ownership helper rather than trusting the stored identifier, so an
        // undo is scoped by the same check the original change was.
        var profile = await _ownership.FindProfileAsync(userProfileId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFoundOrNotOwned();

        var restored = prior.Value ?? string.Empty;
        if (restored.Length == 0)
        {
            throw InvalidArgument("The previous setting was empty and cannot be restored.");
        }

        await ApplyAsync(profile, prior.Setting, restored).ConfigureAwait(false);

        return new CoachWriteExecution(
            $"Put {Label(prior.Setting).ToLowerInvariant()} back to {Display(restored)}",
            new[] { $"{Label(prior.Setting)}: {Display(restored)}" },
            profile.Id,
            PriorStateJson: null);
    }

    private string Language(string value)
    {
        // Rejects anything that is not a plain language name. The value is stored and later fed
        // to generation prompts, so free text here would be a way to put attacker-chosen content
        // into a prompt through a settings write.
        foreach (var c in value)
        {
            if (!char.IsLetter(c) && c != ' ' && c != '-' && c != '\'')
            {
                throw InvalidArgument("A language is a plain name, such as Korean or Spanish.");
            }
        }

        return value;
    }

    private string Minutes(string value)
    {
        if (!int.TryParse(value, out var minutes)
            || minutes < SessionMinutesMin
            || minutes > SessionMinutesMax)
        {
            throw InvalidArgument(
                $"A session length is a whole number of minutes between {SessionMinutesMin} and {SessionMinutesMax}.");
        }

        return minutes.ToString();
    }

    private string Cefr(string value)
    {
        var upper = value.ToUpperInvariant();
        if (!CefrLevels.Contains(upper, StringComparer.Ordinal))
        {
            throw InvalidArgument($"A level must be one of: {string.Join(", ", CefrLevels)}.");
        }

        return upper;
    }

    private string Boolean(string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => "true",
        "false" or "no" or "off" => "false",
        _ => throw InvalidArgument("That setting is either true or false.")
    };

    private static string Label(string setting) => setting switch
    {
        TargetLanguage => "Language being learned",
        NativeLanguage => "Own language",
        DisplayLanguage => "App language",
        SessionMinutes => "Session length",
        CefrLevel => "Target level",
        QuizShowTextWithPhoto => "Show text with photos",
        _ => setting
    };

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(not set)" : value;
}
