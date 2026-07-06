using System.Text;
using System.Text.RegularExpressions;

namespace SentenceStudio.Services;

/// <summary>
/// Shared sentence segmentation for transcripts and reading text.
/// </summary>
public static class TranscriptSentenceSegmenter
{
    public static List<string> Split(string transcript, bool splitOnNewlines = false)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new List<string>();

        if (splitOnNewlines)
        {
            var lineSentences = new List<string>();
            foreach (var line in Regex.Split(transcript, @"(?:\r\n|\r|\n)+"))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                lineSentences.AddRange(Split(line));
            }

            return lineSentences;
        }

        var cleanedText = transcript.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        var sentences = new List<string>();
        var currentSentence = new StringBuilder();

        for (int i = 0; i < cleanedText.Length; i++)
        {
            var c = cleanedText[i];
            currentSentence.Append(c);

            if (IsSentenceDelimiter(c))
            {
                var sentence = currentSentence.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(sentence))
                {
                    sentences.Add(sentence);
                }

                currentSentence.Clear();
            }
        }

        var remaining = currentSentence.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            if (!EndsWithSentenceDelimiter(remaining))
            {
                remaining += ".";
            }

            sentences.Add(remaining);
        }

        return sentences;
    }

    public static bool IsSentenceDelimiter(char c)
    {
        return c == '.' || c == '!' || c == '?' || c == '。' || c == '！' || c == '？';
    }

    private static bool EndsWithSentenceDelimiter(string text)
    {
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ||
            text.EndsWith('。') || text.EndsWith('！') || text.EndsWith('？');
    }
}
