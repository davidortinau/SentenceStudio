using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Turns a turn's observations into the content-free summary a stored outcome may keep.
/// </summary>
/// <remarks>
/// <para>
/// <b>The boundary is here, and it is one-way.</b> Everything upstream of this class is in-memory
/// and may hold a <see cref="CoachResultScope"/>; everything downstream is a closed code, a count,
/// a duration, or a bounded server identifier. Projecting rather than serializing is what keeps the
/// scope's six foundation members — and whatever shape it grows next — out of a protected column
/// nobody versioned.
/// </para>
/// <para>
/// <b>What is dropped, and why.</b> <c>Order</c>, <c>OrderHonored</c>, <c>Filters</c>,
/// <c>AsOfUtc</c> and the window dates are all closed or bounded and could ride along safely. They
/// are left out because a trace is read to answer "what did this turn look at, and did it get
/// everything" — the coverage, the definition, the three counts and the truncation flag answer
/// that, and every field beyond them is a protected column paid for on every turn to answer a
/// question nobody asked yet. Adding one later is additive and cheap; removing one from stored rows
/// is not.
/// </para>
/// <para>
/// <b>The subject code is dropped too.</b> It is closed and bounded, and it is already recorded
/// where it belongs — the opportunity ledger, whose whole purpose is aggregating "learners keep
/// asking for this". A second copy in the turn trace would be the same fact in two places with two
/// retention rules.
/// </para>
/// </remarks>
public static class CoachTurnTraceProjection
{
    /// <summary>
    /// The trace for a turn, or null when no observation was collected for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null means unobserved, not idle.</b> Two facts arrive here and they have opposite
    /// consequences. A <see langword="null"/> buffer says the turn was never observed — a row from
    /// before the buffer existed, or a host that did not register one — and nothing can be
    /// concluded from it. A buffer that <em>is</em> present and holds zero calls says something
    /// much stronger: this turn was watched, and it read nothing.
    /// </para>
    /// <para>
    /// <b>This used to return null for both, and that erased the stronger fact.</b> The original
    /// reasoning — "an empty trace section on every such row is a per-row cost paid to say nothing
    /// happened, which the absence already says" — was wrong in its premise. The absence does not
    /// say it, because the absence is also what an unobserved turn looks like. Two honesty rules
    /// gate themselves on exactly this distinction and say so in their own source:
    /// <c>CoachFabricatedCheckRule</c> bails when <c>Trace is null</c> because "no trace is no
    /// evidence of absence. Only a recorded turn can prove a check did not run." A present buffer
    /// holding zero calls <em>is</em> that recorded turn — and the projection was handing the rule
    /// the same <see langword="null"/> it hands an unobserved one, so an answer claiming a read
    /// that never happened shipped unaltered at Enforce.
    /// </para>
    /// <para>
    /// <b>Nothing is fabricated to make the distinction.</b> An empty turn projects an empty call
    /// list and whatever budget the buffer actually recorded — which, while W4 owns the trace and
    /// W6 owns the binding, is <see langword="null"/>/<see langword="null"/> in production. No
    /// synthetic call stands in for the absent ones, and no budget is invented: a fabricated entry
    /// would be a worse lie than the one this fixes, because it would claim a tool ran.
    /// </para>
    /// <para>
    /// <b>The stored trace changes with it, deliberately.</b> This projection feeds both the
    /// grounding context and the protected turn outcome, and the two are kept identical on purpose.
    /// Making only the in-memory copy honest would leave stored history unable to answer the very
    /// question the rules now answer — "did this turn read anything?" — and would put a second,
    /// quieter definition of "no trace" into rows that outlive every reader of this file. A stored
    /// section with zero calls is a positive record; a stored null goes back to meaning only
    /// "nothing was observed". The schema is unchanged: <c>Trace</c> was always nullable, an empty
    /// <c>Calls</c> list was always well-formed, and rows written before this still read exactly as
    /// they did.
    /// </para>
    /// </remarks>
    public static CoachTurnTraceSummary? Project(ICoachTurnObservationBuffer? buffer)
    {
        if (buffer is null)
        {
            return null;
        }

        var observations = buffer.Observations;

        // Zero-length when the turn called nothing. That is the answer, not a missing answer.
        var calls = new CoachTurnTraceEntry[observations.Count];
        for (var i = 0; i < observations.Count; i++)
        {
            calls[i] = Project(observations[i]);
        }

        return new CoachTurnTraceSummary(calls, buffer.BudgetUsed, buffer.BudgetLimit);
    }

    /// <summary>One observation, reduced to closed codes and counts.</summary>
    public static CoachTurnTraceEntry Project(CoachToolCallObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var scope = observation.Scope;

        return new CoachTurnTraceEntry(
            observation.Ordinal,

            // The registration's name, carried through from the seam and checked against the frozen
            // registry before it is written. The seam does read the registration today, so this
            // normally changes nothing — but "the caller is careful" is a fact about the call graph,
            // and the trace's one string exception is only defensible while it is a fact about the
            // boundary. A non-member collapses to the server constant; the entry and its ordinal
            // stay, so the turn keeps its length and its numbering.
            CoachTurnTraceToolName.Normalize(observation.ToolName),
            observation.Outcome,
            observation.FailureKind,
            observation.ArgumentMask,
            observation.ElapsedMs,

            // A refused or faulted call stated no scope, so its codes are the explicit "not stated"
            // members rather than a guess. Unspecified is a real answer here: it says the call never
            // got far enough to describe what it looked at.
            scope?.Coverage ?? CoachScopeCoverage.Unspecified,
            scope?.DefinitionCode ?? CoachScopeDefinition.Unspecified,
            scope?.WithheldReason ?? CoachScopeWithheldReason.None,
            scope?.MatchedCount,
            scope?.ReturnedCount,
            scope is null ? null : scope.WithheldCount,
            scope?.Truncated ?? false);
    }
}
