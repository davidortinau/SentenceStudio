using System.Collections.Generic;

namespace SentenceStudio.Contracts.Conversation;

/// <summary>
/// Body for <c>POST /api/v1/conversation/continue</c>.
/// Keys serialize as camelCase via the minimal-API web defaults.
/// </summary>
public sealed class ConversationContinueResponse
{
    public string AssistantMessage { get; set; } = string.Empty;

    /// <summary>
    /// Integer 0-100. The underlying agent grades on a 0.0-1.0 scale; the
    /// endpoint multiplies, clamps, and rounds before serializing.
    /// </summary>
    public int ComprehensionScore { get; set; }

    public string? ComprehensionNotes { get; set; }

    public List<ConversationGrammarCorrectionDto> GrammarCorrections { get; set; } = new();

    public List<ConversationVocabularyAnalysisDto> VocabularyAnalysis { get; set; } = new();

    /// <summary>
    /// True when a finite scenario reaches its natural end. v1 always emits
    /// false; the Flutter UI handles false gracefully. See spec 004 §Behavior.
    /// </summary>
    public bool IsComplete { get; set; }
}

public sealed class ConversationGrammarCorrectionDto
{
    public string Original { get; set; } = string.Empty;
    public string Corrected { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
}

public sealed class ConversationVocabularyAnalysisDto
{
    public string UsedForm { get; set; } = string.Empty;
    public string DictionaryForm { get; set; } = string.Empty;
    public string Meaning { get; set; } = string.Empty;
    public bool UsageCorrect { get; set; }
    public string? UsageExplanation { get; set; }
}
