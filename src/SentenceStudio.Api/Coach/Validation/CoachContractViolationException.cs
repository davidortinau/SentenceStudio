namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// A coach safety contract failed. The run stops; nothing reaches the model, the learner,
/// or the database.
/// </summary>
/// <remarks>
/// This is deliberately an exception rather than a result code: every throw site is a
/// programming or configuration fault (a tool set that does not match the allow-list, a
/// coach answer shape that carries embargoed data), and a fault of that class must fail
/// closed rather than degrade into a partial answer.
/// </remarks>
public sealed class CoachContractViolationException : Exception
{
    public CoachContractViolationException(string contract, CoachValidationResult result)
        : base($"{contract}: {Describe(result)}")
    {
        Contract = contract;
        Violations = result.Violations;
    }

    /// <summary>Which contract failed, for a log tag with no learner data.</summary>
    public string Contract { get; }

    /// <summary>The violations, with masked evidence only.</summary>
    public IReadOnlyList<CoachViolation> Violations { get; }

    private static string Describe(CoachValidationResult result) =>
        result.Violations.Count == 0
            ? "the contract failed with no stated violation"
            : string.Join(" ", result.Violations.Select(v => $"[{v.Code}] {v.Message}"));
}
