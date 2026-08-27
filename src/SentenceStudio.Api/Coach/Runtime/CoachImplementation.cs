namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Which Learning Coach arm serves a run.
/// The zero value is <see cref="Baseline"/> so an unset or unparsed configuration
/// value never silently selects the experimental harness arm.
/// </summary>
public enum CoachImplementation
{
    /// <summary>The plain <c>ChatClientAgent</c> arm. This is the shipping default.</summary>
    Baseline = 0,

    /// <summary>The <c>HarnessAgent</c> arm. Enabled only for comparison work.</summary>
    Harness
}
