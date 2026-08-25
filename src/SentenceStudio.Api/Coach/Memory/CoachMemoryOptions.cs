using System.ComponentModel.DataAnnotations;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// Settings for learner memory, bound from the <c>Coach:Memory</c> configuration section.
/// </summary>
/// <remarks>
/// Every default is the safe one. An environment that says nothing about memory gets no memory:
/// the endpoints are absent, the selector returns nothing, and writes are refused.
/// </remarks>
public sealed class CoachMemoryOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Coach:Memory";

    /// <summary>
    /// The master switch. Default false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Stops the selector from placing memory in prompts while leaving the learner's saved facts
    /// intact and manageable. This is the "pause, do not delete" control.
    /// </summary>
    public bool SelectionPaused { get; set; }

    /// <summary>How many facts the selector may place in one prompt.</summary>
    [Range(0, CoachMemoryLimits.ContextFactsMax)]
    public int MaxContextFacts { get; set; } = CoachMemoryLimits.ContextFactsMax;

    /// <summary>The estimated token ceiling for the whole memory block.</summary>
    [Range(0, CoachMemoryLimits.ContextTokensMax)]
    public int MaxContextTokens { get; set; } = CoachMemoryLimits.ContextTokensMax;

    /// <summary>
    /// How long an undecided candidate survives before it stops being offered. Zero means a
    /// candidate never expires on its own.
    /// </summary>
    [Range(0, 365)]
    public int CandidateExpiryDays { get; set; } = 30;

    /// <summary>
    /// How long an approved fact stays eligible. Zero means it stays until the learner changes or
    /// forgets it.
    /// </summary>
    [Range(0, 3650)]
    public int ActiveFactExpiryDays { get; set; }

    /// <summary>How many approved facts one learner may hold.</summary>
    [Range(1, CoachMemoryLimits.ActiveFactsMax)]
    public int MaxActiveFacts { get; set; } = CoachMemoryLimits.ActiveFactsMax;

    /// <summary>How many undecided candidates one learner may hold.</summary>
    [Range(1, CoachMemoryLimits.CandidatesMax)]
    public int MaxCandidates { get; set; } = CoachMemoryLimits.CandidatesMax;
}

/// <summary>
/// Clamps memory settings at startup so a misconfigured environment cannot widen a hard bound.
/// </summary>
public sealed class CoachMemoryOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<CoachMemoryOptions>
{
    /// <inheritdoc />
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, CoachMemoryOptions options)
    {
        if (options is null)
        {
            return Microsoft.Extensions.Options.ValidateOptionsResult.Fail("Coach memory options are missing.");
        }

        var failures = new List<string>();

        if (options.MaxContextFacts is < 0 or > CoachMemoryLimits.ContextFactsMax)
        {
            failures.Add($"{SectionPath(nameof(options.MaxContextFacts))} must be between 0 and {CoachMemoryLimits.ContextFactsMax}.");
        }

        if (options.MaxContextTokens is < 0 or > CoachMemoryLimits.ContextTokensMax)
        {
            failures.Add($"{SectionPath(nameof(options.MaxContextTokens))} must be between 0 and {CoachMemoryLimits.ContextTokensMax}.");
        }

        if (options.MaxActiveFacts is < 1 or > CoachMemoryLimits.ActiveFactsMax)
        {
            failures.Add($"{SectionPath(nameof(options.MaxActiveFacts))} must be between 1 and {CoachMemoryLimits.ActiveFactsMax}.");
        }

        if (options.MaxCandidates is < 1 or > CoachMemoryLimits.CandidatesMax)
        {
            failures.Add($"{SectionPath(nameof(options.MaxCandidates))} must be between 1 and {CoachMemoryLimits.CandidatesMax}.");
        }

        if (options.CandidateExpiryDays is < 0 or > 365)
        {
            failures.Add($"{SectionPath(nameof(options.CandidateExpiryDays))} must be between 0 and 365.");
        }

        if (options.ActiveFactExpiryDays is < 0 or > 3650)
        {
            failures.Add($"{SectionPath(nameof(options.ActiveFactExpiryDays))} must be between 0 and 3650.");
        }

        return failures.Count == 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Success
            : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(failures);
    }

    private static string SectionPath(string property) => $"{CoachMemoryOptions.SectionName}:{property}";
}
