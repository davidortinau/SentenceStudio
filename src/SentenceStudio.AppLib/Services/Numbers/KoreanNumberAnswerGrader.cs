using System.Text.RegularExpressions;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

public class KoreanNumberAnswerGrader : INumberAnswerGrader
{
    public GradeResult Grade(NumberItem item, string userAnswer, int latencyMs)
    {
        var normalized = NormalizeAnswer(userAnswer);
        var canonicalNormalized = NormalizeAnswer(item.CanonicalAnswer);

        // Exact match
        if (normalized == canonicalNormalized)
        {
            return new GradeResult(
                IsCorrect: true,
                Verdict: "정확해요!",
                ErrorClass: null,
                CanonicalAnswer: item.CanonicalAnswer,
                Tip: null
            );
        }

        // Check acceptable alternates
        foreach (var alternate in item.AcceptableAlternates)
        {
            if (normalized == NormalizeAnswer(alternate))
            {
                return new GradeResult(
                    IsCorrect: true,
                    Verdict: "정확해요!",
                    ErrorClass: null,
                    CanonicalAnswer: item.CanonicalAnswer,
                    Tip: null
                );
            }
        }

        // If exact match failed, classify the error
        var (errorClass, tip) = ClassifyError(item, normalized, canonicalNormalized);

        return new GradeResult(
            IsCorrect: false,
            Verdict: "다시 해 볼까요?",
            ErrorClass: errorClass,
            CanonicalAnswer: item.CanonicalAnswer,
            Tip: tip
        );
    }

    private string NormalizeAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return "";

        // Trim whitespace
        var normalized = answer.Trim();

        // Convert full-width digits to half-width
        normalized = ConvertFullWidthToHalfWidth(normalized);

        // Lowercase for romanization comparison
        normalized = normalized.ToLowerInvariant();

        // Remove optional spaces between number and counter
        // But keep spaces within the number phrase itself
        return normalized;
    }

    private string ConvertFullWidthToHalfWidth(string input)
    {
        var result = input;
        for (char c = '０'; c <= '９'; c++)
        {
            result = result.Replace(c, (char)('0' + (c - '０')));
        }
        return result;
    }

    private (string ErrorClass, string Tip) ClassifyError(NumberItem item, string userNormalized, string canonicalNormalized)
    {
        // Check for counter mismatch FIRST (before Sino/Native swap)
        // This prevents false positives where wrong counter + wrong number system both exist
        if (item.CounterText != null)
        {
            var userHasCorrectCounter = userNormalized.Contains(item.CounterText);
            var userHasAnyCounter = ContainsAnyCounter(userNormalized);
            
            if (!userHasCorrectCounter && userHasAnyCounter)
            {
                return ("CounterMismatch", 
                    $"The correct counter is {item.CounterText}. Different counters go with different objects.");
            }
        }

        // Check for Sino/Native system swap
        if (ContainsSinoNumbers(userNormalized) && item.System == NumberSystem.Native)
        {
            return ("SinoNativeSwap", 
                "Native (한, 두, 세) is used with counters like 잔, 개, 명. Sino (일, 이, 삼) is used for minutes, money, dates.");
        }

        if (ContainsNativeNumbers(userNormalized) && item.System == NumberSystem.Sino)
        {
            return ("SinoNativeSwap",
                "Sino (일, 이, 삼) is used here. Native (하나, 둘, 셋) is used with counters.");
        }

        // Check for sound change errors
        var soundChangeErrors = DetectSoundChangeError(item, userNormalized);
        if (soundChangeErrors != null)
        {
            return soundChangeErrors.Value;
        }

        // Check for magnitude errors (off by 10x)
        if (item.SubModeCode == "ListenAndType")
        {
            if (TryParseMagnitude(userNormalized, out var userMagnitude) &&
                TryParseMagnitude(item.DigitValue.ToString(), out var correctMagnitude))
            {
                if (Math.Abs(userMagnitude - correctMagnitude) == 1)
                {
                    return ("MagnitudeOff10x",
                        "Check the magnitude. Did you count the zeros correctly?");
                }
            }
        }

        // Check for single character difference (typo)
        if (LevenshteinDistance(userNormalized, canonicalNormalized) == 1)
        {
            return ("Typo", 
                "Almost! Check your spelling carefully.");
        }

        // Check for wrong format (e.g., user typed Hangul when digits expected)
        if (item.SubModeCode == "ListenAndType" && !ContainsDigits(userNormalized))
        {
            return ("WrongFormat",
                "Please type the number using digits (1, 2, 3...).");
        }

        if (item.SubModeCode == "ReadAndProduce" && ContainsDigits(userNormalized))
        {
            return ("WrongFormat",
                "Please type the answer in Hangul (한글).");
        }

        // Default: unknown error
        return ("Unknown", 
            $"The correct answer is: {item.CanonicalAnswer}");
    }

    private bool ContainsSinoNumbers(string text)
    {
        var sinoDigits = new[] { "일", "이", "삼", "사", "오", "육", "칠", "팔", "구", "영" };
        return sinoDigits.Any(d => text.Contains(d));
    }

    private bool ContainsNativeNumbers(string text)
    {
        var nativeNumbers = new[] { "하나", "둘", "셋", "넷", "다섯", "여섯", "일곱", "여덟", "아홉",
                                    "한", "두", "세", "네" }; // Including sound-changed forms
        return nativeNumbers.Any(n => text.Contains(n));
    }

    private bool ContainsAnyCounter(string text)
    {
        var counters = new[] { "개", "명", "마리", "권", "잔", "병", "장", "번", "살", "시", "분" };
        return counters.Any(c => text.Contains(c));
    }

    private (string, string)? DetectSoundChangeError(NumberItem item, string userAnswer)
    {
        // Check for common sound change errors
        var soundChangePatterns = new Dictionary<string, string>
        {
            { "둘 ", "두 " },  // 둘 → 두 before counter
            { "하나 ", "한 " }, // 하나 → 한 before counter
            { "셋 ", "세 " },   // 셋 → 세 before counter
            { "넷 ", "네 " },   // 넷 → 네 before counter
        };

        foreach (var (wrong, correct) in soundChangePatterns)
        {
            if (userAnswer.Contains(wrong) && item.CanonicalAnswer.Contains(correct))
            {
                var wrongWord = wrong.Trim();
                var correctWord = correct.Trim();
                return ("SoundChangeMissed", 
                    $"{wrongWord} → {correctWord} before counter.");
            }
        }

        // Check for 스물/스무 confusion
        if (userAnswer.Contains("스물 살") && item.CanonicalAnswer.Contains("스무 살"))
        {
            return ("SoundChangeMissed",
                "스물 → 스무 at exactly 20 before 살.");
        }

        if (userAnswer.Contains("스무") && item.CanonicalAnswer.Contains("스물") && item.DigitValue != 20)
        {
            return ("SoundChangeMissed",
                "스무 is only used at exactly 20. From 21-29, use 스물하나, 스물둘, etc.");
        }

        return null;
    }

    private bool TryParseMagnitude(string text, out int magnitude)
    {
        // Try to extract numeric value and calculate magnitude (number of digits)
        var digits = Regex.Match(text, @"\d+");
        if (digits.Success && long.TryParse(digits.Value, out var value))
        {
            magnitude = value == 0 ? 0 : (int)Math.Floor(Math.Log10(value));
            return true;
        }
        magnitude = 0;
        return false;
    }

    private bool ContainsDigits(string text)
    {
        return Regex.IsMatch(text, @"\d");
    }

    private int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s))
            return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t))
            return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];

        for (int i = 0; i <= s.Length; i++)
            d[i, 0] = i;
        for (int j = 0; j <= t.Length; j++)
            d[0, j] = j;

        for (int j = 1; j <= t.Length; j++)
        {
            for (int i = 1; i <= s.Length; i++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost
                );
            }
        }

        return d[s.Length, t.Length];
    }
}
