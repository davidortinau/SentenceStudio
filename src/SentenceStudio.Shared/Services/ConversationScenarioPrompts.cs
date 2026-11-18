namespace SentenceStudio.Services;

/// <summary>What of a scenario goes into a conversation's prompt in a given language.</summary>
public static class ConversationScenarioPrompts
{
    /// <summary>
    /// The scenario's question bank, or nothing when it is written in another language than the
    /// conversation's. The built-in banks are Korean; a German conversation fed Korean questions
    /// would put the wrong language in front of the learner (Learning Value Gate).
    /// </summary>
    public static string QuestionBankFor(string? questionBank, string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(questionBank))
            return string.Empty;
        var korean = questionBank.Any(c => c is >= '가' and <= '힣');
        var targetIsKorean = string.IsNullOrWhiteSpace(targetLanguage)
            || string.Equals(targetLanguage.Trim(), "Korean", StringComparison.OrdinalIgnoreCase);
        return korean && !targetIsKorean ? string.Empty : questionBank;
    }
}
