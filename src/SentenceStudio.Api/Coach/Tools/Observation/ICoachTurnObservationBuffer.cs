namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Every tool call this turn made, in the order it made them.
/// </summary>
/// <remarks>
/// <para>
/// Registered <b>Scoped</b>, so "the turn" is the request scope and no cross-turn state exists to
/// leak or to clear. A singleton would accumulate one learner's reads into another's trace, which
/// is the failure mode this shape exists to make impossible rather than to police.
/// </para>
/// <para>
/// Read-only to consumers. The seam is the only writer, through
/// <see cref="ICoachTurnObservationSink"/>, so a projection cannot quietly append to the record of
/// what happened.
/// </para>
/// </remarks>
public interface ICoachTurnObservationBuffer
{
    /// <summary>The turn's completed tool calls, ordinal-ascending.</summary>
    IReadOnlyList<CoachToolCallObservation> Observations { get; }

    /// <summary>Tool calls counted against the turn's budget, when the turn boundary recorded it.</summary>
    /// <remarks>
    /// Distinct from <c>Observations.Count</c> on purpose. A budget refusal is raised by the outer
    /// budget wrapper before the seam runs, so it is counted here and never appears as an
    /// observation — which means these two numbers legitimately differ on exactly the turns worth
    /// looking at.
    /// </remarks>
    int? BudgetUsed { get; }

    /// <summary>The turn's tool-call cap, when the turn boundary recorded it.</summary>
    int? BudgetLimit { get; }
}

/// <summary>The write half of the buffer. Held only by the seam.</summary>
/// <remarks>
/// Split from <see cref="ICoachTurnObservationBuffer"/> so a consumer that resolves the buffer to
/// project evidence or a trace cannot also write to it. Both halves are the same scoped instance;
/// the split is about what a holder is permitted to do, not about lifetime.
/// </remarks>
public interface ICoachTurnObservationSink
{
    /// <summary>Appends one completed observation. Must not throw.</summary>
    void Add(CoachToolCallObservation observation);

    /// <summary>
    /// Records the turn's budget once, at the turn boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once, and here rather than as a synthetic observation. A refusal has no tool call behind it —
    /// the outer wrapper raised it before the seam ran — so recording it as one would report a
    /// limit as a tool that failed.
    /// </para>
    /// <para>
    /// Last write wins, which is the honest rule for a value the boundary states once: a second
    /// call is a later, better-informed reading of the same turn.
    /// </para>
    /// </remarks>
    void RecordBudget(int used, int limit);
}

/// <summary>The in-memory, per-turn buffer.</summary>
/// <remarks>
/// <para>
/// Guarded by a lock rather than assumed single-threaded. Nothing in the shipped arms calls tools
/// concurrently today, but <c>AIFunction</c> invocation is an async surface a future arm may
/// parallelise, and a torn list here would corrupt the record of a turn rather than merely
/// reordering it.
/// </para>
/// <para>
/// Bounded by the turn's tool-call budget, which <c>CoachToolCallBudget</c> enforces outside this
/// seam — so the list cannot grow without limit even under a misbehaving model.
/// </para>
/// </remarks>
public sealed class CoachTurnObservationBuffer : ICoachTurnObservationBuffer, ICoachTurnObservationSink
{
    private readonly object _gate = new();
    private readonly List<CoachToolCallObservation> _observations = new();
    private int? _budgetUsed;
    private int? _budgetLimit;

    /// <inheritdoc />
    public IReadOnlyList<CoachToolCallObservation> Observations
    {
        get
        {
            lock (_gate)
            {
                // A copy, so a consumer enumerating the turn's reads cannot be tripped by a call
                // that completes while it is projecting.
                return _observations.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public int? BudgetUsed
    {
        get
        {
            lock (_gate)
            {
                return _budgetUsed;
            }
        }
    }

    /// <inheritdoc />
    public int? BudgetLimit
    {
        get
        {
            lock (_gate)
            {
                return _budgetLimit;
            }
        }
    }

    /// <inheritdoc />
    public void RecordBudget(int used, int limit)
    {
        lock (_gate)
        {
            _budgetUsed = used;
            _budgetLimit = limit;
        }
    }

    /// <inheritdoc />
    public void Add(CoachToolCallObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_gate)
        {
            _observations.Add(observation);
        }
    }
}
