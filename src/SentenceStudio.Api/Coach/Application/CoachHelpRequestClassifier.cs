using System.Text.RegularExpressions;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Server-owned classifier for "what can you do?" questions about Sam itself.
/// </summary>
/// <remarks>
/// <para>
/// Runs before the model, beside <see cref="CoachLatestStudyClassifier"/>. A match is answered
/// from the enabled tool registry by <see cref="CoachHelpCatalog"/>, so the list of actions can
/// never claim a tool this deployment has switched off.
/// </para>
/// <para>
/// Bounded on purpose. The patterns match a question about Sam's abilities, or a message that is
/// only the word "help". "Help me with this sentence" or "can you help me understand -았-" is a
/// language request and goes to the model.
/// </para>
/// </remarks>
public static class CoachHelpRequestClassifier
{
    // Short linear patterns; the timeout is only a ReDoS backstop (see CoachLatestStudyClassifier).
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

    // The whole message is "help", "help?", "help me", "/help", "menu" or "commands".
    private static readonly Regex EnBareHelp = new(
        @"^\s*/?(?:help(?:\s+me)?|menu|commands|options)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    // The whole message is a question about Sam's abilities, with an optional greeting before
    // it and a short tail after it: "what can you do?", "hey Sam, what else can you help me
    // with?", "tell me what you can do", "what are you able to do", "how can you help me?",
    // "what are your features?", "what can I ask you?". Anchored to the whole message so
    // "how do I say 'what can you do' in Korean" stays a language question.
    private static readonly Regex EnCapabilityQuestion = new(
        @"^\s*(?:(?:hey|hi|hello|ok|okay|so|sam|please)[\s,]+)*" +
        @"(?:" +
        @"(?:so\s+)?what\s+(?:else\s+|all\s+|things\s+|kinds?\s+of\s+things\s+)?(?:can|could)\s+(?:you|sam)\s+(?:do|help(?:\s+(?:me|us))?(?:\s+with)?)" +
        @"|(?:can\s+you\s+)?(?:tell|show)\s+me\s+what\s+(?:you|sam)\s+(?:can|could)\s+(?:do|help(?:\s+(?:me|us))?(?:\s+with)?)" +
        @"|what\s+(?:are|is)\s+(?:you|sam)\s+able\s+to\s+do" +
        @"|how\s+(?:can|could)\s+(?:you|sam)\s+help(?:\s+(?:me|us))?" +
        @"|what\s+(?:do|does)\s+(?:you|sam)\s+(?:do|know\s+how\s+to\s+do)" +
        @"|what\s+are\s+(?:your|sam'?s)\s+(?:capabilities|features|abilities|skills|commands)" +
        @"|what\s+can\s+I\s+(?:ask|tell)\s+(?:you|sam)(?:\s+to\s+do)?" +
        @")" +
        @"(?:[\s,]+(?:for\s+me|here|in\s+(?:this|the)\s+app|sam|exactly|again|please))*\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    // The whole message is 도움말 / 도움 / 도와줘 / 도와주세요 / 메뉴.
    private static readonly Regex KoBareHelp = new(
        @"^\s*/?(?:도움말|도움|도와\s*줘(?:요)?|도와\s*주세요|메뉴)\s*[.!?]*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    // The whole message asks what Sam can do: "뭘 할 수 있어요?", "쌤은 무엇을 도와줄 수
    // 있어요?", "할 수 있는 게 뭐야?". Anchored so "주말에 뭘 할 수 있어요?" — a sentence the
    // learner wants checked — still reaches the teacher.
    private static readonly Regex KoCapabilityQuestion = new(
        @"^\s*(?:(?:쌤|선생님|너|당신)(?:은|는|이|가)?\s*)?(?:나(?:를|에게|한테)?\s*)?(?:여기서\s*)?" +
        @"(?:" +
        @"(?:뭘|뭐|무엇을?|무얼|어떤\s*(?:것|거|걸|일)을?)\s*(?:더\s*)?(?:할|해\s*줄|도와\s*줄|도와\s*드릴|도울)\s*수\s*있\S*" +
        @"|(?:할|해\s*줄|도와\s*줄|도울)\s*수\s*있는\s*(?:게|것|거|일|기능)(?:은|이|들은|들이)?\s*(?:뭐|무엇|뭔)\S*" +
        @")" +
        @"\s*[.!?]*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>True when the learner is asking what Sam can do.</summary>
    public static bool IsHelpRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // A capability question is short. A long message that happens to contain "what can you
        // do" is a real request with more in it, and the model should read all of it.
        if (text.Length > 120)
        {
            return false;
        }

        try
        {
            return EnBareHelp.IsMatch(text)
                || EnCapabilityQuestion.IsMatch(text)
                || KoBareHelp.IsMatch(text)
                || KoCapabilityQuestion.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // A timeout falls through to the model, which can answer the question itself.
            return false;
        }
    }
}
