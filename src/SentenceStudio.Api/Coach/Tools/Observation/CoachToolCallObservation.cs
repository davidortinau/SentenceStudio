namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// One completed tool call, described in facts a trace may keep.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one record rather than two collectors.</b> W3 projects learner-visible evidence from a
/// turn's scopes; W4 projects a content-free trace from the same calls. Both need capture at the
/// same seam, and two capture points would mean two DTOs, two edits to
/// <c>CoachToolFactory</c>, and two answers to "what did this turn actually read". The
/// <see cref="Scope"/> member is the single decision that prevents that: one capture, two
/// projections.
/// </para>
/// <para>
/// <b>What may be on this record.</b> A closed code, a bounded identifier, a count, a duration, or
/// an in-memory object that is documented never to cross the persistence boundary. Never an
/// argument value, never learner text, never an exception object, and never a model-supplied
/// string.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> There is no conversation id, no turn id and no learner id.
/// The buffer that holds these is scoped to one turn, so the turn is the context; stamping the ids
/// onto every observation would put three identifiers into a structure whose whole claim is that it
/// holds none.
/// </para>
/// </remarks>
/// <param name="ToolName">
/// The registered tool name, taken from <c>CoachToolRegistration</c> at build time. Never a string
/// the model supplied — a model that invents a name cannot widen this member.
/// </param>
/// <param name="Ordinal">
/// Position within the turn, 1-based. One-based rather than zero-based because the value is read by
/// humans in a trace and by the model in a summary, and "the first tool call" reading as call 0 is
/// a footnote nobody remembers.
/// </param>
/// <param name="Outcome">How the call ended.</param>
/// <param name="FailureKind">
/// The typed refusal kind when <paramref name="Outcome"/> is
/// <see cref="CoachToolCallOutcome.Refused"/>; null otherwise. A fault carries no kind, because the
/// exception that produced it never reaches this seam.
/// </param>
/// <param name="ArgumentMask">Which arguments were present. Presence only.</param>
/// <param name="ElapsedMs">
/// Wall time for the inner call, in whole milliseconds. Measured around the delegate only, so it
/// excludes the observers' own work — a slow subscriber must not be able to make a tool look slow.
/// </param>
/// <param name="Scope">
/// The scope the read stated, when it stated one. <b>In-memory only.</b> It rides the observation so
/// W3 and W4 can each project from it; the object itself is never serialized from here.
/// </param>
/// <param name="SubjectCode">
/// A closed-set capability code for the call's subject, when the tool has one. Populated only for
/// preference-change proposals today, where "which setting" is the entire signal that decides
/// whether the empty allow-list should gain an entry.
/// </param>
public sealed record CoachToolCallObservation(
    string ToolName,
    int Ordinal,
    CoachToolCallOutcome Outcome,
    CoachToolFailureKind? FailureKind,
    CoachToolArgumentMask ArgumentMask,
    int ElapsedMs,
    CoachResultScope? Scope,
    CoachToolSubjectCode? SubjectCode = null);
