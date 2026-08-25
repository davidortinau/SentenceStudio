using System.Text.RegularExpressions;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// Checks the shape of a coach turn before the application acts on it.
/// The validator refuses a turn that makes a banned claim, that names a data
/// command, that is too long, that does not agree with itself, that shows
/// evidence without a correct window, or that previews a resource the learner
/// does not own.
/// The application never asks the model to repair a refused turn.
/// </summary>
/// <remarks>
/// Active call sites in the reducer: <c>ValidateIntent</c> on every completed turn,
/// <c>ValidateEvidence</c> on the evidence a turn shows, and <c>ValidateOwnedPreview</c>
/// before any model-derived change is applied — a direct request read from learner text and
/// an accepted suggestion both go through it. A structured constraint action from the UI is
/// deterministic input, so it uses plan validation only.
/// </remarks>
public sealed class CoachIntentValidator
{
    private const int MaxCoachMessageLength = 400;
    private const int MaxClarifyingQuestionLength = 200;
    private const int MaxSuggestionIdLength = 64;
    private const int MaxEvidenceReferences = 6;

    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>Claims about level, aptitude, health, or a time to fluency.</summary>
    private static readonly (Regex Pattern, string Code)[] BannedClaims =
    [
        (new Regex(@"\b(you are|you're|your level is|you have reached)\s+(a\s+|an\s+)?(a1|a2|b1|b2|c1|c2|beginner|intermediate|advanced|fluent|native)\b", Options), "proficiency_claim"),
        (new Regex(@"\b(time to fluency|you will be fluent|fluent (in|by|within)\s+\d+)\b", Options), "fluency_timeline"),
        (new Regex(@"\b(fast learner|slow learner|natural talent|you have talent|gifted learner|your aptitude)\b", Options), "aptitude_claim"),
        (new Regex(@"\b(dyslexi\w*|adhd|diagnos\w*|disorder)\b", Options), "health_claim"),
        (new Regex(@"\b(guarantee|guaranteed)\s+(you|results|fluency|progress)\b", Options), "guarantee_claim")
    ];

    /// <summary>Text that tries to name a data command, a route, or a link.</summary>
    private static readonly (Regex Pattern, string Code)[] WriteCommands =
    [
        (new Regex(@"\b(drop|truncate)\s+table\b", Options), "sql_command"),
        (new Regex(@"\bdelete\s+from\b", Options), "sql_command"),
        (new Regex(@"\binsert\s+into\b", Options), "sql_command"),
        (new Regex(@"\bupdate\s+\w+\s+set\b", Options), "sql_command"),
        (new Regex(@"https?://", Options), "external_link"),
        (new Regex(@"(^|\s)/api/", Options), "route_reference"),
        (new Regex(@"\b(userprofileid|user_profile_id|tenantid)\b", Options), "identity_reference")
    ];

    /// <summary>Checks one turn intent.</summary>
    public CoachValidationResult ValidateIntent(CoachTurnIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var violations = new List<CoachViolation>();

        // A kind outside the enum has no reducer branch, no surfacing rule, and no meaning. It
        // arrives when the model emits a number or a name the contract does not define, and
        // deserialization of an undeclared value leaves the field at whatever it cast to. Refuse
        // it here rather than let a later switch pick a default arm for it.
        if (!Enum.IsDefined(intent.Kind))
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.IntentShape,
                "undefined_intent_kind",
                "The turn names an intent kind the contract does not define."));
        }

        CheckText(intent.CoachMessage, nameof(CoachTurnIntent.CoachMessage), MaxCoachMessageLength, violations);
        CheckText(intent.ClarifyingQuestion, nameof(CoachTurnIntent.ClarifyingQuestion), MaxClarifyingQuestionLength, violations);

        if (intent.PendingSuggestionId is { Length: > MaxSuggestionIdLength })
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.LengthLimit,
                "suggestion_id_length",
                $"The suggestion identifier is longer than {MaxSuggestionIdLength} characters."));
        }

        if (intent.EvidenceReferences.Count > MaxEvidenceReferences)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.LengthLimit,
                "evidence_count",
                $"The answer names more than {MaxEvidenceReferences} facts."));
        }

        CheckIntentShape(intent, violations);
        CheckEvidenceReferences(intent, violations);

        return CoachValidationResult.From(violations);
    }

    /// <summary>Checks the evidence the application shows to the learner.</summary>
    public CoachValidationResult ValidateEvidence(IEnumerable<CoachEvidenceDto> evidence, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var violations = new List<CoachViolation>();

        foreach (var item in evidence)
        {
            if (item.WindowEndDate < item.WindowStartDate)
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.EvidenceWindow,
                    "window_order",
                    $"The {item.Kind} window ends before it starts."));
                continue;
            }

            if (item.WindowEndDate > today)
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.EvidenceWindow,
                    "window_future",
                    $"The {item.Kind} window ends in the future."));
            }

            var days = item.WindowEndDate.DayNumber - item.WindowStartDate.DayNumber + 1;
            if (item.Kind == CoachEvidenceKind.PracticeBalance && !CoachPracticeWindows.AllowedDays.Contains(days))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.EvidenceWindow,
                    "window_length",
                    "A practice balance window must be seven, fourteen, or thirty days."));
            }
        }

        return CoachValidationResult.From(violations);
    }

    /// <summary>
    /// Checks that a plan preview names owned resources only.
    /// The application supplies the owned identifiers from the resource catalog.
    /// </summary>
    public CoachValidationResult ValidateOwnedPreview(
        PlanPreviewSummary preview,
        IReadOnlyCollection<string> ownedResourceIds)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return ValidateOwnedPreview(
            preview.PreviewId,
            preview.Items.Select(i => i.ResourceId).Prepend(preview.PrimaryResourceId),
            ownedResourceIds);
    }

    /// <summary>
    /// Checks a trusted, server-built preview before a model-derived change may be applied.
    /// </summary>
    /// <param name="previewId">
    /// The deterministic preview identifier or plan hash the server itself produced. A
    /// missing identifier means the caller has no proof of which plan it validated, so the
    /// check fails rather than passing an unidentified plan.
    /// </param>
    /// <param name="referencedResourceIds">Every resource the preview names.</param>
    /// <param name="ownedResourceIds">Every resource the trusted learner owns.</param>
    public CoachValidationResult ValidateOwnedPreview(
        string? previewId,
        IEnumerable<string?> referencedResourceIds,
        IReadOnlyCollection<string> ownedResourceIds)
    {
        ArgumentNullException.ThrowIfNull(referencedResourceIds);
        ArgumentNullException.ThrowIfNull(ownedResourceIds);

        var owned = new HashSet<string>(ownedResourceIds, StringComparer.Ordinal);
        var violations = new List<CoachViolation>();

        if (string.IsNullOrWhiteSpace(previewId))
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.Ownership,
                "missing_preview_id",
                "The preview has no server-built identifier, so its contents cannot be trusted."));
        }

        foreach (var resourceId in referencedResourceIds)
        {
            if (!string.IsNullOrEmpty(resourceId) && !owned.Contains(resourceId))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Ownership,
                    "unowned_resource",
                    "The preview names a resource the learner does not own.",
                    CoachValidationResult.Mask(resourceId)));
            }
        }

        return CoachValidationResult.From(violations);
    }

    private static void CheckText(string? text, string member, int maxLength, List<CoachViolation> violations)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Length > maxLength)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.LengthLimit,
                "text_length",
                $"{member} is longer than {maxLength} characters."));
        }

        foreach (var (pattern, code) in BannedClaims)
        {
            if (pattern.IsMatch(text))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.BannedClaim,
                    code,
                    $"{member} makes a claim the coach must not make."));
            }
        }

        foreach (var (pattern, code) in WriteCommands)
        {
            if (pattern.IsMatch(text))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.WriteCommand,
                    code,
                    $"{member} names a command, a route, or a link."));
            }
        }
    }

    private static void CheckIntentShape(CoachTurnIntent intent, List<CoachViolation> violations)
    {
        var hasDelta = HasAnyChange(intent.ConstraintDelta);

        void Require(bool condition, string code, string message)
        {
            if (!condition)
            {
                violations.Add(new CoachViolation(CoachViolationKind.IntentShape, code, message));
            }
        }

        switch (intent.Kind)
        {
            case CoachIntentKind.DirectConstraintChange:
            case CoachIntentKind.SuggestConstraintChange:
                Require(hasDelta, "delta_required", $"{intent.Kind} needs at least one constraint change.");
                break;

            case CoachIntentKind.AcceptPendingSuggestion:
                Require(!string.IsNullOrWhiteSpace(intent.PendingSuggestionId),
                    "suggestion_required", "An acceptance must name the suggestion.");
                Require(intent.AcceptanceState == CoachAcceptanceState.Accepted,
                    "acceptance_state", "An acceptance must set the acceptance state to Accepted.");
                Require(!hasDelta, "delta_forbidden", "An acceptance must carry no new constraint change.");
                break;

            case CoachIntentKind.RejectPendingSuggestion:
                Require(!string.IsNullOrWhiteSpace(intent.PendingSuggestionId),
                    "suggestion_required", "A rejection must name the suggestion.");
                Require(intent.AcceptanceState == CoachAcceptanceState.Rejected,
                    "acceptance_state", "A rejection must set the acceptance state to Rejected.");
                Require(!hasDelta, "delta_forbidden", "A rejection must carry no constraint change.");
                break;

            case CoachIntentKind.AskClarification:
                Require(!string.IsNullOrWhiteSpace(intent.ClarifyingQuestion),
                    "question_required", "A clarification must hold one question.");
                Require(!hasDelta, "delta_forbidden", "A clarification must carry no constraint change.");
                break;

            case CoachIntentKind.NoChange:
            case CoachIntentKind.OffTopic:
                Require(!hasDelta, "delta_forbidden", $"{intent.Kind} must carry no constraint change.");
                break;

            case CoachIntentKind.PedagogicalAnswer:
                Require(intent.PedagogicalAnswer is not null,
                    "answer_required", "A pedagogical answer must carry an answer.");
                Require(!hasDelta, "delta_forbidden", "A pedagogical answer must carry no constraint change.");
                Require(string.IsNullOrWhiteSpace(intent.ClarifyingQuestion),
                    "question_forbidden", "A pedagogical answer must not also ask a clarifying question.");
                break;
        }

        // An answer may accompany only the two kinds that never write on their own: the
        // answer-only turn, and the mixed turn whose plan change waits for an explicit
        // acceptance. Anywhere else it would ride along with a write.
        if (intent.PedagogicalAnswer is not null
            && intent.Kind is not (CoachIntentKind.PedagogicalAnswer or CoachIntentKind.SuggestConstraintChange))
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.IntentShape,
                "answer_forbidden",
                $"{intent.Kind} must carry no pedagogical answer."));
        }

        if (intent.AcceptanceState == CoachAcceptanceState.Ambiguous
            && intent.Kind != CoachIntentKind.AskClarification)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.IntentShape,
                "ambiguous_requires_question",
                "An unclear answer must ask a question and must change nothing."));
        }
    }

    private static void CheckEvidenceReferences(CoachTurnIntent intent, List<CoachViolation> violations)
    {
        foreach (var reference in intent.EvidenceReferences)
        {
            if (reference.WindowDays is { } days && !CoachPracticeWindows.AllowedDays.Contains(days))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.EvidenceWindow,
                    "window_length",
                    "A fact window must be seven, fourteen, or thirty days."));
            }

            if (reference.Kind == CoachEvidenceKind.PracticeBalance && reference.WindowDays is null)
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.EvidenceWindow,
                    "window_required",
                    "A practice balance fact must state its window."));
            }
        }
    }

    /// <summary>True when the change touches at least one constraint field.</summary>
    internal static bool HasAnyChange(CoachConstraintDeltaIntent? delta) =>
        delta is not null
        && (delta.AvailableMinutes is not null
            || delta.AudioAllowed is not null
            || delta.SpeechAllowed is not null
            || delta.TypingAllowed is not null
            || delta.SkillEmphasis is not null
            || delta.ClearSkillEmphasis
            || !string.IsNullOrWhiteSpace(delta.GoalTag)
            || delta.ClearGoalTag
            || delta.GoalHorizonDays is not null
            || delta.ClearGoalHorizonDays
            || delta.EnergyLevel is not null
            || !string.IsNullOrWhiteSpace(delta.VocabularyFocusDescription)
            || delta.ClearVocabularyFocus);
}
