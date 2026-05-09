using System.Text.RegularExpressions;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

public class KoreanNumberAnswerGrader : INumberAnswerGrader
{
    public GradeResult Grade(NumberItem item, string userAnswer, int latencyMs)
    {
        var normalized = NormalizeAnswer(userAnswer);
        var canonicalNormalized = NormalizeAnswer(item.CanonicalAnswer);

        // Special case: If user typed ONLY bare digits (no Korean, no counter), accept as shortcut
        if (Regex.IsMatch(normalized, @"^\d+$"))
        {
            // Extract digit value from canonical answer
            var canonicalDigitMatch = Regex.Match(item.DigitValue.ToString(), @"\d+");
            if (canonicalDigitMatch.Success && normalized == canonicalDigitMatch.Value)
            {
                return new GradeResult(
                    IsCorrect: true,
                    Verdict: "정확해요!",
                    ErrorClass: null,
                    CanonicalAnswer: item.CanonicalAnswer,
                    UserAnswer: userAnswer.Trim(),
                    Tip: null
                );
            }
        }

        // Special case: If user typed ONLY Korean number words (no digits, no counter), check if it matches
        if (!ContainsDigits(normalized) && !ContainsAnyCounter(normalized))
        {
            // Convert user's Korean to digits and compare — guard against cross-system swap.
            var userAsDigits = ConvertFullWidthToHalfWidth(KoreanToDigitString(normalized, item.System));
            if (!string.IsNullOrWhiteSpace(userAsDigits) && Regex.IsMatch(userAsDigits, @"^\d+$"))
            {
                if (int.TryParse(userAsDigits, out var userValue) && userValue == item.DigitValue
                    && !UsesWrongNumberSystem(normalized, canonicalNormalized, item.System))
                {
                    return new GradeResult(
                        IsCorrect: true,
                        Verdict: "정확해요!",
                        ErrorClass: null,
                        CanonicalAnswer: item.CanonicalAnswer,
                        UserAnswer: userAnswer.Trim(),
                        Tip: null
                    );
                }
            }
        }

        // Generate equivalent forms for permissive matching (system-aware)
        var userForms = KoreanNumberNormalizer.GenerateEquivalentForms(normalized, item.System);
        var canonicalForms = KoreanNumberNormalizer.GenerateEquivalentForms(canonicalNormalized, item.System);

        // Check if any user form matches any canonical form
        var isMatch = userForms.Any(uf => canonicalForms.Any(cf => 
            string.Equals(uf, cf, StringComparison.OrdinalIgnoreCase)));

        if (isMatch && !UsesWrongNumberSystem(normalized, canonicalNormalized, item.System))
        {
            return new GradeResult(
                IsCorrect: true,
                Verdict: "정확해요!",
                ErrorClass: null,
                CanonicalAnswer: item.CanonicalAnswer,
                UserAnswer: userAnswer.Trim(),
                Tip: null
            );
        }

        // Check acceptable alternates
        foreach (var alternate in item.AcceptableAlternates)
        {
            var alternateNormalized = NormalizeAnswer(alternate);
            var alternateForms = KoreanNumberNormalizer.GenerateEquivalentForms(alternateNormalized, item.System);
            if (userForms.Any(uf => alternateForms.Any(af => 
                string.Equals(uf, af, StringComparison.OrdinalIgnoreCase)))
                && !UsesWrongNumberSystem(normalized, alternateNormalized, item.System))
            {
                return new GradeResult(
                    IsCorrect: true,
                    Verdict: "정확해요!",
                    ErrorClass: null,
                    CanonicalAnswer: item.CanonicalAnswer,
                    UserAnswer: userAnswer.Trim(),
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
            UserAnswer: userAnswer.Trim(),
            Tip: tip
        );
    }

    private string KoreanToDigitString(string text, NumberSystem system)
    {
        // Use the normalizer's conversion in the item's system so Sino input
        // can't bridge to a Native item (or vice versa).
        var forms = KoreanNumberNormalizer.GenerateEquivalentForms(text, system);
        // Find the first form that contains only digits
        return forms.FirstOrDefault(f => Regex.IsMatch(f, @"^\d+$")) ?? text;
    }

    private string NormalizeAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return "";

        // Use KoreanNumberNormalizer for whitespace normalization
        var normalized = KoreanNumberNormalizer.NormalizeWhitespace(answer);

        // Strip internal commas from numbers (narrow rule: 15,000 → 15000)
        normalized = StripInternalCommas(normalized);

        // Strip trailing punctuation (narrow rule: ., ,, ?, !, ?, ?, !)
        normalized = StripTrailingPunctuation(normalized);

        // Convert full-width digits to half-width
        normalized = ConvertFullWidthToHalfWidth(normalized);

        // Lowercase for romanization comparison
        normalized = normalized.ToLowerInvariant();

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

    private string StripInternalCommas(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        // Narrow permissive rule: strip commas ONLY between digits (e.g., 15,000 → 15000)
        // Regex pattern: (?<=\d),(?=\d) matches commas that have a digit before AND after
        // This avoids affecting Korean text or other uses of commas
        return Regex.Replace(input, @"(?<=\d),(?=\d)", "");
    }

    private string StripTrailingPunctuation(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        // Narrow permissive rule: strip trailing ASCII and fullwidth punctuation
        // . , ? ! 。 ？ ！ (but NOTHING else — no Levenshtein, no whitespace gymnastics beyond what's already in NormalizeWhitespace)
        return input.TrimEnd('.', ',', '?', '!', '。', '？', '！');
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

    /// <summary>
    /// Detects when the user has typed digits using the wrong number system for the item.
    /// E.g., item.System=Native, canonical "쉰여덟 잔", user typed "오십팔 잔" (Sino digits).
    /// Both normalize to "58 잔" via ConvertKoreanToDigits, but the user used the wrong system.
    /// Returns true if user input contains opposite-system digit words that the canonical does NOT.
    /// </summary>
    private bool UsesWrongNumberSystem(string userText, string canonicalText, NumberSystem system)
    {
        // Mixed/Lexical accept either system — no guard.
        if (system != NumberSystem.Native && system != NumberSystem.Sino)
            return false;

        // Sino-exclusive digits (1-9). Place words 십/백/천/만 are shared with Native context (e.g. "백" alone for 100).
        var sinoExclusiveDigits = new[] { "일", "이", "삼", "사", "오", "육", "칠", "팔", "구" };
        // Native-exclusive number words (full forms + sound-changed forms + tens 10-90).
        var nativeExclusiveWords = new[]
        {
            "하나", "둘", "셋", "넷", "다섯", "여섯", "일곱", "여덟", "아홉",
            "한", "두", "세", "네",
            "열", "스물", "스무", "서른", "마흔", "쉰", "예순", "일흔", "여든", "아흔"
        };

        if (system == NumberSystem.Native)
        {
            // User used Sino digits where Native is required, and canonical doesn't have them
            var userHasSino = sinoExclusiveDigits.Any(d => userText.Contains(d));
            var canonicalHasSino = sinoExclusiveDigits.Any(d => canonicalText.Contains(d));
            if (userHasSino && !canonicalHasSino)
                return true;
        }
        else // Sino
        {
            // User used Native words where Sino is required, and canonical doesn't have them
            var userHasNative = nativeExclusiveWords.Any(n => userText.Contains(n));
            var canonicalHasNative = nativeExclusiveWords.Any(n => canonicalText.Contains(n));
            if (userHasNative && !canonicalHasNative)
                return true;
        }

        return false;
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
