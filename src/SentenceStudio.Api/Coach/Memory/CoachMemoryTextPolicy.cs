using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// The content gate for learner-authored memory text.
/// </summary>
/// <remarks>
/// <para>
/// Only one memory kind carries free text — <see cref="CoachMemoryKind.PersistentStudyGoal"/> —
/// and this class is the only thing standing between that text and a model prompt. It runs twice:
/// once when a candidate is created, and again when the formatter builds the prompt block. The
/// second pass is not redundant. It is what makes the system fail closed if a row was written by
/// an older, weaker version of these rules, or by anything other than this code path.
/// </para>
/// <para>
/// The rules are deliberately blunt and err toward refusal. A learner who cannot save
/// "study for the JLPT — see https://example.com" loses very little; a learner whose saved goal
/// can carry a role marker into every future prompt loses a great deal.
/// </para>
/// </remarks>
public static partial class CoachMemoryTextPolicy
{
    /// <summary>
    /// Normalizes learner text to the exact characters that will be stored, shown, and formatted.
    /// </summary>
    /// <remarks>
    /// Normalization happens before screening so that a rule cannot be evaded with unusual
    /// whitespace or a decomposed form, and before storage so the learner reads back precisely
    /// what a prompt would carry.
    /// </remarks>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var lastWasSpace = false;

        foreach (var rune in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(rune);
            var isSeparator = char.IsWhiteSpace(rune) || category == UnicodeCategory.LineSeparator;

            if (isSeparator)
            {
                if (builder.Length > 0 && !lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(rune);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Screens normalized text. Returns <see cref="CoachMemoryValueRejection.None"/> when the text
    /// is safe to store and to place in a prompt.
    /// </summary>
    /// <param name="normalized">Text already passed through <see cref="Normalize"/>.</param>
    /// <param name="maxLength">The kind's length bound.</param>
    public static CoachMemoryValueRejection Screen(string normalized, int maxLength)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return CoachMemoryValueRejection.Empty;
        }

        if (normalized.Length > maxLength)
        {
            return CoachMemoryValueRejection.TooLong;
        }

        foreach (var rune in normalized)
        {
            // Normalize() already collapsed legitimate whitespace, so anything left in the control
            // or format categories is either a smuggled line break or a bidi/zero-width trick.
            var category = CharUnicodeInfo.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse)
            {
                return CoachMemoryValueRejection.ControlCharacters;
            }
        }

        if (RoleMarkerPattern().IsMatch(normalized))
        {
            return CoachMemoryValueRejection.RoleMarker;
        }

        if (InstructionPattern().IsMatch(normalized))
        {
            return CoachMemoryValueRejection.Instruction;
        }

        if (SecretPattern().IsMatch(normalized))
        {
            return CoachMemoryValueRejection.Secret;
        }

        if (SensitivePattern().IsMatch(normalized) || EmailPattern().IsMatch(normalized) || PhonePattern().IsMatch(normalized))
        {
            return CoachMemoryValueRejection.SensitivePersonalDetail;
        }

        if (LinkPattern().IsMatch(normalized))
        {
            return CoachMemoryValueRejection.Link;
        }

        if (CommandPattern().IsMatch(normalized))
        {
            return CoachMemoryValueRejection.Command;
        }

        if (AssessmentPattern().IsMatch(normalized))
        {
            return CoachMemoryValueRejection.AssessmentAnswer;
        }

        return CoachMemoryValueRejection.None;
    }

    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Chat, template, and tool markers that would let saved text impersonate a speaker.</summary>
    [GeneratedRegex(
        @"(<\|[^|]*\|>)|(<</?SYS>>)|(\[/?INST\])|(</?s>)|(^|\s|[\[\(#])(system|assistant|developer|tool|function|user)\s*[:=]|(#{2,}\s*(system|assistant|instruction))|(\bfunction_call\b)|(\btool_call\b)",
        Flags)]
    private static partial Regex RoleMarkerPattern();

    /// <summary>Attempts to steer the model rather than describe the learner.</summary>
    [GeneratedRegex(
        @"\b(ignore|disregard|forget|override|bypass|reveal|leak)\b[^.]{0,32}\b(previous|prior|earlier|above|all|any|your|the)?\s*(instruction|instructions|policy|policies|rules?|prompt|context|memory|guardrails?|everything)\b|\byou (are|must|should|will|shall)\b|\bact as\b|\bpretend to be\b|\bfrom now on\b|\bnew instructions?\b|\bjailbreak\b|\bdo not follow\b|\balways (say|answer|reply|respond)\b|\bnever (say|answer|reply|mention|refuse)\b|\bdelete (the )?(database|data|account|everything)\b|\bdrop (the )?(database|table)\b",
        Flags)]
    private static partial Regex InstructionPattern();

    /// <summary>Credential shapes. A saved goal has no business carrying one.</summary>
    [GeneratedRegex(
        @"\b(api[_\-\s]?key|access[_\-\s]?token|refresh[_\-\s]?token|bearer|client[_\-\s]?secret|passwd|password|passphrase|private[_\-\s]?key|secret)\b|-----BEGIN|\bsk-[A-Za-z0-9_\-]{8,}|\bgh[pousr]_[A-Za-z0-9]{8,}|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{6,}|\b[A-Fa-f0-9]{32,}\b",
        Flags)]
    private static partial Regex SecretPattern();

    /// <summary>Identifying or special-category personal detail.</summary>
    [GeneratedRegex(
        @"\b(social security|ssn|passport|credit card|bank account|routing number|date of birth|home address|my address|street address|postal code|zip code|salary|diagnos(is|ed)|prescription|medication|mental health|religion|religious|political party|sexual orientation|immigration status|visa number)\b|\b\d{3}-\d{2}-\d{4}\b|\b(?:\d[ \-]?){13,19}\b",
        Flags)]
    private static partial Regex SensitivePattern();

    /// <summary>Any address-shaped token.</summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", Flags)]
    private static partial Regex EmailPattern();

    /// <summary>Phone-shaped runs, including grouped international forms.</summary>
    [GeneratedRegex(@"(?<!\d)\+?\d[\d\s().\-]{8,}\d(?!\d)", Flags)]
    private static partial Regex PhonePattern();

    /// <summary>Links, schemes, and bare hostnames.</summary>
    [GeneratedRegex(
        @"\b[a-z][a-z0-9+.\-]*://|\bwww\.[^\s]+|\b[a-z0-9\-]+\.(com|net|org|io|ai|dev|co|app|gov|edu|ru|cn|jp|kr|xyz|link)\b|\bdata:[a-z]+/",
        Flags)]
    private static partial Regex LinkPattern();

    /// <summary>Shell, SQL, script, and template-injection shapes.</summary>
    [GeneratedRegex(
        @"\b(rm\s+-rf|sudo|chmod|chown|curl|wget|nc\s+-l|ssh|scp|kill\s+-9|shutdown|reboot)\b|\b(drop|truncate|alter|insert|update|delete)\s+(table|from|into|database)\b|\bselect\b[^.]{0,40}\bfrom\b|\bunion\s+select\b|\bexec(ute)?\s*\(|\bos\.system\b|\bsubprocess\b|\beval\s*\(|<\s*script\b|\bjavascript:|\$\{[^}]*\}|\$\([^)]*\)|`[^`]*`|;\s*--|\b0x[0-9a-f]{8,}\b",
        Flags)]
    private static partial Regex CommandPattern();

    /// <summary>Graded-material answers. Saved memory must not become an answer key.</summary>
    [GeneratedRegex(
        @"\b(answer key|correct answers?|the answers? (is|are)|quiz answers?|test answers?|exam answers?|cheat sheet)\b",
        Flags)]
    private static partial Regex AssessmentPattern();
}
