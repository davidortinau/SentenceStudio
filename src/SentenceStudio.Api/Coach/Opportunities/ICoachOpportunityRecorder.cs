namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// The one place a Sam capability gap is written to the ledger.
/// </summary>
/// <remarks>
/// <para>
/// A single recorder rather than a per-boundary writer, because the guarantees that make this
/// table safe — owner resolution, closed-vocabulary validation, aggregate-only pointer stripping,
/// content-free logging, and never-throw semantics — have to hold on every path. Three writers
/// would be three chances to forget one.
/// </para>
/// <para>
/// <b>Implementations must never change the learner's response and must never throw.</b> The
/// contract is that every call is made <em>after</em> the turn result has already been computed,
/// and that a failure inside the recorder — a dropped connection, a missing identity, a bad
/// signal — degrades to "no row was written" and nothing else.
/// </para>
/// </remarks>
public interface ICoachOpportunityRecorder
{
    /// <summary>
    /// Records one occurrence, or increments today's count if the same learner already hit the
    /// same problem today. Never throws, never alters a turn, and no-ops when capture is off or
    /// no trusted owner is present.
    /// </summary>
    ValueTask RecordAsync(CoachOpportunitySignal signal, CancellationToken cancellationToken = default);
}

/// <summary>
/// The recorder a host uses when capture is off, or when a service was constructed without one.
/// </summary>
/// <remarks>
/// Exists so a call site never has to null-check, and so "capture disabled" is a registration
/// decision rather than a branch repeated at every boundary.
/// </remarks>
public sealed class NullCoachOpportunityRecorder : ICoachOpportunityRecorder
{
    /// <summary>The shared instance.</summary>
    public static ICoachOpportunityRecorder Instance { get; } = new NullCoachOpportunityRecorder();

    /// <inheritdoc />
    public ValueTask RecordAsync(
        CoachOpportunitySignal signal,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
