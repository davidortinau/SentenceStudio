using System.Globalization;
using System.Text.RegularExpressions;

namespace SentenceStudio.Native.Features.Import;

/// <summary>
/// The shared <c>ContentImportService</c> returns its detected format, warnings, row errors and
/// result reasons as English text. A native page shows them through these resource keys; text
/// the map does not know (an AI note, an exception message) is shown as it came (B74).
/// </summary>
public static partial class ImportServiceText
{
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Tab-delimited (TSV)"] = "Import_Format_Tsv",
        ["Comma-delimited (CSV)"] = "Import_Format_Csv",
        ["Pipe-delimited"] = "Import_Format_Pipe",
        ["Free-form text (AI-extracted)"] = "Import_Format_FreeText",
        ["Transcript"] = "Import_ContentType_Transcript",
        ["Sentences (parsed + AI-extracted)"] = "Import_Format_Sentences",
        ["Phrases (parsed + AI-extracted)"] = "Import_Format_Phrases",
        ["Unknown"] = "Vocabulary_Unknown",
    };

    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        // Preview warnings
        ["Transcript will be stored but no vocabulary extraction was requested."] =
            "Import_Warn_TranscriptOnly",
        ["No vocabulary extracted from transcript. The transcript will still be stored on the resource."] =
            "Import_Warn_TranscriptNoVocab",
        // Row errors
        ["Target language term is required."] = "Import_RowError_TargetRequired",
        ["Native language term missing (will use AI translation)."] = "Import_RowError_NativeMissing",
        ["AI could not translate this term."] = "Import_RowError_NotTranslated",
        ["Content exceeds size limit for phrase extraction (50KB)."] = "Import_RowError_TooLargePhrases",
        ["Content exceeds size limit for free-text extraction (50KB)."] = "Import_RowError_TooLargeFreeText",
        ["No vocabulary extracted. Check language settings or provide structured data."] =
            "Import_RowError_NoVocabulary",
        // Result reasons
        ["Already exists in resource"] = "Import_Reason_AlreadyExists",
        ["Duplicate within batch"] = "Import_PreviewDuplicate_DuplicateWithinBatch",
        ["Phrase-linked entries require manual edit"] = "Import_Reason_PhraseLinked",
        ["Target language term is empty"] = "Import_Reason_TargetEmpty",
        ["Native language term is empty (AI translation not yet implemented)"] = "Import_Reason_NativeEmpty",
    };

    private static readonly (Regex Pattern, string Key)[] Patterns =
    [
        (WordExtractionFailed(), "Import_Warn_AiWordExtractionFailed"),
        (TranslationFailed(), "Import_Warn_AiTranslationFailed"),
        (TooLargePhrases(), "Import_Warn_TooLargePhrases"),
        (TooLargeFreeText(), "Import_Warn_TooLargeFreeText"),
        (RowExtractionFailed(), "Import_RowError_AiExtractionFailed"),
        (ExtractionFailed(), "Import_Warn_AiExtractionFailed"),
    ];

    /// <summary>The detected format line (ContentImportPreview.DetectedFormat).</summary>
    public static string DetectedFormat(string value, Func<string, string> text) =>
        Formats.TryGetValue(value, out var key) ? text(key) : value;

    /// <summary>A preview warning, a row error or a result reason.</summary>
    public static string Message(string? value, Func<string, string> text)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (Fixed.TryGetValue(value, out var key))
            return text(key);
        foreach (var (pattern, patternKey) in Patterns)
        {
            var match = pattern.Match(value);
            if (match.Success)
                return string.Format(CultureInfo.CurrentCulture, text(patternKey),
                    match.Groups[1].Value);
        }
        return value;
    }

    [GeneratedRegex(@"^AI word extraction failed: (.*)\. Primary entries are still available\.$",
        RegexOptions.Singleline)]
    private static partial Regex WordExtractionFailed();

    [GeneratedRegex(@"^AI translation failed: (.*)\. Please provide translations manually\.$",
        RegexOptions.Singleline)]
    private static partial Regex TranslationFailed();

    [GeneratedRegex(@"^Content is too large for phrase extraction \((\d+) chars\)\. Please split into smaller chunks\.$")]
    private static partial Regex TooLargePhrases();

    [GeneratedRegex(@"^Content is too large for free-text extraction \((\d+) chars\)\. Please provide structured data or split into smaller chunks\.$")]
    private static partial Regex TooLargeFreeText();

    [GeneratedRegex(@"^AI extraction failed: (.*)\. Please check your content or provide a structured format\.$",
        RegexOptions.Singleline)]
    private static partial Regex RowExtractionFailed();

    [GeneratedRegex(@"^AI extraction failed: (.*)$", RegexOptions.Singleline)]
    private static partial Regex ExtractionFailed();
}
