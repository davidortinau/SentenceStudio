using Microsoft.Extensions.AI;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Counts tool invocations for one coach turn and refuses the call that would exceed the cap.
/// </summary>
/// <remarks>
/// <para>
/// The RFC caps a turn at twenty read-tool calls. That cap needs somewhere to live that a model
/// cannot argue with, and the iteration limit is not that place. <c>MaximumIterationsPerRequest</c>
/// bounds how many times the harness goes back to the model; it says nothing about how many tool
/// calls ride along in a single assistant message. One iteration carrying six parallel calls is a
/// perfectly ordinary thing for a model to emit, so an iteration limit of six is a tool-call limit
/// of six only by coincidence.
/// </para>
/// <para>
/// So the count is kept where the calls actually happen. Every function the factory hands out for
/// a turn shares one budget instance, each invocation decrements it, and the twenty-first call
/// fails with a typed error instead of running. The learner's data is never touched by a call that
/// exceeds the cap: the guard runs before the inner function, so no query is issued.
/// </para>
/// <para>
/// The budget is deliberately not a rate limiter and not a cost control. It is a bound on how much
/// of a learner's account a single turn can read, which is a containment property — if a prompt
/// injection ever talked the model into enumerating everything it could reach, this is the thing
/// that stops the enumeration part way rather than after it finished.
/// </para>
/// </remarks>
public sealed class CoachToolCallBudget
{
    /// <summary>The maximum number of read-tool calls one turn may make.</summary>
    /// <remarks>
    /// Twenty is the RFC figure. It sits far above what an honest turn needs — a plan revision
    /// reads a handful of summaries — and far below what an enumeration attempt would want.
    /// </remarks>
    public const int MaxCallsPerTurn = 20;

    private readonly int _limit;
    private int _used;

    /// <summary>Creates a budget for one turn.</summary>
    /// <param name="limit">The cap. Defaults to <see cref="MaxCallsPerTurn"/>.</param>
    public CoachToolCallBudget(int limit = MaxCallsPerTurn)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit, "A coach turn must be allowed at least one tool call.");
        }

        _limit = limit;
    }

    /// <summary>The cap for this turn.</summary>
    public int Limit => _limit;

    /// <summary>How many calls have been consumed so far.</summary>
    public int Used => Volatile.Read(ref _used);

    /// <summary>How many calls remain. Never negative.</summary>
    public int Remaining => Math.Max(0, _limit - Used);

    /// <summary>
    /// Consumes one call. Throws <see cref="CoachToolException"/> when the cap is already spent.
    /// </summary>
    /// <param name="toolName">The tool being called, for the error message.</param>
    /// <remarks>
    /// <see cref="Interlocked.Increment(ref int)"/> rather than a plain increment because the
    /// harness may dispatch parallel tool calls from one assistant message, and two calls racing
    /// past the same last slot would defeat the cap in exactly the case it matters.
    /// </remarks>
    public void Consume(string toolName)
    {
        var used = Interlocked.Increment(ref _used);
        if (used > _limit)
        {
            throw new CoachToolException(
                CoachToolFailureKind.BudgetExhausted,
                toolName,
                $"This turn has used its {_limit} tool calls. Answer with what you already read, " +
                "or ask the learner what to look at next.");
        }
    }

    /// <summary>
    /// Wraps every function in <paramref name="tools"/> so its invocations count against a fresh
    /// budget, and returns that budget alongside the wrapped set.
    /// </summary>
    /// <remarks>
    /// Applied by the coach at the point it takes a tool set for a turn, rather than inside the
    /// factory. Two reasons. The cap is a property of a turn, and the turn is what the coach owns.
    /// And a test that substitutes its own factory is capped on the same terms as production
    /// instead of quietly opting out of the guarantee.
    /// </remarks>
    public static (IReadOnlyList<AIFunction> Tools, CoachToolCallBudget Budget) Apply(
        IReadOnlyList<AIFunction> tools,
        int limit = MaxCallsPerTurn)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var budget = new CoachToolCallBudget(limit);
        var wrapped = new List<AIFunction>(tools.Count);
        foreach (var tool in tools)
            wrapped.Add(new BudgetedAIFunction(tool, budget));

        return (wrapped, budget);
    }
}

/// <summary>
/// Wraps a tool so every invocation is counted against the turn's <see cref="CoachToolCallBudget"/>.
/// </summary>
/// <remarks>
/// A delegating wrapper rather than a check inside each tool body, because the guarantee should not
/// depend on nine authors remembering it. The coach wraps whatever set it is handed, so a tool added
/// later is capped by construction and there is no version of "forgot to add the counter" that
/// compiles into a shipped build.
/// </remarks>
public sealed class BudgetedAIFunction : DelegatingAIFunction
{
    private readonly CoachToolCallBudget _budget;

    public BudgetedAIFunction(AIFunction inner, CoachToolCallBudget budget)
        : base(inner)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    /// <summary>The budget this function is counted against.</summary>
    public CoachToolCallBudget Budget => _budget;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Before the inner call, so an over-budget invocation issues no query.
        _budget.Consume(Name);
        return base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
