namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Hands out 1-based ordinals within one turn.
/// </summary>
/// <remarks>
/// <para>
/// One instance is created per <c>CoachToolFactory.CreateTools()</c> call and shared by every
/// wrapper in that tool set. Both agent arms build the tool set exactly once per turn —
/// <c>CoachToolCallBudget.Apply(_toolFactory.CreateTools())</c> — so "per tool set" and "per turn"
/// are the same thing, and the ordinal needs no ambient scope, no DI lifetime, and no reset.
/// </para>
/// <para>
/// Deliberately not derived from the buffer's length. The buffer is optional — a host with no
/// buffer registered still produces correctly ordered observations for the opportunity ledger — and
/// an ordinal that silently restarted at 1 when the buffer was absent would be a different number
/// depending on configuration.
/// </para>
/// <para>
/// <see cref="Interlocked"/> rather than a plain increment, for the same reason the buffer takes a
/// lock: tool invocation is an async surface a future arm may parallelise, and two calls sharing an
/// ordinal would misdescribe the turn.
/// </para>
/// </remarks>
public sealed class CoachToolCallSequence
{
    private int _issued;

    /// <summary>How many ordinals have been issued.</summary>
    public int Issued => Volatile.Read(ref _issued);

    /// <summary>The next ordinal, starting at 1.</summary>
    public int Next() => Interlocked.Increment(ref _issued);
}
