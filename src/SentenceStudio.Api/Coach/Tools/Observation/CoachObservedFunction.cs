using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;

namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Wraps one registered tool and reports what the call did to every observer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Applied in <c>CoachToolFactory</c>, not in the two agent arms.</b> Both
/// <c>BaselineLearningCoach</c> and <c>HarnessLearningCoach</c> call
/// <c>CoachToolCallBudget.Apply(_toolFactory.CreateTools())</c>, so wrapping inside the factory
/// puts this seam <em>inside</em> the budget wrapper. That ordering matters twice over. A budget
/// refusal is raised by <c>BudgetedAIFunction.Consume</c> before the inner delegate runs, so the
/// seam never sees the call and produces <b>zero</b> observations for it — the refusal is counted
/// once at the turn boundary from <c>CoachToolCallBudget.Used</c> rather than being double-counted
/// here as a tool failure. And any future agent arm is covered by construction instead of by
/// somebody remembering to wrap it.
/// </para>
/// <para>
/// <b>One seam, many subscribers.</b> This replaces the single-purpose wrapper that fanned into the
/// opportunity ledger alone. A second sibling wrapper would have made subscriber ordering implicit
/// and untestable, and would have needed a second edit to <c>CoachToolFactory</c> — the contested
/// file this arrangement exists to keep to one owner and one edit.
/// </para>
/// <para>
/// <b>Every surface member is forwarded verbatim</b> by <see cref="DelegatingAIFunction"/>, so the
/// allow-list contract sees exactly the tool set it saw before. A wrapper that renamed anything
/// would fail <c>CoachToolAllowList.Validate</c> as an unknown tool.
/// </para>
/// <para>
/// <b>The result is never changed and the exception is always rethrown.</b> Observation must not
/// alter what the model is told. Each observer is guarded independently — the interface says it
/// never throws, but the contract is one anyone can implement, and the cost of trusting it is that
/// a bounded, actionable tool refusal is replaced by an unrelated failure the learner then reads.
/// </para>
/// </remarks>
public sealed class CoachObservedFunction : DelegatingAIFunction
{
    private readonly CoachToolRegistration _registration;
    private readonly IReadOnlyList<ICoachToolCallObserver> _observers;
    private readonly CoachToolCallSequence _sequence;

    public CoachObservedFunction(
        AIFunction inner,
        CoachToolRegistration registration,
        IReadOnlyList<ICoachToolCallObserver> observers,
        CoachToolCallSequence sequence)
        : base(inner)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _observers = observers ?? throw new ArgumentNullException(nameof(observers));
        _sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
    }

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Read before the call. The arguments object is the harness's and nothing here promises it
        // is unchanged afterwards, and the mask is a fact about what was asked for rather than
        // about what came back.
        var mask = CoachToolArgumentMaskReader.Read(arguments);
        var subject = ReadSubjectCode(arguments);

        // Taken before the ordinal, so a call that faults still holds a position in the turn. An
        // ordinal issued only on success would renumber the turn around its failures.
        var ordinal = _sequence.Next();

        // Installed before the call so the serializer's capture converter has somewhere to put the
        // scope. AIFunctionFactory marshals the result to a JsonElement on the way out, so by the
        // time this method holds the result the envelope that stated the scope is gone.
        var capture = CoachToolScopeCapture.Begin();
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);

            await PublishAsync(
                    Observation(
                        ordinal, CoachToolCallOutcome.Succeeded, failureKind: null,
                        mask, started, capture.Scope, subject),
                    cancellationToken)
                .ConfigureAwait(false);

            return result;
        }
        catch (CoachToolException ex)
        {
            await PublishAsync(
                    Observation(
                        ordinal, CoachToolCallOutcome.Refused, ex.Kind,
                        mask, started, scope: null, subject),
                    cancellationToken)
                .ConfigureAwait(false);

            throw;
        }
        catch
        {
            // The exception object never reaches an observer, a log, or the record — only the fact
            // that the call faulted. An untyped throw from a tool is the case most likely to carry
            // a provider message with learner text echoed back inside it.
            await PublishAsync(
                    Observation(
                        ordinal, CoachToolCallOutcome.Faulted, failureKind: null,
                        mask, started, scope: null, subject),
                    cancellationToken)
                .ConfigureAwait(false);

            throw;
        }
        finally
        {
            CoachToolScopeCapture.End();
        }
    }

    private CoachToolCallObservation Observation(
        int ordinal,
        CoachToolCallOutcome outcome,
        CoachToolFailureKind? failureKind,
        CoachToolArgumentMask mask,
        long startedTimestamp,
        CoachResultScope? scope,
        CoachToolSubjectCode? subject) =>
        new(
            // The registration held at build time, never anything the model supplied. A model that
            // invents a name cannot widen this member.
            _registration.Name,
            ordinal,
            outcome,
            failureKind,
            mask,
            ElapsedMs(startedTimestamp),
            scope,
            subject);

    /// <summary>
    /// Measured around the inner delegate only, so a slow observer cannot make a tool look slow.
    /// </summary>
    private static int ElapsedMs(long startedTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

        // Clamped rather than cast. A negative value is impossible from a monotonic clock but an
        // int that overflowed would be, and a negative duration in a trace is worse than a wrong
        // one because it reads as a defect in whatever consumed it.
        return elapsed <= 0 ? 0 : elapsed >= int.MaxValue ? int.MaxValue : (int)elapsed;
    }

    /// <summary>
    /// Publishes to every observer, guarding each one separately.
    /// </summary>
    /// <remarks>
    /// Separately, so one bad subscriber cannot silence the others: a single try around the loop
    /// would let observer 1 throwing hide the call from observer 2, which is how a trace ends up
    /// missing exactly the turns something went wrong in.
    /// </remarks>
    private async ValueTask PublishAsync(
        CoachToolCallObservation observation, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _observers.Count; i++)
        {
            try
            {
                await _observers[i].OnCompletedAsync(observation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception observerFailure)
            {
                // Every exception, including OperationCanceledException. The tool call's own
                // cancellation is already carried by whatever this method was called alongside, so
                // letting an observer's cancellation escape would replace an actionable result or
                // refusal with an unrelated cancellation.
                //
                // Discarded rather than logged: an observer owns its own content-free failure
                // logging, and logging an exception object here is exactly what the coach logging
                // rule forbids.
                _ = observerFailure;
            }
        }
    }

    /// <summary>
    /// Collapses a preference-change call's setting name to a closed-set code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place a model-supplied value is read, and it is collapsed immediately: the raw
    /// string never leaves this method and never reaches a column, a log line, or the observation.
    /// <see cref="CoachToolSubjectCode"/> can only hold a member of the closed set or the unknown
    /// bucket.
    /// </para>
    /// <para>
    /// Without it the ledger could say "somebody asked to change a setting" but not <em>which</em>,
    /// and "learners keep asking for session_minutes" is the entire signal that decides whether the
    /// empty allow-list should gain an entry.
    /// </para>
    /// </remarks>
    private CoachToolSubjectCode? ReadSubjectCode(AIFunctionArguments? arguments)
    {
        if (!string.Equals(_registration.Name, CoachToolNames.ProposePreferenceChange, StringComparison.Ordinal))
        {
            return null;
        }

        if (arguments is null)
        {
            return CoachToolSubjectCode.ForPreferenceSetting(null);
        }

        foreach (var (key, value) in arguments)
        {
            // The write tools take a single typed argument object; its member is named "setting".
            // Matched case-insensitively because the serializer's naming policy is a detail this
            // reader should not depend on.
            if (!string.Equals(key, "arguments", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "setting", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = ExtractSetting(value, key);
            if (candidate is not null)
            {
                return CoachToolSubjectCode.ForPreferenceSetting(candidate);
            }
        }

        return CoachToolSubjectCode.ForPreferenceSetting(null);
    }

    private static string? ExtractSetting(object? value, string key)
    {
        switch (value)
        {
            case null:
                return null;

            case CoachPreferenceChangeArgs typed:
                return typed.Setting;

            case string text when string.Equals(key, "setting", StringComparison.OrdinalIgnoreCase):
                return text;

            case JsonElement { ValueKind: JsonValueKind.String } stringElement
                when string.Equals(key, "setting", StringComparison.OrdinalIgnoreCase):
                return stringElement.GetString();

            case JsonElement { ValueKind: JsonValueKind.Object } objectElement:
                return objectElement.TryGetProperty("setting", out var property)
                       && property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;

            default:
                return null;
        }
    }
}
