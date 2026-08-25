namespace SentenceStudio.Api.Coach.Validation;

/// <summary>The kind of rule a coach validation violation broke.</summary>
public enum CoachViolationKind
{
    /// <summary>A shape carries identity data or embargoed content.</summary>
    Embargo = 0,

    /// <summary>Text repeats a word, a translation, or an example that is due for review.</summary>
    AnswerLeak,

    /// <summary>Text makes a claim the coach must not make.</summary>
    BannedClaim,

    /// <summary>Text or a member is longer than the limit.</summary>
    LengthLimit,

    /// <summary>The intent members do not agree with each other.</summary>
    IntentShape,

    /// <summary>Evidence has no window, or the window is not allowed.</summary>
    EvidenceWindow,

    /// <summary>A preview names a resource the learner does not own.</summary>
    Ownership,

    /// <summary>Text tries to name a data command or a route.</summary>
    WriteCommand,

    /// <summary>A tool is not on the allow-list.</summary>
    ToolAllowList
}

/// <summary>
/// One validation violation. The evidence is masked, so a violation record
/// never repeats the embargoed value it found.
/// </summary>
public sealed record CoachViolation(
    CoachViolationKind Kind,
    string Code,
    string Message,
    string? MaskedEvidence = null);

/// <summary>
/// The result of a coach validation.
/// The application refuses the turn when the result is not valid.
/// The application does not ask the model to repair the answer.
/// </summary>
public sealed record CoachValidationResult(IReadOnlyList<CoachViolation> Violations)
{
    /// <summary>A result with no violation.</summary>
    public static CoachValidationResult Valid { get; } = new(Array.Empty<CoachViolation>());

    /// <summary>True when the answer passed every rule.</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>Builds a result from a list of violations.</summary>
    public static CoachValidationResult From(IEnumerable<CoachViolation> violations)
    {
        var list = violations as IReadOnlyList<CoachViolation> ?? violations.ToList();
        return list.Count == 0 ? Valid : new CoachValidationResult(list);
    }

    /// <summary>Masks a value so a log or a test failure never shows it.</summary>
    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "***";
        }

        return value.Length == 1
            ? "*"
            : string.Concat(value.AsSpan(0, 1), new string('*', Math.Min(value.Length - 1, 6)));
    }
}
