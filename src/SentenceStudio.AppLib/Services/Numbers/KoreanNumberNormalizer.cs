using System.Text;
using System.Text.RegularExpressions;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

/// <summary>
/// Normalizes Korean number expressions to support permissive grading.
/// Generates equivalent forms based on the specified number system.
/// Strategy: SYSTEM-AWARE (accepts only forms matching the item's NumberSystem + bare digits as shortcut).
/// </summary>
public static class KoreanNumberNormalizer
{
    // Native counting numbers (1-99)
    private static readonly Dictionary<int, string> NativeNumbers = new()
    {
        { 1, "하나" }, { 2, "둘" }, { 3, "셋" }, { 4, "넷" }, { 5, "다섯" },
        { 6, "여섯" }, { 7, "일곱" }, { 8, "여덟" }, { 9, "아홉" }, { 10, "열" },
        { 20, "스물" }, { 30, "서른" }, { 40, "마흔" }, { 50, "쉰" },
        { 60, "예순" }, { 70, "일흔" }, { 80, "여든" }, { 90, "아흔" }
    };

    // Sound-changed Native forms (before counters)
    private static readonly Dictionary<int, string> NativeNumbersSoundChanged = new()
    {
        { 1, "한" }, { 2, "두" }, { 3, "세" }, { 4, "네" },
        { 20, "스무" }
    };

    // Sino numbers (0-9)
    private static readonly Dictionary<int, string> SinoDigits = new()
    {
        { 0, "영" }, { 1, "일" }, { 2, "이" }, { 3, "삼" }, { 4, "사" },
        { 5, "오" }, { 6, "육" }, { 7, "칠" }, { 8, "팔" }, { 9, "구" }
    };

    // Common counters that might appear in answers
    private static readonly string[] Counters = 
    {
        "개", "명", "마리", "권", "잔", "병", "장", "번", "살", "시", "분", "초",
        "년", "월", "일", "원", "층", "호", "번지", "대"
    };

    // Common Sino compound numbers (for ConvertKoreanToDigits)
    private static readonly Dictionary<string, string> SinoCompounds = new()
    {
        { "천", "1000" },
        { "만", "10000" },
        { "백", "100" },
        { "십", "10" },
        { "이천", "2000" },
        { "삼천", "3000" },
        { "사천", "4000" },
        { "오천", "5000" },
        { "육천", "6000" },
        { "칠천", "7000" },
        { "팔천", "8000" },
        { "구천", "9000" },
        { "이만", "20000" },
        { "삼만", "30000" },
        { "사만", "40000" },
        { "오만", "50000" },
        { "육만", "60000" },
        { "칠만", "70000" },
        { "팔만", "80000" },
        { "구만", "90000" },
        { "십만", "100000" }
    };

    /// <summary>
    /// Normalizes whitespace: collapses all internal whitespace to single space, trims edges.
    /// Treats fullwidth and halfwidth spaces equivalently.
    /// </summary>
    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Replace fullwidth spaces with halfwidth
        text = text.Replace('\u3000', ' ');
        
        // Collapse multiple spaces to single space
        text = Regex.Replace(text, @"\s+", " ");
        
        // Trim edges
        return text.Trim();
    }

    /// <summary>
    /// Generates all linguistically valid forms for a given answer based on the specified number system.
    /// Strategy: Accept bare digits always + forms matching the specified system + whitespace variants.
    /// Rejects wrong number system to enforce pedagogical correctness.
    /// </summary>
    public static List<string> GenerateEquivalentForms(string answer, NumberSystem system)
    {
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Add the original normalized form
        var normalized = NormalizeWhitespace(answer);
        forms.Add(normalized);

        // ALWAYS accept bare digits as a shortcut (regardless of system)
        var withDigits = ConvertKoreanToDigits(normalized);
        if (withDigits != normalized)
            forms.Add(NormalizeWhitespace(withDigits));

        // Generate system-appropriate Korean forms
        switch (system)
        {
            case NumberSystem.Native:
                var withNative = ConvertDigitsToNative(normalized);
                if (withNative != normalized)
                    forms.Add(NormalizeWhitespace(withNative));
                break;
                
            case NumberSystem.Sino:
                var withSino = ConvertDigitsToSino(normalized);
                if (withSino != normalized)
                    forms.Add(NormalizeWhitespace(withSino));
                break;
                
            case NumberSystem.Mixed:
            case NumberSystem.Lexical:
                // For mixed/lexical, accept both forms (backward compatibility)
                var mixedNative = ConvertDigitsToNative(normalized);
                if (mixedNative != normalized)
                    forms.Add(NormalizeWhitespace(mixedNative));
                    
                var mixedSino = ConvertDigitsToSino(normalized);
                if (mixedSino != normalized)
                    forms.Add(NormalizeWhitespace(mixedSino));
                break;
        }

        // Generate variants with/without spaces before counters
        var additionalForms = new List<string>();
        foreach (var form in forms.ToList())
        {
            // For each counter, generate both "5시" and "5 시" variants
            foreach (var counter in Counters)
            {
                if (form.Contains(counter))
                {
                    var withSpace = Regex.Replace(form, $@"(\d+|{string.Join("|", NativeNumbers.Values)}|{string.Join("|", SinoDigits.Values)}){counter}", 
                        m => m.Value.Replace(counter, $" {counter}"));
                    var withoutSpace = form.Replace($" {counter}", counter);
                    
                    additionalForms.Add(NormalizeWhitespace(withSpace));
                    additionalForms.Add(NormalizeWhitespace(withoutSpace));
                }
            }
        }
        
        foreach (var form in additionalForms)
            forms.Add(form);

        return forms.ToList();
    }

    /// <summary>
    /// Converts Arabic digit runs to Native Korean (e.g., "5" → "다섯").
    /// Only handles 1-99 range (Native number system limitation).
    /// </summary>
    private static string ConvertDigitsToNative(string text)
    {
        return Regex.Replace(text, @"\d+", match =>
        {
            if (int.TryParse(match.Value, out var num) && num >= 1 && num <= 99)
            {
                return NumberToNative(num);
            }
            return match.Value;
        });
    }

    /// <summary>
    /// Converts Arabic digit runs to Sino Korean (e.g., "5" → "오").
    /// Handles 0-99 range.
    /// </summary>
    private static string ConvertDigitsToSino(string text)
    {
        return Regex.Replace(text, @"\d+", match =>
        {
            if (int.TryParse(match.Value, out var num) && num >= 0 && num <= 99)
            {
                return NumberToSino(num);
            }
            return match.Value;
        });
    }

    /// <summary>
    /// Converts Korean number words back to Arabic digits (best-effort).
    /// Detects Native/Sino runs and replaces with digits.
    /// </summary>
    private static string ConvertKoreanToDigits(string text)
    {
        // Parse Sino numbers with additive composition (만 오천 = 10000 + 5000 = 15000)
        text = ParseSinoNumbers(text);
        
        // Replace Native numbers
        foreach (var kvp in NativeNumbers.OrderByDescending(x => x.Value.Length))
        {
            text = text.Replace(kvp.Value, kvp.Key.ToString());
        }
        
        // Replace sound-changed Native forms
        foreach (var kvp in NativeNumbersSoundChanged.OrderByDescending(x => x.Value.Length))
        {
            text = text.Replace(kvp.Value, kvp.Key.ToString());
        }
        
        // Replace remaining Sino digits (0-9) that weren't part of larger numbers
        foreach (var kvp in SinoDigits.OrderByDescending(x => x.Value.Length))
        {
            text = text.Replace(kvp.Value, kvp.Key.ToString());
        }
        
        return text;
    }

    /// <summary>
    /// Parses Sino Korean numbers with additive composition.
    /// Examples: 만 오천 = 10000 + 5000 = 15000, 이만 삼천 = 20000 + 3000 = 23000
    /// Algorithm: coefficient * place + coefficient * place + ... + trailing digit
    /// 삼십칠 = 삼(3) * 십(10) + 칠(7) = 37
    /// 만 오천 = 만(10000) + 오(5) * 천(1000) = 15000
    /// </summary>
    private static string ParseSinoNumbers(string input)
    {
        // Sino coefficient digits (일=1, 이=2, etc.)
        var sinoDigits = new Dictionary<string, int>
        {
            { "일", 1 }, { "이", 2 }, { "삼", 3 }, { "사", 4 }, { "오", 5 },
            { "육", 6 }, { "칠", 7 }, { "팔", 8 }, { "구", 9 }
        };

        // Sino place markers and their values
        var sinoPlaces = new Dictionary<string, long>
        {
            { "억", 100000000 },   // 100 million
            { "만", 10000 },        // 10 thousand
            { "천", 1000 },         // thousand
            { "백", 100 },          // hundred
            { "십", 10 }            // ten
        };

        // Pattern to match Sino number sequences including trailing digits
        // Examples: 만, 오천, 삼십칠, 만 오천, 이만 삼천 백오십
        var pattern = @"(?:일|이|삼|사|오|육|칠|팔|구)?(?:억|만|천|백|십)(?:\s*(?:일|이|삼|사|오|육|칠|팔|구)?(?:억|만|천|백|십))*(?:\s*(?:일|이|삼|사|오|육|칠|팔|구))?";
        var matches = Regex.Matches(input, pattern);

        var result = input;
        
        // Process matches from right to left to avoid index shifting
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var sinoText = match.Value.Replace(" ", ""); // Remove spaces for parsing
            
            if (string.IsNullOrEmpty(sinoText))
                continue;

            long totalValue = 0;
            int currentCoefficient = 1; // Default coefficient is 1 (e.g., 만 = 1만)
            int pos = 0;

            while (pos < sinoText.Length)
            {
                bool found = false;

                // Try to match a coefficient digit
                foreach (var kvp in sinoDigits)
                {
                    if (sinoText.Substring(pos).StartsWith(kvp.Key))
                    {
                        // Check if this digit is followed by a place marker
                        bool hasPlaceAfter = false;
                        int nextPos = pos + kvp.Key.Length;
                        if (nextPos < sinoText.Length)
                        {
                            foreach (var place in sinoPlaces.Keys)
                            {
                                if (sinoText.Substring(nextPos).StartsWith(place))
                                {
                                    hasPlaceAfter = true;
                                    break;
                                }
                            }
                        }

                        if (hasPlaceAfter)
                        {
                            // This is a coefficient for the next place marker
                            currentCoefficient = kvp.Value;
                        }
                        else
                        {
                            // This is a trailing digit (ones place)
                            totalValue += kvp.Value;
                        }
                        
                        pos += kvp.Key.Length;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // Try to match a place marker
                    foreach (var kvp in sinoPlaces.OrderByDescending(p => p.Value))
                    {
                        if (sinoText.Substring(pos).StartsWith(kvp.Key))
                        {
                            totalValue += currentCoefficient * kvp.Value;
                            currentCoefficient = 1; // Reset coefficient for next place
                            pos += kvp.Key.Length;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    // Skip unknown character
                    pos++;
                }
            }

            // If we successfully parsed a number, replace it
            if (totalValue > 0)
            {
                result = result.Substring(0, match.Index) + totalValue.ToString() + result.Substring(match.Index + match.Length);
            }
        }

        return result;
    }

    /// <summary>
    /// Converts an integer (1-99) to Native Korean.
    /// </summary>
    private static string NumberToNative(int num)
    {
        if (num < 1 || num > 99)
            return num.ToString();
        
        if (NativeNumbers.ContainsKey(num))
            return NativeNumbers[num];
        
        // Compound (e.g., 23 = 스물셋)
        var tens = (num / 10) * 10;
        var ones = num % 10;
        
        var result = "";
        if (tens > 0 && NativeNumbers.ContainsKey(tens))
            result += NativeNumbers[tens];
        if (ones > 0 && NativeNumbers.ContainsKey(ones))
            result += NativeNumbers[ones];
        
        return result;
    }

    /// <summary>
    /// Converts an integer (0-99) to Sino Korean.
    /// </summary>
    private static string NumberToSino(int num)
    {
        if (num < 0 || num > 99)
            return num.ToString();
        
        if (num < 10)
            return SinoDigits[num];
        
        // Teens and up (e.g., 15 = 십오, 23 = 이십삼)
        var tens = num / 10;
        var ones = num % 10;
        
        var result = "";
        if (tens == 1)
            result = "십";
        else if (tens > 1)
            result = SinoDigits[tens] + "십";
        
        if (ones > 0)
            result += SinoDigits[ones];
        
        return result;
    }
}
