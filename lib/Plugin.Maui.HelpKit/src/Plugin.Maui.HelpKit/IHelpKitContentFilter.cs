using System.Text.RegularExpressions;

namespace Plugin.Maui.HelpKit;

/// <summary>
/// Hook invoked during ingestion to redact or transform content before it
/// reaches the embedding model or on-disk vector store.
/// </summary>
public interface IHelpKitContentFilter
{
    /// <summary>
    /// Returns a redacted copy of <paramref name="content"/>. The return
    /// value is what gets chunked, embedded, and persisted.
    /// </summary>
    string Redact(string content);
}

/// <summary>
/// Default <see cref="IHelpKitContentFilter"/> that applies naive regex-based
/// redaction for common secret patterns. Not a security boundary; pair with a
/// real secret scanner for sensitive corpora.
/// </summary>
public sealed class DefaultSecretRedactor : IHelpKitContentFilter
{
    private const string Placeholder = "[REDACTED]";

    // Ordering matters — most specific first.
    private static readonly (Regex Pattern, string Replacement)[] Rules = new[]
    {
        // "api_key: sk-..." / "password = 'abc'" / "token: xyz"
        (new Regex(
            @"(?ix)
              \b(api[_\-\s]?key|secret|password|passwd|token|bearer|authorization)\b
              \s*[:=]\s*
              [""']?[A-Za-z0-9_\-\.]{8,}[""']?",
            RegexOptions.Compiled),
         "$1: " + Placeholder),

        // Common provider-prefixed keys (OpenAI, GitHub, AWS, Slack, etc.)
        (new Regex(@"\b(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16})\b",
            RegexOptions.Compiled),
         Placeholder),

        // Email addresses
        (new Regex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled),
         Placeholder),

        // GUIDs preceded by a secret-ish keyword
        (new Regex(
            @"(?i)\b(secret|token|key|credential|client[_\-]?id|tenant[_\-]?id)\b[^A-Za-z0-9]{0,6}[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
            RegexOptions.Compiled),
         "$1: " + Placeholder),
    };

    /// <inheritdoc />
    public string Redact(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var working = content;
        foreach (var (pattern, replacement) in Rules)
            working = pattern.Replace(working, replacement);

        return working;
    }
}
