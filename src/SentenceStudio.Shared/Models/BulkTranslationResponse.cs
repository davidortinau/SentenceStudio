using System.ComponentModel;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// AI response DTO for bulk translation of vocabulary terms.
/// Used with TranslateMissingNativeTerms.scriban-txt template.
/// Deserialized directly from the LLM's JSON output via AiService.SendPrompt&lt;T&gt;.
/// </summary>
public class BulkTranslationResponse
{
    [Description("List of target language terms with their native language translations")]
    public List<TranslationPair> Translations { get; set; } = new();
}

/// <summary>
/// A single target-to-native translation pair returned by the AI.
/// </summary>
public class TranslationPair
{
    [Description("Target language term from the input list")]
    public string TargetLanguageTerm { get; set; } = string.Empty;

    [Description("Native language definition — clear, concise")]
    public string NativeLanguageTerm { get; set; } = string.Empty;
}
