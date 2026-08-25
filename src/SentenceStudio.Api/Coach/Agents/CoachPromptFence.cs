using System.Security.Cryptography;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// The delimiter pair that separates untrusted learner content from developer-authored prompt
/// text for one turn.
/// </summary>
/// <remarks>
/// <para>
/// A fixed delimiter such as <c>&lt;&lt;&lt;</c>/<c>&gt;&gt;&gt;</c> is not a boundary — it is a
/// string the untrusted side can also type. A learner who writes the closing token and then
/// their own directives closes the data block early, and everything after it reads to the model
/// as prompt rather than as content. That is fence breakout, and it needs no model weakness: the
/// text really is outside the block by the time the model sees it.
/// </para>
/// <para>
/// The fix is the standard one: draw the delimiter per turn from a cryptographic RNG. Learner
/// content cannot reproduce a value it has never seen, so there is no string a learner can type
/// that ends the block. The delimiter is also stated in the turn preamble, so the model is told
/// which line is authoritative rather than having to infer it.
/// </para>
/// <para>
/// This changes the delimiter only. Block labels, ordering, role tags, and the surrounding
/// developer text are untouched, so the prompt shape the coach was evaluated against is
/// preserved, and the content inside the block remains untrusted regardless — tool access stays
/// allow-listed and the response stays schema-constrained.
/// </para>
/// </remarks>
/// <param name="Open">The line that opens a data block.</param>
/// <param name="Close">The line that closes a data block.</param>
public readonly record struct CoachPromptFence(string Open, string Close)
{
    /// <summary>The stable prefix, so a reader can recognise the line as a delimiter.</summary>
    public const string OpenPrefix = "<<<COACH-DATA-";

    /// <summary>The stable prefix of the closing line.</summary>
    public const string ClosePrefix = ">>>COACH-DATA-";

    /// <summary>Bytes of randomness in the per-turn token.</summary>
    private const int TokenBytes = 12;

    /// <summary>
    /// Builds a fence whose token appears nowhere in the content it will wrap.
    /// </summary>
    /// <remarks>
    /// The collision check is a belt-and-braces measure, not the security property: a 96-bit
    /// random token colliding with learner text is not a reachable event. It exists so that the
    /// invariant "the token does not occur inside the block" is enforced by code rather than by
    /// an argument about probabilities, which is the kind of argument that stops holding when
    /// somebody later shortens the token.
    /// </remarks>
    /// <param name="learnerText">The learner's message for this turn.</param>
    /// <param name="priorMessages">Any replayed ledger messages included in this turn.</param>
    public static CoachPromptFence Create(
        string? learnerText,
        IReadOnlyList<CoachPriorMessage>? priorMessages = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fence = CreateCandidate();

            if (attempt >= 4 || !Collides(fence, learnerText, priorMessages))
            {
                return fence;
            }
        }
    }

    private static CoachPromptFence CreateCandidate()
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));
        return new CoachPromptFence(OpenPrefix + token, ClosePrefix + token);
    }

    private static bool Collides(
        CoachPromptFence fence,
        string? learnerText,
        IReadOnlyList<CoachPriorMessage>? priorMessages)
    {
        if (Contains(learnerText))
        {
            return true;
        }

        if (priorMessages is null)
        {
            return false;
        }

        for (var i = 0; i < priorMessages.Count; i++)
        {
            if (Contains(priorMessages[i].Text))
            {
                return true;
            }
        }

        return false;

        bool Contains(string? text) =>
            !string.IsNullOrEmpty(text)
            && (text.Contains(fence.Open, StringComparison.Ordinal)
                || text.Contains(fence.Close, StringComparison.Ordinal));
    }
}
