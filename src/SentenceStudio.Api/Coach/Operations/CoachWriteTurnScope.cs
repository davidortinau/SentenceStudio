namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// Carries the conversation and turn a write proposal belongs to, for the length of one request.
/// </summary>
/// <remarks>
/// <para>
/// A proposal is bound to a conversation, and the binding is load-bearing: it is what stops a
/// confirmation minted in one conversation from settling an operation in another. The tool the
/// model calls has no way to know which conversation it is running inside — tools are resolved
/// from the request scope and receive only their own arguments — so the conversation has to
/// arrive some other way.
/// </para>
/// <para>
/// It arrives here, and only from the turn pipeline. The alternative would be an argument on
/// every write tool, which would mean the model naming the conversation its proposal binds to.
/// That is precisely the value that must not be model-supplied.
/// </para>
/// <para>
/// Unset is a refusal, not a default. A write tool that runs outside a turn has no conversation to
/// bind to, and binding it to a placeholder would produce an operation no confirmation could ever
/// match — or worse, one that any confirmation could.
/// </para>
/// </remarks>
public sealed class CoachWriteTurnScope
{
    /// <summary>Marks a turn identity the server minted because the client sent none.</summary>
    public const string ServerTurnPrefix = "srv-";

    /// <summary>Marks the turn identity a reversal row carries instead of the original's.</summary>
    /// <remarks>
    /// A reversal is not a second proposal in the turn it reverses, so it takes a derived identity
    /// of its own. That identity is bookkeeping the ledger issues; it is never a value a request
    /// may supply.
    /// </remarks>
    public const string UndoTurnPrefix = "undo:";

    /// <summary>
    /// True when a turn identity is one the server issues and a client may not claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both prefixes name rows the ledger writes for itself, and a turn identity is now unique per
    /// conversation, so a client that supplied one would be taking a slot the server needs. The
    /// <c>undo:</c> case is the one with teeth: a request naming <c>undo:{operationId}</c> would
    /// occupy exactly the identity that operation's reversal will later need, and the learner's
    /// Undo would then fail for as long as the row existed — a change made unreversible by
    /// somebody else's request.
    /// </para>
    /// <para>
    /// Ordinal and case-insensitive. The comparison decides whether a value is reserved, and a
    /// reserved value spelled <c>UNDO:</c> is the same claim on the same slot.
    /// </para>
    /// </remarks>
    public static bool IsReservedTurnId(string? turnId) =>
        turnId is not null
        && (turnId.StartsWith(ServerTurnPrefix, StringComparison.OrdinalIgnoreCase)
            || turnId.StartsWith(UndoTurnPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The conversation this request belongs to, or null outside a turn.</summary>
    public string? ConversationId { get; private set; }

    /// <summary>The turn this request belongs to. Never null once a conversation is known.</summary>
    public string? TurnId { get; private set; }

    /// <summary>True when a conversation is known.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(ConversationId);

    /// <summary>
    /// Records the conversation and turn for this request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once by the turn pipeline. A blank conversation leaves the scope inactive rather
    /// than storing an empty string, so <see cref="IsActive"/> answers the same question either
    /// way.
    /// </para>
    /// <para>
    /// A blank turn identity is different: it is filled in, not left empty. The per-turn write
    /// budget counts proposals by turn, so a turn with no identity is a turn with no cap, and a
    /// client that simply omits the field would be choosing the weaker of the two bounds for
    /// itself. The server mints one instead — one value for the whole request scope, so it bounds
    /// exactly the same set of calls a client-supplied identity would, and prefixed so an
    /// operator reading an audit can tell a turn the client named from one the server did.
    /// </para>
    /// <para>
    /// The minted value is not an idempotency key and is never used as one. The client's identity
    /// still drives turn replay upstream; this only gives the write budget something to count
    /// against.
    /// </para>
    /// <para>
    /// A reserved identity is treated exactly like a blank one: replaced, not honoured. The route
    /// refuses such a request outright and is the answer a caller sees, so this is defence in
    /// depth for any path that reaches the scope without passing that check — and it fails towards
    /// a value the server chose, which is the safe direction. Honouring it would let a request
    /// occupy a slot the ledger issues to itself.
    /// </para>
    /// </remarks>
    public void Enter(string? conversationId, string? turnId = null)
    {
        ConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId;

        if (ConversationId is null)
        {
            TurnId = string.IsNullOrWhiteSpace(turnId) || IsReservedTurnId(turnId) ? null : turnId;
            return;
        }

        TurnId = string.IsNullOrWhiteSpace(turnId) || IsReservedTurnId(turnId)
            ? ServerTurnPrefix + Guid.NewGuid().ToString("N")
            : turnId;
    }
}
