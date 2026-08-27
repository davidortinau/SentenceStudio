using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// Which of the coach's durable messages a learner may report as unsatisfactory.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the client and the server on purpose. The client decides whether to draw the flag and
/// the server decides whether to accept the report, and when those two rules were written twice
/// they disagreed: the transcript withheld the control on a notice that the server would happily
/// have accepted, which is how <i>"There is no plan for today yet"</i> — the response a learner was
/// most likely to want to complain about — ended up as the one message on screen with no way to
/// complain about it.
/// </para>
/// <para>
/// The rule is about <b>who is speaking</b>, not about whether the answer was any good. Everything
/// the coach says to the learner in their own words can be reported, including a notice: a notice
/// that answers a learner's request is an answer, however short, and refusing feedback on it means
/// refusing feedback exactly where the product failed.
/// </para>
/// <para>
/// A receipt is the exception. It is not the coach talking; it is the record of a change that was
/// applied to the learner's own data, and a quarrel with it is a quarrel with the change. That
/// belongs to the plan surface, which can undo it, rather than to a review queue that cannot.
/// </para>
/// <para>
/// This is a rule about <i>kind</i> alone, and it is not the whole gate. The server additionally
/// refuses any response it cannot correlate to the learner request it answered, which is what
/// keeps the coach's own internal notices — the ones written outside a learner's turn — out of the
/// queue without this predicate needing to know they exist. The client applies one further
/// refinement the server does not: it withholds the flag on a notice whose reason code marks the
/// turn as having changed nothing, because a stopped turn reads as bookkeeping rather than as an
/// answer. That is an affordance choice, not a refusal — a report that arrives against one is
/// still owned, still paired, and still worth reading.
/// </para>
/// </remarks>
public static class CoachResponseReportability
{
    /// <summary>True when a message of this kind is one the coach said and the learner may report.</summary>
    /// <remarks>
    /// Written as an explicit switch over every member rather than a negation, so adding a kind to
    /// <see cref="CoachMessageKind"/> is a compile-time decision about whether learners can report
    /// it rather than a silent yes.
    /// </remarks>
    public static bool IsReportableKind(CoachMessageKind kind) => kind switch
    {
        CoachMessageKind.Text => true,
        CoachMessageKind.Clarification => true,
        CoachMessageKind.Suggestion => true,
        CoachMessageKind.Notice => true,
        CoachMessageKind.PedagogicalAnswer => true,
        CoachMessageKind.Receipt => false,

        // Produced only by a client's tolerant wire converter, never by the server. Nothing is
        // known about what it was, so there is nothing to review and no queue entry worth making.
        CoachMessageKind.Unrecognized => false,
        _ => false
    };
}

/// <summary>
/// Why a learner reported one of the coach's responses.
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed set, and deliberately no free-text sibling.</b> A "tell us more" box is the one
/// place a learner would paste the material the whole coach surface is built to keep out of
/// server-side product telemetry — their own sentence, somebody's name, a diary line. The five
/// members below are the shapes of dissatisfaction the product can actually act on, and anything
/// finer is a conversation to have with the learner, not a column.
/// </para>
/// <para>
/// Stored as an <b>ordinal</b> in <c>CoachResponseReport</c>, so member order is a persistence
/// contract: members may only be appended. Serialized <b>by name</b> on the wire, because this
/// host has no global string-enum converter and the value arrives in a learner's request body —
/// without the attribute the request fails to bind before any handler runs.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachResponseReportReason.Other), WireEnumFallbackKind.NeutralMember,
    "Other is \u201cnone of the above\u201d, which is what an unnameable reason is. DidNotAnswer \u2014 "
    + "the zero value \u2014 would attribute a specific complaint to a learner who did not make it.")]
public enum CoachResponseReportReason
{
    /// <summary>The response did not answer what was asked.</summary>
    DidNotAnswer = 0,

    /// <summary>The response stated something wrong or misleading.</summary>
    IncorrectOrMisleading = 1,

    /// <summary>The learner expected the app to do something, and it only talked about it.</summary>
    ExpectedAppAction = 2,

    /// <summary>The response was hard to follow.</summary>
    Confusing = 3,

    /// <summary>None of the above.</summary>
    Other = 4
}

/// <summary>
/// What happened to a report.
/// </summary>
/// <remarks>
/// <see cref="AlreadyReported"/> is a success, not a failure: reporting the same response twice
/// is a normal thing for a learner to do across two devices or after a reload, and the second
/// attempt must leave them exactly where the first one did rather than showing an error for
/// something that already worked. Serialized by name for the same reason the reason enum is.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachResponseReportState.AlreadyReported), WireEnumFallbackKind.NeutralMember,
    "AlreadyReported is the no-op reading: it tells the client the report exists and suppresses a second "
    + "submit. Recorded \u2014 the zero value \u2014 would claim this request wrote something, which is "
    + "precisely the fact the client cannot verify.")]
public enum CoachResponseReportState
{
    /// <summary>This request recorded the report.</summary>
    Recorded = 0,

    /// <summary>A report for this response already existed. Nothing changed.</summary>
    AlreadyReported = 1
}

/// <summary>
/// A learner's report of one coach response.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reason is the whole request.</b> There is no text member and no place to add one: the
/// server reads the exchange out of its own encrypted ledger, so nothing a learner typed is ever
/// re-sent, re-serialized, or re-logged in order to report it.
/// </para>
/// <para>
/// <b>The learner's own request is deliberately not named here.</b> The server derives it from
/// the response's turn correlation, which the ledger stamped at append time — a fact the client
/// cannot forge and, after a history reload renumbers the transcript, cannot reliably reconstruct
/// either. Asking the client to assert the pairing would have made a correct report refusable
/// after a reload, and an incorrect one accepted before it.
/// </para>
/// </remarks>
public sealed class CoachResponseReportRequest
{
    /// <summary>Why the learner is reporting the response.</summary>
    public required CoachResponseReportReason Reason { get; init; }
}

/// <summary>
/// The server's answer to a report.
/// </summary>
/// <remarks>
/// Carries no ledger identity, no fingerprint, and no operator state. A learner reporting a
/// response is entitled to know it was received; everything downstream of that is a reviewer's
/// business and nothing the client needs in order to render "Reported for review".
/// </remarks>
public sealed class CoachResponseReportResponse
{
    /// <summary>The reported response's message identifier.</summary>
    public required string MessageId { get; init; }

    /// <summary>The reason the report was recorded under. On a repeat, the reason that won.</summary>
    public required CoachResponseReportReason Reason { get; init; }

    /// <summary>Whether this request recorded the report or found one already there.</summary>
    public required CoachResponseReportState State { get; init; }

    /// <summary>When the report was first recorded.</summary>
    public required DateTime ReportedAtUtc { get; init; }
}

/// <summary>
/// Which of a conversation's coach responses the learner has already reported.
/// </summary>
/// <remarks>
/// <para>
/// Read on entry and after a resume, so the flag control renders as "Reported for review" on the
/// exact responses it did before the reload. The client cannot derive this: the report lives on
/// the server, and a browser that forgot everything must still be told the truth about what this
/// learner already did.
/// </para>
/// <para>
/// Owner-scoped by construction — the route derives the learner and the response never names one
/// — so this can only ever describe the caller's own conversation.
/// </para>
/// </remarks>
public sealed class CoachReportedResponsesDto
{
    /// <summary>The identifiers of the coach responses this learner has reported.</summary>
    public IReadOnlyList<string> MessageIds { get; init; } = Array.Empty<string>();
}
