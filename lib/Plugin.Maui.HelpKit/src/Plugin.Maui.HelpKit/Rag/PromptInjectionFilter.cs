namespace Plugin.Maui.HelpKit.Rag;

/// <summary>
/// Output-side defence against prompt injection: scans the LLM response for fingerprint phrases
/// from <see cref="SystemPrompt.FingerprintPhrases"/> and replaces the whole reply with a generic
/// refusal when leakage is detected. Complements the delimiter-fenced <c>&lt;doc&gt;</c> tagging
/// on the input side.
/// </summary>
public static class PromptInjectionFilter
{
    /// <summary>Generic refusal rendered when leakage is detected.</summary>
    public const string LeakRefusal = "I can't share my instructions, but I'm happy to help with the app.";

    /// <summary>
    /// Returns <c>true</c> (and a cleaned/refusal response) when the model output contains
    /// fingerprint phrases from the system prompt — a strong signal the model is echoing
    /// its own instructions.
    /// </summary>
    public static bool TryDetectLeak(string llmOutput, out string sanitizedOutput)
    {
        sanitizedOutput = llmOutput ?? string.Empty;
        if (string.IsNullOrWhiteSpace(llmOutput))
            return false;

        foreach (var phrase in SystemPrompt.FingerprintPhrases)
        {
            if (llmOutput.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                sanitizedOutput = LeakRefusal;
                return true;
            }
        }

        // Also catch literal echoes of the "rules" list structure.
        if (llmOutput.Contains("You are the in-app help assistant", StringComparison.OrdinalIgnoreCase)
            || llmOutput.Contains("system instructions", StringComparison.OrdinalIgnoreCase)
            && llmOutput.Contains("STRICTLY", StringComparison.OrdinalIgnoreCase))
        {
            sanitizedOutput = LeakRefusal;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Convenience wrapper: always returns the (possibly rewritten) safe output.
    /// </summary>
    public static string Sanitize(string llmOutput)
    {
        TryDetectLeak(llmOutput, out var sanitized);
        return sanitized;
    }
}
