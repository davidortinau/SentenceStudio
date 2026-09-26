using System.Globalization;
using System.Text;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Decides whether a free-text turn explicitly authorizes routing to Today's Plan and, when it
/// does, whether it is allowed to change the plan on its own.
/// </summary>
/// <remarks>
/// <para>
/// A plan-shaped model result is never authority to route a learner request to Today's Plan.
/// The learner's own text must explicitly name Today's Plan and request a supported plan outcome.
/// Once the coach also answers language questions, "the learner typed something and the model
/// called it a direct change" is not enough. A message like
/// <c>"What's the difference between 좋아하다 and 좋다? Also make today shorter."</c> is a
/// question with a request attached, and the plan half of it must be offered, not applied.
/// </para>
/// <para>
/// So a typed direct write now needs a plan command that governs the explicit plan reference: no
/// quoted material, no target-language lexical question, and no second independent request. A
/// bounded purpose clause may explain the command without becoming a second activity request.
/// Anything short of that is downgraded to a suggestion the learner explicitly accepts, or to a
/// clarification. Buttons and structured constraint actions are unaffected — a tap is already
/// unambiguous.
/// </para>
/// <para>
/// This is a one-way gate: it can only ever <b>reduce</b> authority. It never turns a
/// suggestion into a write.
/// </para>
/// </remarks>
public sealed class CoachWriteAuthority
{
    /// <summary>Longest message that can still read as a bare plan command.</summary>
    public const int MaxCommandLength = 160;

    private const string KoreanMinuteBridgeSuffix = "분으로";

    private static readonly int MaxAvailableMinutesDigitCount =
        DecimalDigitCount(CoachConstraintLimits.MaxAvailableMinutes);

    /// <summary>
    /// Words that mean the learner is asking about the language rather than instructing the
    /// planner. Shared with the acceptance classifier so the two decisions read the same
    /// vocabulary.
    /// </summary>
    private static IReadOnlyList<string> QuestionMarkers => CoachQuestionMarkers.Words;

    /// <summary>
    /// Connectors that introduce a second, different request. "Make it 10 minutes and no audio"
    /// is one command; "make it 10 minutes and what does this mean" is not, and that is caught by
    /// the question markers rather than here.
    /// </summary>
    private static readonly string[] SecondRequestMarkers =
    [
        "also", "additionally", "by the way", "btw", "and also", "then tell", "then explain",
        "그리고 또", "또한", "그런데", "참고로"
    ];

    private static readonly string[] EnglishPlanReferences =
    [
        "today s plan", "todays plan", "today plan", "plan for today", "plan today",
        "today s study plan", "today s practice plan",
        "my plan", "my study plan", "my practice plan"
    ];

    private static readonly string[] EnglishPlanActionMarkers =
    [
        "change", "adjust", "update", "revise", "replace", "remove", "clear", "set", "make",
        "shorten", "lengthen", "cut", "extend", "limit", "focus", "add", "include", "put",
        "suggest", "recommend", "propose", "preview",
        "changing", "adjusting", "updating", "revising", "replacing", "removing", "clearing",
        "setting", "making", "shortening", "lengthening", "cutting", "extending", "limiting",
        "focusing", "adding", "including", "putting"
    ];

    private static readonly string[] SuggestionActionMarkers =
    [
        "suggest", "recommend", "propose", "preview"
    ];

    private static readonly string[] InformationalPlanPrefixes =
    [
        "does ", "do ", "did ", "is ", "are ", "was ", "were ", "has ", "have ",
        "what ", "which ", "when ", "where ", "why ", "how ", "whether ",
        "show me how ", "tell me how ", "explain ", "can i ", "could i ", "should i ",
        "would it ", "what happens if ", "if "
    ];

    private static readonly string[] EnglishNegationMarkers =
    [
        "do not", "don t", "dont", "never", "must not", "should not", "not want",
        "mustn t", "mustnt", "shouldn t", "shouldnt", "wouldn t", "wouldnt",
        "do not want", "don t want", "dont want", "without changing", "without adjusting",
        "without updating", "without revising", "without replacing", "no change to",
        "leave today s plan unchanged", "keep today s plan as is"
    ];

    private static readonly string[] KoreanPlanActionTokens =
    [
        "바꿔", "바꿔줘", "바꿔주세요",
        "변경해줘", "변경해주세요",
        "수정해줘", "수정해주세요",
        "조정해줘", "조정해주세요",
        "줄여줘", "줄여주세요",
        "늘려줘", "늘려주세요",
        "제안해줘", "제안해주세요",
        "추천해줘", "추천해주세요",
        "집중해줘", "집중해주세요",
        "빼줘", "빼주세요",
        "제거해줘", "제거해주세요",
        "추가해줘", "추가해주세요",
        "설정해줘", "설정해주세요"
    ];

    private static readonly string[] KoreanStateSettingActionTokens =
    [
        "해줘", "해주세요"
    ];

    private static readonly string[] KoreanDurationActionTokens =
    [
        "바꿔", "바꿔줘", "바꿔주세요",
        "변경해줘", "변경해주세요",
        "수정해줘", "수정해주세요",
        "조정해줘", "조정해주세요",
        "줄여줘", "줄여주세요",
        "늘려줘", "늘려주세요",
        "설정해줘", "설정해주세요"
    ];

    private static readonly string[] KoreanFocusActionTokens =
    [
        "바꿔", "바꿔줘", "바꿔주세요",
        "집중해줘", "집중해주세요",
        "설정해줘", "설정해주세요"
    ];

    private static readonly string[] KoreanFocusValueNouns =
    [
        "어휘", "단어", "동사", "명사", "형용사", "부사", "문법", "발음", "회화"
    ];

    private static readonly string[][] KoreanNegatedPlanTails =
    [
        ["바꾸지", "마"],
        ["변경하지", "마"],
        ["수정하지", "마"],
        ["조정하지", "마"],
        ["손대지", "마"],
        ["바꾸지", "말아줘"],
        ["변경하지", "말아줘"],
        ["수정하지", "말아줘"],
        ["조정하지", "말아줘"],
        ["바꾸지", "말아주세요"],
        ["변경하지", "말아주세요"],
        ["바꾸지", "말고"],
        ["변경하지", "말고"],
        ["수정하지", "말고"],
        ["조정하지", "말고"],
        ["안", "바꿔도", "돼"],
        ["바꾸고", "싶지", "않아"],
        ["변경하고", "싶지", "않아"],
        ["수정하고", "싶지", "않아"],
        ["바꾸길", "원하지", "않아"],
        ["변경하길", "원하지", "않아"],
        ["그대로"],
        ["바꾸면", "안", "돼"],
        ["바꾸지마"],
        ["변경하지마"],
        ["수정하지마"],
        ["조정하지마"],
        ["손대지마"]
    ];

    private static readonly string[] NonCurrentPlanMarkers =
    [
        "later", "someday", "eventually", "hypothetically", "if ", "i would ", "i could ",
        "it would ", "it could ",
        "might ", "may ", "i plan to ", "나중에", "하려고", "위해", "수 있", "면 "
    ];

    private static readonly string[] ActivityRequestMarkers =
    [
        "start", "launch", "begin", "open", "do", "시작", "열어", "실행"
    ];

    private static readonly string[] ActivityDestinationMarkers =
    [
        "activity", "vocabulary review", "review activity", "practice activity",
        "활동", "어휘 복습", "단어 복습", "연습"
    ];

    private static readonly string[] GeneralStudyMarkers =
    [
        "study", "practice", "learn", "공부", "연습", "학습"
    ];

    private static readonly string[] StudySubjectMarkers =
    [
        "vocabulary", "words", "grammar", "pronunciation", "conversation", "어휘", "단어", "문법", "발음", "회화"
    ];

    /// <summary>The application-owned interpretation of a text turn's plan authority.</summary>
    internal enum PlanRoutingDecision
    {
        /// <summary>The learner did not identify Today's Plan as the destination.</summary>
        NoPlanReference = 0,

        /// <summary>The learner named Today's Plan but did not request a plan outcome.</summary>
        PlanMentionOnly,

        /// <summary>The learner explicitly asked that a plan action not happen.</summary>
        NegatedPlanAction,

        /// <summary>The learner explicitly requested an outcome for Today's Plan.</summary>
        ExplicitPlanRequest
    }

    /// <summary>Why a typed turn may not write.</summary>
    public enum Denial
    {
        /// <summary>The message is a plan command and nothing else.</summary>
        None = 0,

        /// <summary>The message asks something.</summary>
        AsksAQuestion,

        /// <summary>The message quotes text, so part of it is material to discuss.</summary>
        QuotesText,

        /// <summary>The message carries a second, different request.</summary>
        CarriesASecondRequest,

        /// <summary>The message names no plan constraint at all.</summary>
        NamesNoPlanChange,

        /// <summary>The message is long enough that it is prose, not a command.</summary>
        TooLongToBeACommand
    }

    /// <summary>
    /// True when the whole message is a conservative, exclusive plan command and may therefore
    /// apply immediately.
    /// </summary>
    public Denial Evaluate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Denial.NamesNoPlanChange;
        }

        var trimmed = text.Trim();

        if (trimmed.Length > MaxCommandLength)
        {
            return Denial.TooLongToBeACommand;
        }

        // A question mark anywhere means part of the message is a question, whatever else it
        // also says.
        if (trimmed.Contains('?') || trimmed.Contains('？'))
        {
            return Denial.AsksAQuestion;
        }

        if (ContainsQuotation(trimmed))
        {
            return Denial.QuotesText;
        }

        var normalized = Normalize(trimmed);

        foreach (var marker in SecondRequestMarkers)
        {
            if (ContainsPhrase(normalized, Normalize(marker)))
            {
                return Denial.CarriesASecondRequest;
            }
        }

        foreach (var clause in SemanticClauses(trimmed).Where(clause => !clause.IsPurpose))
        {
            foreach (var marker in QuestionMarkers)
            {
                if (ContainsPhrase(clause.Text, Normalize(marker)))
                {
                    return Denial.AsksAQuestion;
                }
            }
        }

        return ClassifyPlanRouting(trimmed) == PlanRoutingDecision.ExplicitPlanRequest
            ? Denial.None
            : Denial.NamesNoPlanChange;
    }

    /// <summary>True when the message may apply a plan change on its own.</summary>
    public bool AllowsDirectWrite(string? text) => Evaluate(text) == Denial.None;

    /// <summary>
    /// Classifies whether the learner's own text permits the model result to enter a plan reducer.
    /// </summary>
    /// <remarks>
    /// This intentionally recognizes only bounded, obvious English and Korean references. Plan
    /// references and mutation outcomes must occur in the same semantic clause; an action in a
    /// neighboring clause cannot lend authority to a plan mention. Unknown wording fails closed to
    /// no plan effect; the replacement runtime owns broader multilingual intent classification.
    /// </remarks>
    internal PlanRoutingDecision ClassifyPlanRouting(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PlanRoutingDecision.NoPlanReference;
        }

        var planClauses = SemanticClauses(text)
            .Where(clause => ContainsPlanReference(clause.Text))
            .ToArray();
        if (planClauses.Length == 0)
        {
            return PlanRoutingDecision.NoPlanReference;
        }

        var currentPlanClauses = planClauses
            .Where(clause => !clause.IsPurpose)
            .ToArray();

        if (currentPlanClauses.Any(HasNegatedPlanAction))
        {
            return PlanRoutingDecision.NegatedPlanAction;
        }

        return currentPlanClauses.Any(HasExplicitPlanAction)
            ? PlanRoutingDecision.ExplicitPlanRequest
            : PlanRoutingDecision.PlanMentionOnly;
    }

    /// <summary>
    /// True when the learner explicitly asks to launch an activity rather than change a plan.
    /// </summary>
    internal bool IsActivityLaunchRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return SemanticClauses(text).Any(clause =>
            !clause.IsPurpose
            && ActivityDestinationMarkers.Any(marker => ContainsPhrase(clause.Text, marker))
            && ActivityRequestMarkers.Any(marker => IsBoundedActivityImperative(clause.Text, marker)));
    }

    /// <summary>
    /// True when a non-question asks generally to study language material without naming a
    /// supported destination.
    /// </summary>
    internal bool IsGeneralStudyRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = Normalize(text);
        return !text.Contains('?')
            && !text.Contains('？')
            && GeneralStudyMarkers.Any(marker => ContainsPhrase(normalized, marker))
            && StudySubjectMarkers.Any(marker => ContainsPhrase(normalized, marker));
    }

    /// <summary>
    /// Quotation marks in the forms a learner is likely to paste.
    /// </summary>
    /// <remarks>
    /// A single quote or a right single quotation mark sitting between two letters is an
    /// apostrophe, not a quotation: "make today's plan shorter" is a plain command and must
    /// stay one. Only a quote that opens or closes a run of text counts.
    /// </remarks>
    private static bool ContainsQuotation(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c is '"' or '\u201C' or '\u201D' or '\u300C' or '\u300D' or '\u300E' or '\u300F' or '`')
            {
                return true;
            }

            if (c is '\'' or '\u2018' or '\u2019' && !IsApostrophe(value, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the mark at <paramref name="index"/> sits inside a word.</summary>
    private static bool IsApostrophe(string value, int index) =>
        index > 0
        && index < value.Length - 1
        && char.IsLetter(value[index - 1])
        && char.IsLetter(value[index + 1]);

    /// <summary>
    /// Whole-token containment for Latin markers, prefix containment for CJK, which does not
    /// separate words with spaces.
    /// </summary>
    private static bool ContainsPhrase(string normalized, string marker)
    {
        if (marker.Length == 0)
        {
            return false;
        }

        if (!char.IsAscii(marker[0]))
        {
            return normalized.Contains(marker, StringComparison.Ordinal);
        }

        if (marker.Contains(' ', StringComparison.Ordinal))
        {
            return normalized.Contains(marker, StringComparison.Ordinal);
        }

        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPlanReference(string normalized) =>
        EnglishPlanReferences.Any(reference => ContainsPhrase(normalized, reference))
        || FindKoreanPlanReference(TokenizeKoreanEojol(normalized)) is not null;

    private static bool HasExplicitPlanAction(SemanticClause clause)
    {
        if (IsInformationalPlanConstruction(clause) || IsNonCurrentPlanConstruction(clause.Text))
        {
            return false;
        }

        return HasDirectEnglishPlanAction(clause.Text)
            || HasDirectKoreanPlanAction(clause.Text, clause.OriginalText);
    }

    private static bool HasNegatedPlanAction(SemanticClause clause)
    {
        if (HasNegatedKoreanPlanAction(clause.Text))
        {
            return true;
        }

        var tokens = Tokens(clause.Text);
        var plan = FindEnglishPlanReference(tokens);
        if (plan is null)
        {
            return false;
        }

        foreach (var actionIndex in FindEnglishActionIndexes(tokens))
        {
            if (!DirectlyGovernsEnglishPlan(tokens, actionIndex, plan.Value))
            {
                continue;
            }

            var prefixStart = Math.Max(0, actionIndex - 6);
            var actionPrefix = string.Join(' ', tokens[prefixStart..actionIndex]);
            if (EnglishNegationMarkers.Any(marker => ContainsPhrase(actionPrefix, marker))
                || (actionIndex > 0 && tokens[actionIndex - 1] == "not"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDirectEnglishPlanAction(string clause)
    {
        var tokens = Tokens(clause);
        var plan = FindEnglishPlanReference(tokens);
        if (plan is null)
        {
            return false;
        }

        foreach (var actionIndex in FindEnglishActionIndexes(tokens))
        {
            if (!DirectlyGovernsEnglishPlan(tokens, actionIndex, plan.Value))
            {
                continue;
            }

            if (actionIndex > plan.Value.Start
                || IsAllowedActionLeadIn(tokens[..actionIndex], tokens[actionIndex]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDirectKoreanPlanAction(string clause, string originalClause)
    {
        var tokens = TokenizeKoreanEojol(clause);
        var plan = FindKoreanPlanReference(tokens);
        if (plan is null || HasNegatedKoreanPlanAction(tokens, plan.Value))
        {
            return false;
        }

        var actionIndex = tokens.Length - 1;
        if (actionIndex < plan.Value.End)
        {
            return false;
        }

        var action = tokens[actionIndex];
        if (KoreanPlanActionTokens.Contains(action, StringComparer.Ordinal))
        {
            return DirectlyGovernsKoreanPlan(
                tokens,
                plan.Value,
                actionIndex,
                action,
                originalClause);
        }

        if (!KoreanStateSettingActionTokens.Contains(action, StringComparer.Ordinal))
        {
            return false;
        }

        if (actionIndex > plan.Value.End
            && tokens[actionIndex - 1] == "변경되게")
        {
            return plan.Value.Particle == KoreanPlanParticle.Subject
                && IsNumericDurationBridge(tokens.AsSpan(
                    plan.Value.End,
                    actionIndex - plan.Value.End - 1),
                    originalClause);
        }

        return HasExplicitKoreanStateSettingComplement(
            tokens,
            plan.Value,
            actionIndex,
            originalClause);
    }

    private static bool HasExplicitKoreanStateSettingComplement(
        string[] tokens,
        KoreanPlanSpan plan,
        int actionIndex,
        string originalClause)
    {
        if (plan.Particle is not (KoreanPlanParticle.Object or KoreanPlanParticle.Topic))
        {
            return false;
        }

        // Generic 하다 is authority only for the closed duration value grammar. In particular,
        // 기준으로 and other arbitrary -(으)로 complements are references, not state changes.
        return IsNumericDurationBridge(tokens.AsSpan(
            plan.End,
            actionIndex - plan.End),
            originalClause);
    }

    private static bool DirectlyGovernsKoreanPlan(
        string[] tokens,
        KoreanPlanSpan plan,
        int actionIndex,
        string action,
        string originalClause)
    {
        var bridge = tokens.AsSpan(plan.End, actionIndex - plan.End);

        // A bare plan noun governs an adjacent reviewed predicate or that predicate through the
        // same closed numeric-duration value accepted for particle-marked plan targets. Unknown
        // intervening eojol, including a destination such as 메모에, always revokes authority.
        if (plan.Particle == KoreanPlanParticle.Bare)
        {
            return bridge.Length == 0
                || KoreanDurationActionTokens.Contains(action, StringComparer.Ordinal)
                    && IsNumericDurationBridge(bridge, originalClause);
        }

        if (plan.Particle is KoreanPlanParticle.Subject
            or KoreanPlanParticle.Destination
            or KoreanPlanParticle.Genitive)
        {
            return false;
        }

        if (bridge.Length == 0)
        {
            return true;
        }

        if (KoreanDurationActionTokens.Contains(action, StringComparer.Ordinal)
            && IsNumericDurationBridge(bridge, originalClause))
        {
            return true;
        }

        return KoreanFocusActionTokens.Contains(action, StringComparer.Ordinal)
            && IsSupportedFocusBridge(bridge);
    }

    private static bool IsNumericDurationBridge(
        ReadOnlySpan<string> bridge,
        string originalClause)
    {
        if (bridge.Length != 1
            || !HasCanonicalOriginalDurationToken(originalClause))
        {
            return false;
        }

        return IsCanonicalAvailableMinutesToken(bridge[0]);
    }

    /// <summary>
    /// Validates the learner's original eojol before general normalization can erase a sign or
    /// punctuation. The duration must be one exact token immediately after the plan target.
    /// </summary>
    private static bool HasCanonicalOriginalDurationToken(string originalClause)
    {
        var value = originalClause.Normalize(NormalizationForm.FormC);
        var source = value.AsSpan();
        var previousStart = -1;
        var previousLength = 0;
        var durationCount = 0;

        for (var index = 0; index < source.Length;)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            if (index == source.Length)
            {
                break;
            }

            var tokenStart = index;
            while (index < source.Length && !char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            var token = source[tokenStart..index];
            if (token.IndexOf(KoreanMinuteBridgeSuffix, StringComparison.Ordinal) >= 0)
            {
                durationCount++;
                if (durationCount > 1
                    || previousStart < 0
                    || !IsKoreanPlanToken(source.Slice(previousStart, previousLength))
                    || !IsCanonicalAvailableMinutesToken(token))
                {
                    return false;
                }
            }

            previousStart = tokenStart;
            previousLength = token.Length;
        }

        return durationCount == 1;
    }

    private static bool IsCanonicalAvailableMinutesToken(ReadOnlySpan<char> token)
    {
        if (!token.EndsWith(KoreanMinuteBridgeSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = token[..^KoreanMinuteBridgeSuffix.Length];
        if (digits.Length == 0
            || digits.Length > MaxAvailableMinutesDigitCount
            || digits.Length > 1 && digits[0] == '0')
        {
            return false;
        }

        foreach (var digit in digits)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(
                digits,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var minutes)
            && minutes >= CoachConstraintLimits.MinAvailableMinutes
            && minutes <= CoachConstraintLimits.MaxAvailableMinutes;
    }

    private static bool IsKoreanPlanToken(ReadOnlySpan<char> token) =>
        token.SequenceEqual("계획")
        || token.SequenceEqual("계획을")
        || token.SequenceEqual("계획은")
        || token.SequenceEqual("계획이");

    private static int DecimalDigitCount(int value)
    {
        var count = 1;
        while (value >= 10)
        {
            value /= 10;
            count++;
        }

        return count;
    }

    private static bool IsSupportedFocusBridge(ReadOnlySpan<string> bridge) =>
        bridge.Length == 2
        && KoreanFocusValueNouns.Contains(bridge[0], StringComparer.Ordinal)
        && bridge[1] is "중심으로" or "위주로";

    private static bool DirectlyGovernsEnglishPlan(
        string[] tokens,
        int actionIndex,
        TokenSpan plan)
    {
        if (actionIndex > plan.Start)
        {
            var planEnd = plan.Start + plan.Length;
            if (actionIndex != planEnd + 1
                || tokens[planEnd] != "to")
            {
                return false;
            }

            var subjectPrefix = string.Join(' ', tokens[..plan.Start]);
            return subjectPrefix is "i want" or "please make" or "please set";
        }

        var bridge = tokens[(actionIndex + 1)..plan.Start];
        if (bridge.Length == 0)
        {
            return true;
        }

        if (!SuggestionActionMarkers.Contains(tokens[actionIndex], StringComparer.Ordinal))
        {
            return false;
        }

        var bridgeText = string.Join(' ', bridge);
        return bridgeText is "a change to" or "changes to"
            or "one change to" or "one useful change to" or "a useful change to"
            or "changing" or "making" or "focusing" or "adjusting" or "updating"
            or "revising" or "replacing";
    }

    private static bool IsAllowedActionLeadIn(string[] prefixTokens, string action)
    {
        var prefix = string.Join(' ', prefixTokens);
        if (prefix is "" or "please" or "also" or "please also"
            or "can you" or "could you" or "would you"
            or "i want to" or "i d like to" or "i would like to")
        {
            return true;
        }

        return action == "focusing" && prefix == "stop";
    }

    private static bool IsInformationalPlanConstruction(SemanticClause clause)
    {
        var text = clause.Text;
        if (text.StartsWith("please ", StringComparison.Ordinal))
        {
            text = text["please ".Length..];
        }

        var koreanTokens = TokenizeKoreanEojol(text);
        return InformationalPlanPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal))
            || koreanTokens.Length > 0
                && koreanTokens[0] is "뭐" or "무엇" or "어떤" or "언제" or "어디" or "왜"
            || koreanTokens.Any(token =>
                token is "어떻게" or "가능한지"
                    or "확인해줘" or "확인해주세요"
                    or "보여줘" or "보여주세요"
                    or "설명해줘" or "설명해주세요"
                    or "알려줘" or "알려주세요"
                    or "읽어줘" or "읽어주세요");
    }

    private static bool IsNonCurrentPlanConstruction(string clause) =>
        NonCurrentPlanMarkers.Any(marker =>
            marker.EndsWith(' ')
                ? clause.StartsWith(marker, StringComparison.Ordinal)
                    || clause.Contains($" {marker}", StringComparison.Ordinal)
                : ContainsPhrase(clause, marker));

    private static string[] Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<int> FindEnglishActionIndexes(string[] tokens)
    {
        for (var index = 0; index < tokens.Length; index++)
        {
            if (EnglishPlanActionMarkers.Contains(tokens[index], StringComparer.Ordinal))
            {
                yield return index;
            }
        }
    }

    private static TokenSpan? FindEnglishPlanReference(string[] tokens)
    {
        var references = new[]
        {
            new[] { "today", "s", "plan" },
            ["todays", "plan"],
            ["today", "plan"],
            ["plan", "for", "today"],
            ["plan", "today"],
            ["today", "s", "study", "plan"],
            ["today", "s", "practice", "plan"],
            ["my", "plan"],
            ["my", "study", "plan"],
            ["my", "practice", "plan"]
        };

        foreach (var reference in references)
        {
            for (var start = 0; start <= tokens.Length - reference.Length; start++)
            {
                if (tokens.AsSpan(start, reference.Length).SequenceEqual(reference))
                {
                    return new TokenSpan(start, reference.Length);
                }
            }
        }

        return null;
    }

    private static string[] TokenizeKoreanEojol(string value)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();

        foreach (var valueCharacter in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(valueCharacter))
            {
                token.Append(valueCharacter);
                continue;
            }

            if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return [.. tokens];
    }

    private static KoreanPlanSpan? FindKoreanPlanReference(string[] tokens)
    {
        for (var start = 0; start < tokens.Length; start++)
        {
            if (tokens[start] is not ("오늘" or "오늘의"))
            {
                continue;
            }

            var planIndex = start + 1;
            if (planIndex < tokens.Length && tokens[planIndex] is "학습" or "공부")
            {
                planIndex++;
            }

            if (planIndex >= tokens.Length
                || !TryParseKoreanPlanToken(tokens[planIndex], out var particle))
            {
                continue;
            }

            return new KoreanPlanSpan(start, planIndex - start + 1, particle);
        }

        return null;
    }

    private static bool TryParseKoreanPlanToken(
        string token,
        out KoreanPlanParticle particle)
    {
        particle = token switch
        {
            "계획" => KoreanPlanParticle.Bare,
            "계획을" => KoreanPlanParticle.Object,
            "계획은" => KoreanPlanParticle.Topic,
            "계획이" => KoreanPlanParticle.Subject,
            "계획에" or "계획에서" or "계획으로" => KoreanPlanParticle.Destination,
            "계획의" => KoreanPlanParticle.Genitive,
            _ => KoreanPlanParticle.Unknown
        };

        return particle != KoreanPlanParticle.Unknown;
    }

    private static bool HasNegatedKoreanPlanAction(string clause)
    {
        var tokens = TokenizeKoreanEojol(clause);
        var plan = FindKoreanPlanReference(tokens);
        return plan is not null && HasNegatedKoreanPlanAction(tokens, plan.Value);
    }

    private static bool HasNegatedKoreanPlanAction(
        string[] tokens,
        KoreanPlanSpan plan)
    {
        if (plan.Particle is KoreanPlanParticle.Subject
            or KoreanPlanParticle.Destination
            or KoreanPlanParticle.Genitive)
        {
            return false;
        }

        var tail = tokens.AsSpan(plan.End);
        foreach (var negatedTail in KoreanNegatedPlanTails)
        {
            if (tail.Length >= negatedTail.Length
                && tail[..negatedTail.Length].SequenceEqual(negatedTail))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBoundedActivityImperative(string clause, string marker)
    {
        var markerIndex = clause.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        if (!char.IsAscii(marker[0]))
        {
            return !clause.Contains("위해", StringComparison.Ordinal)
                && !clause.Contains("려고", StringComparison.Ordinal);
        }

        var prefix = clause[..markerIndex].Trim();
        return prefix.Length == 0
            || prefix is "please" or "can you" or "could you" or "would you"
                or "let s" or "lets" or "i want to" or "i would like to";
    }

    private static IReadOnlyList<SemanticClause> SemanticClauses(string value)
    {
        var clauses = new List<SemanticClause>();
        var start = 0;

        for (var index = 0; index <= value.Length; index++)
        {
            var atEnd = index == value.Length;
            var boundary = !atEnd && IsClauseBoundary(value[index]);
            if (!atEnd && !boundary)
            {
                continue;
            }

            var raw = value[start..index];
            AddSemanticSegments(clauses, raw);
            start = index + 1;
        }

        return clauses;
    }

    private static void AddSemanticSegments(
        List<SemanticClause> clauses,
        string original)
    {
        var normalized = Normalize(original);
        if (normalized.Length == 0)
        {
            return;
        }

        var remaining = normalized;
        var isPurpose = false;
        while (remaining.Length > 0)
        {
            var (index, connector, purposeConnector) = FindNextConnector(remaining);
            var segment = index < 0 ? remaining : remaining[..index];
            if (segment.Length > 0)
            {
                clauses.Add(new SemanticClause(segment.Trim(), isPurpose, original));
            }

            if (index < 0)
            {
                break;
            }

            isPurpose = isPurpose || purposeConnector;
            remaining = remaining[(index + connector.Length)..].Trim();
        }
    }

    private static (int Index, string Connector, bool Purpose) FindNextConnector(string value)
    {
        var candidates = new (string Connector, bool Purpose)[]
        {
            (" so that ", true),
            (" in order to ", true),
            (" and also ", false),
            (" 그리고 또 ", false),
            (" 그래서 ", true),
            (" and ", false),
            (" also ", false),
            (" but ", false),
            (" then ", false),
            (" so ", true),
            (" 그리고 ", false),
            (" 하지만 ", false)
        };

        var bestIndex = -1;
        var bestConnector = string.Empty;
        var bestPurpose = false;
        foreach (var candidate in candidates)
        {
            var index = value.IndexOf(candidate.Connector, StringComparison.Ordinal);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
                bestConnector = candidate.Connector;
                bestPurpose = candidate.Purpose;
            }
        }

        return (bestIndex, bestConnector, bestPurpose);
    }

    private static bool IsClauseBoundary(char value) =>
        value is ',' or '，' or '.' or ';' or '?' or '？' or '!' or '！' or '\n' or '\r';

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var rune in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(rune))
            {
                builder.Append(char.ToLower(rune, CultureInfo.InvariantCulture));
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private readonly record struct SemanticClause(
        string Text,
        bool IsPurpose,
        string OriginalText);

    private readonly record struct TokenSpan(int Start, int Length);

    private readonly record struct KoreanPlanSpan(
        int Start,
        int Length,
        KoreanPlanParticle Particle)
    {
        public int End => Start + Length;
    }

    private enum KoreanPlanParticle
    {
        Unknown = 0,
        Bare,
        Object,
        Topic,
        Subject,
        Destination,
        Genitive
    }
}
