namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// Which boundary a shape sits on, and therefore which embargo applies to it.
/// </summary>
/// <remarks>
/// <para>
/// The coach has two boundaries, and they are not the same boundary. One faces the model: tool
/// answers, the typed intent it produces, and the context assembled for a run. The other faces the
/// authenticated owner over HTTPS: the conversation list, the message page, the export.
/// </para>
/// <para>
/// Applying the model-facing rules to the client-facing shapes was a real defect and not a
/// harmless over-caution. It made the identifier a REST resource is addressed by — the one field a
/// conversation API cannot exist without — indistinguishable from a leak, and the only ways out
/// were to rename a correct public field to something evasive or to keep a hand-maintained list of
/// exceptions. Both hide the boundary rather than describe it.
/// </para>
/// <para>
/// So the scope is named instead. A shape declares which side it is on, the scanner applies the
/// rules for that side, and a shape that tries to cross gets caught by the structural tests rather
/// than by a word list.
/// </para>
/// </remarks>
public enum CoachEmbargoScope
{
    /// <summary>
    /// The model can see it or produce it: a tool answer, the typed intent, the turn context.
    /// </summary>
    /// <remarks>
    /// The strict scope. Learner content is refused outright here, because the coach's whole
    /// safety argument is that it plans study time without being shown the material it is planning
    /// about. This is the default, so a new shape is treated as model-visible until someone
    /// deliberately says otherwise.
    /// </remarks>
    ModelVisible = 0,

    /// <summary>
    /// The server sends it to the authenticated owner and the model never sees it.
    /// </summary>
    /// <remarks>
    /// The bounded scope. Learner content is the payload rather than a leak — a history API returns
    /// the learner their own conversation — so the content rule does not apply. Identity,
    /// directives, and credentials still do, and the storage layer's own machinery is added:
    /// ciphertext, nonces, key ids, leases, and idempotency digests are not client-facing metadata
    /// and must not ride out on a public contract.
    /// </remarks>
    PublicClient = 1,

    /// <summary>
    /// A tool result carrying explicit learner-requested content the model may see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This scope sits between ModelVisible and PublicClient. It permits learner content words
    /// (term, example, sentence) that would be refused in ModelVisible, because the tool's typed
    /// envelope declares that the content is an explicit answer to a learner query — not a
    /// smuggled leak. The tool result is authenticated server-side from
    /// <see cref="Tools.IUserScopeProvider"/>, so the model never receives another tenant's data.
    /// </para>
    /// <para>
    /// Identity, directives, credentials, entities, and open types are still refused. Bulk content
    /// (transcripts, diaries, conversations, memories, due words) remains embargoed: the learner
    /// asked for a word detail, not for their entire learning history.
    /// </para>
    /// </remarks>
    ToolResult = 2,

    /// <summary>
    /// A result-scope envelope: metadata about how a read answered, never about what it found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strictest scope of the four, and the only one that constrains member <em>types</em>
    /// rather than only member names. A scope shape exists to describe coverage, order, filters,
    /// and counts. None of those need a string, and a string is the one member type through which
    /// a target term, a gloss, an example sentence, a fragment of transcript, a note the learner
    /// wrote, or an echo of the model's own query could travel.
    /// </para>
    /// <para>
    /// So the rule is an allow-list rather than a word list: booleans, whole numbers, dates, and
    /// closed enums may appear on a scope, and nothing else may. A word list would only refuse the
    /// leaks somebody thought to name; the allow-list refuses the mechanism, which is why a future
    /// <c>MatchedTermPreview</c> cannot be added here by anyone in a hurry.
    /// </para>
    /// </remarks>
    ResultScope = 3
}
