using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

public class KoreanNumberItemGenerator : INumberItemGenerator
{
    private readonly ILogger<KoreanNumberItemGenerator> _logger;
    
    public string LanguageCode => "ko";

    public KoreanNumberItemGenerator(ILogger<KoreanNumberItemGenerator> logger)
    {
        _logger = logger;
    }

    private static readonly Dictionary<string, string[]> Phase1Counters = new()
    {
        { "잔", new[] { "잔", "cups/glasses" } },
        { "개", new[] { "개", "generic objects" } },
        { "명", new[] { "명", "people" } },
        { "마리", new[] { "마리", "animals" } },
        { "권", new[] { "권", "books" } },
        { "살", new[] { "살", "years old" } }
    };

    public NumberItem GenerateItem(NumberItemRequest request)
    {
        var random = request.RandomSeed.HasValue 
            ? new Random(request.RandomSeed.Value) 
            : new Random();

        // Disambiguate sub-mode is cross-context (tests contrast across different contexts)
        if (request.SubModeCode == "Disambiguate")
        {
            return GenerateDisambiguateItem(request, random);
        }

        // ListenAndPlace sub-mode is Time context only (audio → digital time matching)
        if (request.SubModeCode == "ListenAndPlace")
        {
            return GenerateListenAndPlaceItem(request, random);
        }

        return request.ContextCode switch
        {
            "Counting" => GenerateCountingItem(request, random),
            "Time" => GenerateTimeItem(request, random),
            "Age" => GenerateAgeItem(request, random),
            "Money" => GenerateMoneyItem(request, random),
            "Date" => GenerateDateItem(request, random),
            "Ordinal" => GenerateOrdinalItem(request, random),
            _ => throw new ArgumentException($"Unknown context: {request.ContextCode}")
        };
    }

    private NumberItem GenerateCountingItem(NumberItemRequest request, Random random)
    {
        // Phase 1: 1-99 only
        var value = random.Next(1, 100);
        var bucket = GetBucket(value);

        // Pick counter
        var counterKey = request.CounterId ?? Phase1Counters.Keys.ToArray()[random.Next(Phase1Counters.Count)];
        var counterInfo = Phase1Counters[counterKey];
        var counterText = counterInfo[0];

        // Generate native Korean numeral with sound changes
        var nativeNumeral = ConvertToNative(value, beforeCounter: true);
        var canonicalAnswer = $"{nativeNumeral} {counterText}";

        // For TapTheCounter: show sentence frame with blank counter
        if (request.SubModeCode == "TapTheCounter")
        {
            // Noun cue based on counter type
            var nounCue = counterKey switch
            {
                "잔" => "coffee",
                "개" => "items",
                "명" => "people",
                "마리" => "animals",
                "권" => "books",
                _ => "things"
            };
            
            // Display prompt: sentence frame with blank
            var displayPrompt = $"{nativeNumeral} ___";
            
            // Pick 2 distractors from the other 4 counters
            var allCounters = Phase1Counters.Keys.ToList();
            var distractors = allCounters.Where(c => c != counterKey).OrderBy(_ => random.Next()).Take(2).ToList();
            var choices = new List<string> { counterText };
            choices.AddRange(distractors.Select(d => Phase1Counters[d][0]));
            
            // Shuffle choices
            choices = choices.OrderBy(_ => random.Next()).ToList();
            
            var hints = new List<string>
            {
                $"Think about what you're counting: {nounCue}",
                $"{counterInfo[1]}"
            };
            
            return new NumberItem(
                Id: Guid.NewGuid(),
                ContextCode: "Counting",
                SubModeCode: request.SubModeCode,
                CounterId: counterKey,
                CounterText: counterText,
                System: NumberSystem.Native,
                Bucket: bucket,
                DigitValue: value,
                CanonicalAnswer: canonicalAnswer,
                DisplayPrompt: displayPrompt,
                AudioCue: canonicalAnswer, // TTS plays after answer
                Hints: hints,
                AcceptableAlternates: new List<string>(),
                ErrorClassHints: null,
                NounCue: nounCue,
                CounterChoices: choices
            );
        }
        
        // For ListenAndType: audio plays Korean, user types digit
        // For ReadAndProduce: prompt shows digit + counter image, user types Hangul
        var regularDisplayPrompt = request.SubModeCode == "ListenAndType" 
            ? "" // Audio-only prompt
            : $"{value} {counterText}";
        
        var regularAudioCue = request.SubModeCode == "ListenAndType"
            ? canonicalAnswer
            : ""; // No audio for ReadAndProduce

        var regularHints = new List<string>
        {
            $"Native numbers (한, 두, 세) are used with counters like {counterText}",
            $"{counterInfo[1]}"
        };

        var alternates = new List<string>();
        // Accept with or without space
        alternates.Add($"{nativeNumeral}{counterText}");
        if (canonicalAnswer.Contains(" "))
            alternates.Add(canonicalAnswer.Replace(" ", ""));

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Counting",
            SubModeCode: request.SubModeCode,
            CounterId: counterKey,
            CounterText: counterText,
            System: NumberSystem.Native,
            Bucket: bucket,
            DigitValue: value,
            CanonicalAnswer: canonicalAnswer,
            DisplayPrompt: regularDisplayPrompt,
            AudioCue: regularAudioCue,
            Hints: regularHints,
            AcceptableAlternates: alternates
        );
    }

    private NumberItem GenerateTimeItem(NumberItemRequest request, Random random)
    {
        // Phase 1: 12-hour only, H:MM where H in 1-12, MM in {00, 15, 30, 45}
        var hour = random.Next(1, 13); // 1-12
        var minuteOptions = new[] { 0, 15, 30, 45 };
        var minute = minuteOptions[random.Next(minuteOptions.Length)];

        // Hour uses NATIVE (with sound change for 12)
        var hourNative = ConvertToNative(hour, beforeCounter: true);
        var hourText = $"{hourNative} 시";

        // Minute uses SINO
        var minuteSino = minute == 0 ? "" : $" {ConvertToSino(minute)} 분";
        var canonicalAnswer = minute == 0 ? hourText : $"{hourText}{minuteSino}";

        var displayTime = $"{hour}:{minute:D2}";
        var displayPrompt = request.SubModeCode == "ListenAndType"
            ? ""
            : displayTime;

        var audioCue = request.SubModeCode == "ListenAndType"
            ? canonicalAnswer
            : "";

        var hints = new List<string>
        {
            "Hours use Native numbers (한 시, 두 시, 세 시...)",
            "Minutes use Sino numbers (영 분, 십오 분, 삼십 분...)"
        };

        var alternates = new List<string>();
        // Accept H:MM format for ListenAndType
        if (request.SubModeCode == "ListenAndType")
        {
            alternates.Add(displayTime);
            alternates.Add($"{hour}:{minute}");
        }
        else
        {
            // Accept variations in spacing
            alternates.Add(canonicalAnswer.Replace(" ", ""));
        }

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Time",
            SubModeCode: request.SubModeCode,
            CounterId: null,
            CounterText: null,
            System: NumberSystem.Mixed,
            Bucket: GetBucket(hour),
            DigitValue: hour * 100 + minute,
            CanonicalAnswer: canonicalAnswer,
            DisplayPrompt: displayPrompt,
            AudioCue: audioCue,
            Hints: hints,
            AcceptableAlternates: alternates
        );
    }

    private NumberItem GenerateAgeItem(NumberItemRequest request, Random random)
    {
        // Skew toward 5-60 for realism, but allow 1-99
        var value = random.Next(0, 100) < 80 
            ? random.Next(5, 61)  // 80% chance: 5-60
            : random.Next(1, 100); // 20% chance: 1-99

        var bucket = GetBucket(value);

        // Generate native with sound change for 살
        var nativeNumeral = ConvertToNative(value, beforeCounter: true, counterIs살: true);
        var canonicalAnswer = $"{nativeNumeral} 살";

        var displayPrompt = request.SubModeCode == "ListenAndType"
            ? ""
            : $"{value}살";

        var audioCue = request.SubModeCode == "ListenAndType"
            ? canonicalAnswer
            : "";

        var hints = new List<string>
        {
            "Age uses Native numbers + 살",
            "Sound changes: 스물 → 스무 (only at exactly 20)"
        };

        var alternates = new List<string>
        {
            $"{nativeNumeral}살", // No space
            canonicalAnswer.Replace(" ", "") // No space version
        };

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Age",
            SubModeCode: request.SubModeCode,
            CounterId: "살",
            CounterText: "살",
            System: NumberSystem.Native,
            Bucket: bucket,
            DigitValue: value,
            CanonicalAnswer: canonicalAnswer,
            DisplayPrompt: displayPrompt,
            AudioCue: audioCue,
            Hints: hints,
            AcceptableAlternates: alternates
        );
    }

    private NumberItem GenerateMoneyItem(NumberItemRequest request, Random random)
    {
        // Price range weights (from seed contextNotes.Money.ranges)
        // Small: 100-3000 (60%), Medium: 10k-50k (30%), Large: 100k-1M (10%)
        int value;
        string context;
        
        var rangeRoll = random.Next(100);
        if (rangeRoll < 60)
        {
            // Small purchase (100-3000)
            value = random.Next(0, 100) switch
            {
                < 30 => 1000,      // 천 원 (coffee)
                < 50 => 3000,      // 삼천 원 (snack)
                < 70 => 2000,      // 이천 원
                < 90 => 5000,      // 오천 원
                _ => random.Next(1, 10) * 1000 // 1k-9k random
            };
            context = "small purchase";
        }
        else if (rangeRoll < 90)
        {
            // Medium purchase (10k-50k)
            value = random.Next(0, 100) switch
            {
                < 40 => 10000,     // 만 원 (lunch)
                < 60 => 15000,     // 만 오천 원 (meal)
                < 80 => 20000,     // 이만 원
                _ => random.Next(3, 6) * 10000 // 30k-50k
            };
            context = "meal/shopping";
        }
        else
        {
            // Large purchase (100k-1M)
            value = random.Next(0, 100) switch
            {
                < 60 => 100000,    // 십만 원 (shopping)
                < 85 => 500000,    // 오십만 원
                _ => 1000000       // 백만 원 (rent)
            };
            context = "shopping/rent";
        }

        var bucket = GetBucket(value);
        var sinoReading = ConvertToSinoMoney(value);
        var canonicalAnswer = $"{sinoReading} 원";
        
        _logger.LogTrace("📐 Generated Money item: context={Context} digit={Digit} answer={Answer}",
            context, value, canonicalAnswer);

        // Display as digit with comma grouping (Korean style 4-digit groups when over 10k)
        var displayValue = value >= 10000 
            ? value.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"))
            : value.ToString();
        var displayPrompt = request.SubModeCode == "ListenAndType"
            ? ""
            : $"{displayValue}원";

        var audioCue = request.SubModeCode == "ListenAndType"
            ? canonicalAnswer
            : "";

        var hints = new List<string>
        {
            "Korean uses Sino numbers for money",
            "만 (10,000) and 억 (100,000,000) group by 4 digits, not 3",
            $"Context: {context}"
        };

        var alternates = new List<string>
        {
            $"{sinoReading}원", // No space
        };

        // Error-class hints for Phase 3 Insights
        var errorHints = new Dictionary<string, string>();
        if (value >= 10000 && value < 100000000)
        {
            errorHints["likely_error"] = "place_value_grouping_4digit_vs_3digit";
            errorHints["hint"] = "Korean groups by 만 (10,000) not thousand";
        }

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Money",
            SubModeCode: request.SubModeCode,
            CounterId: "원",
            CounterText: "원",
            System: NumberSystem.Sino,
            Bucket: bucket,
            DigitValue: value,
            CanonicalAnswer: canonicalAnswer,
            DisplayPrompt: displayPrompt,
            AudioCue: audioCue,
            Hints: hints,
            AcceptableAlternates: alternates,
            ErrorClassHints: errorHints.Count > 0 ? errorHints : null
        );
    }

    private NumberItem GenerateDateItem(NumberItemRequest request, Random random)
    {
        // Generate valid date (avoid Feb 30, etc.)
        var month = random.Next(1, 13);
        var maxDay = month switch
        {
            2 => 28,  // Simplified: no leap year handling
            4 or 6 or 9 or 11 => 30,
            _ => 31
        };
        var day = random.Next(1, maxDay + 1);

        // Optional: include year at higher difficulty (20% chance)
        var includeYear = random.Next(100) < 20;
        var year = includeYear ? 2026 : 0;

        // Month irregular handling (from seed contextNotes.Date.irregularMonths)
        var monthReading = month switch
        {
            1 => "일월",
            2 => "이월",
            3 => "삼월",
            4 => "사월",
            5 => "오월",
            6 => "유월",   // IRREGULAR: 유월 NOT 육월
            7 => "칠월",
            8 => "팔월",
            9 => "구월",
            10 => "시월",  // IRREGULAR: 시월 NOT 십월
            11 => "십일월",
            12 => "십이월",
            _ => throw new ArgumentException($"Invalid month: {month}")
        };

        var dayReading = ConvertToSino(day) + " 일";
        var yearReading = includeYear ? ConvertToSinoYear(year) + " 년 " : "";
        var canonicalAnswer = $"{yearReading}{monthReading} {dayReading}";
        
        _logger.LogTrace("📐 Generated Date item: context=Date year={Year} month={Month} day={Day} answer={Answer}",
            includeYear ? year : 0, month, day, canonicalAnswer);

        var displayPrompt = request.SubModeCode == "ListenAndType"
            ? ""
            : (includeYear ? $"{year}-{month:D2}-{day:D2}" : $"{month}월 {day}일");

        var audioCue = request.SubModeCode == "ListenAndType"
            ? canonicalAnswer
            : "";

        var hints = new List<string>
        {
            "Dates use Sino numbers for both month and day",
            "Month 6 is 유월 (NOT 육월), month 10 is 시월 (NOT 십월)"
        };

        var alternates = new List<string>
        {
            canonicalAnswer.Replace(" ", ""), // No spaces
            $"{monthReading}{dayReading}",   // Without year if included
        };

        // Error-class hints for Phase 3 Insights
        var errorHints = new Dictionary<string, string>();
        if (month == 6 || month == 10)
        {
            errorHints["likely_error"] = "wrong_form_for_month_6_or_10";
            errorHints["hint"] = month == 6 
                ? "June is 유월 (irregular), not 육월" 
                : "October is 시월 (irregular), not 십월";
        }

        var bucket = includeYear ? "with_year" : "month_day_only";

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Date",
            SubModeCode: request.SubModeCode,
            CounterId: null,
            CounterText: null,
            System: NumberSystem.Sino,
            Bucket: bucket,
            DigitValue: month * 100 + day,
            CanonicalAnswer: canonicalAnswer,
            DisplayPrompt: displayPrompt,
            AudioCue: audioCue,
            Hints: hints,
            AcceptableAlternates: alternates,
            ErrorClassHints: errorHints.Count > 0 ? errorHints : null
        );
    }

    private NumberItem GenerateOrdinalItem(NumberItemRequest request, Random random)
    {
        // Phase 1: 1-10 only for ordinals
        var value = random.Next(1, 11);

        // Two patterns: 째 (ranking) vs 번째 (occurrence)
        // 60% ranking, 40% occurrence
        var isRanking = random.Next(100) < 60;
        var pattern = isRanking ? "째" : "번째";
        var context = isRanking ? "ranking" : "occurrence";

        // Generate native Korean ordinal with sound changes
        string ordinalReading;
        if (isRanking)
        {
            // Native + 째 (첫째, 둘째, 셋째...)
            // Special case: 첫째 NOT 하나째
            ordinalReading = value switch
            {
                1 => "첫째",
                2 => "둘째",
                3 => "셋째",
                4 => "넷째",
                5 => "다섯째",
                6 => "여섯째",
                7 => "일곱째",
                8 => "여덟째",
                9 => "아홉째",
                10 => "열째",
                _ => throw new ArgumentException($"Invalid ordinal: {value}")
            };
        }
        else
        {
            // Native + 번째 (첫 번째, 두 번째, 세 번째...)
            // Note: 첫 번째 is SPACED
            var nativeBase = value switch
            {
                1 => "첫",  // Special case: 첫 NOT 하나
                2 => "두",
                3 => "세",
                4 => "네",
                5 => "다섯",
                6 => "여섯",
                7 => "일곱",
                8 => "여덟",
                9 => "아홉",
                10 => "열",
                _ => throw new ArgumentException($"Invalid ordinal: {value}")
            };
            ordinalReading = $"{nativeBase} 번째";
        }

        var canonicalAnswer = ordinalReading;
        
        _logger.LogTrace("📐 Generated Ordinal item: context={Context} digit={Digit} pattern={Pattern} answer={Answer}",
            context, value, pattern, canonicalAnswer);

        // Display prompt: use English ordinal suffix or Korean context
        var displayPrompt = request.SubModeCode == "ListenAndType"
            ? ""
            : (isRanking 
                ? $"{value}째 (rank)"  // e.g., "2째 (rank)" → "둘째"
                : $"{value}번째 (occurrence)"); // e.g., "3번째 (occurrence)" → "세 번째"

        var audioCue = request.SubModeCode == "ListenAndType"
            ? canonicalAnswer
            : "";

        var hints = new List<string>
        {
            $"Ordinal uses Native numbers with {pattern}",
            isRanking 
                ? "째 is used for ranking/birth order (첫째 아이 = first child)"
                : "번째 is used for occurrences/'Nth time' (첫 번째 방문 = first visit)",
            "Special case: 첫째 and 첫 번째 (NOT 하나째 or 하나 번째)"
        };

        var alternates = new List<string>();
        if (!isRanking)
        {
            // Accept with or without space for 번째 pattern
            alternates.Add(ordinalReading.Replace(" ", ""));
        }

        // Error-class hints for Phase 3 Insights
        var errorHints = new Dictionary<string, string>
        {
            ["likely_error"] = "rank_vs_occurrence_confusion",
            ["hint"] = isRanking
                ? "째 is for ranking (첫째 자녀 = first child)"
                : "번째 is for occurrences (첫 번째 시도 = first attempt)",
            ["pattern"] = pattern
        };

        var bucket = isRanking ? "rank" : "occurrence";

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Ordinal",
            SubModeCode: request.SubModeCode,
            CounterId: pattern,
            CounterText: pattern,
            System: NumberSystem.Native,
            Bucket: bucket,
            DigitValue: value,
            CanonicalAnswer: canonicalAnswer,
            DisplayPrompt: displayPrompt,
            AudioCue: audioCue,
            Hints: hints,
            AcceptableAlternates: alternates,
            ErrorClassHints: errorHints
        );
    }

    private string ConvertToNative(int value, bool beforeCounter = false, bool counterIs살 = false)
    {
        if (value == 0) return "영";
        if (value > 99) throw new ArgumentException("Phase 1 only supports 1-99 for Native");

        var tens = value / 10;
        var ones = value % 10;

        var result = "";

        // Tens place
        if (tens > 0)
        {
            result += tens switch
            {
                1 => "열",
                2 => beforeCounter && ones == 0 ? "스무" : "스물", // 스물 → 스무 ONLY at exactly 20
                3 => "서른",
                4 => "마흔",
                5 => "쉰",
                6 => "예순",
                7 => "일흔",
                8 => "여든",
                9 => "아흔",
                _ => throw new ArgumentException($"Invalid tens: {tens}")
            };
        }

        // Ones place with sound changes
        // Sound changes apply ONLY before counter AND when standalone (not compounded with tens)
        if (ones > 0)
        {
            // Sound changes DON'T apply when ones are part of a compound (21-29, 31-39, etc.)
            bool applyCounterSoundChange = beforeCounter && tens == 0;
            
            result += ones switch
            {
                1 => applyCounterSoundChange ? "한" : "하나",
                2 => applyCounterSoundChange ? "두" : "둘",
                3 => applyCounterSoundChange ? "세" : "셋",
                4 => applyCounterSoundChange ? "네" : "넷",
                5 => "다섯",
                6 => "여섯",
                7 => "일곱",
                8 => "여덟",
                9 => "아홉",
                _ => throw new ArgumentException($"Invalid ones: {ones}")
            };
        }

        return result;
    }

    private string ConvertToSino(int value)
    {
        if (value == 0) return "영";
        if (value > 9999) throw new ArgumentException("Phase 1 only supports up to 9999 for Sino");

        var result = "";
        var thousands = value / 1000;
        var hundreds = (value % 1000) / 100;
        var tens = (value % 100) / 10;
        var ones = value % 10;

        if (thousands > 0)
        {
            if (thousands > 1) result += GetSinoDigit(thousands);
            result += "천";
        }

        if (hundreds > 0)
        {
            if (hundreds > 1) result += GetSinoDigit(hundreds);
            result += "백";
        }

        if (tens > 0)
        {
            if (tens > 1) result += GetSinoDigit(tens);
            result += "십";
        }

        if (ones > 0)
        {
            result += GetSinoDigit(ones);
        }

        return result;
    }

    private string ConvertToSinoMoney(int value)
    {
        if (value == 0) return "영";
        
        // Korean money uses 만 (10,000) and 억 (100,000,000) grouping
        var parts = new List<string>();
        
        var eok = value / 100000000;  // 억 (hundred million)
        var man = (value % 100000000) / 10000;  // 만 (ten thousand)
        var remainder = value % 10000;
        
        if (eok > 0)
        {
            parts.Add(ConvertToSino(eok) + "억");
        }
        
        if (man > 0)
        {
            if (man == 1)
                parts.Add("만");
            else
                parts.Add(ConvertToSino(man) + "만");
        }
        
        if (remainder > 0)
        {
            parts.Add(ConvertToSino(remainder));
        }
        
        return string.Join(" ", parts);
    }

    private string ConvertToSinoYear(int year)
    {
        // Year is read digit-by-digit in Korean
        // 2026 → 이천이십육 (not 이영이육)
        var digits = year.ToString();
        var result = "";
        
        for (int i = 0; i < digits.Length; i++)
        {
            var digit = int.Parse(digits[i].ToString());
            var placeValue = digits.Length - i - 1;
            
            if (digit == 0)
            {
                result += "영";
                continue;
            }
            
            // For thousands, hundreds, tens
            if (placeValue == 3) // thousands
            {
                if (digit > 1) result += GetSinoDigit(digit);
                result += "천";
            }
            else if (placeValue == 2) // hundreds
            {
                if (digit > 1) result += GetSinoDigit(digit);
                result += "백";
            }
            else if (placeValue == 1) // tens
            {
                if (digit > 1) result += GetSinoDigit(digit);
                result += "십";
            }
            else // ones
            {
                result += GetSinoDigit(digit);
            }
        }
        
        return result;
    }

    private string GetSinoDigit(int digit)
    {
        return digit switch
        {
            0 => "영",
            1 => "일",
            2 => "이",
            3 => "삼",
            4 => "사",
            5 => "오",
            6 => "육",
            7 => "칠",
            8 => "팔",
            9 => "구",
            _ => throw new ArgumentException($"Invalid digit: {digit}")
        };
    }

    private string GetBucket(int value)
    {
        if (value <= 10) return "1-10";
        if (value <= 99) return "11-99";
        if (value <= 999) return "100-999";
        return "1000+";
    }

    private NumberItem GenerateListenAndPlaceItem(NumberItemRequest request, Random random)
    {
        // Audio-to-digital-time matching (Time context only, Phase 2)
        // Matches seed: lib/content/numbers/ko.json -> listenAndPlaceItems
        var items = new[]
        {
            new { AudioText = "세 시 사십오 분", CorrectAnswer = "3:45", Distractors = new[] { "3:15", "9:45" } },
            new { AudioText = "두 시 십 분", CorrectAnswer = "2:10", Distractors = new[] { "2:20", "12:10" } },
            new { AudioText = "일곱 시 반", CorrectAnswer = "7:30", Distractors = new[] { "7:00", "6:30" } },
            new { AudioText = "다섯 시", CorrectAnswer = "5:00", Distractors = new[] { "5:30", "15:00" } },
            new { AudioText = "열한 시 이십 분", CorrectAnswer = "11:20", Distractors = new[] { "11:30", "1:20" } },
            new { AudioText = "아홉 시 오십오 분", CorrectAnswer = "9:55", Distractors = new[] { "9:05", "9:50" } },
            new { AudioText = "열두 시", CorrectAnswer = "12:00", Distractors = new[] { "2:00", "12:30" } },
            new { AudioText = "여섯 시 사십 분", CorrectAnswer = "6:40", Distractors = new[] { "6:14", "6:04" } },
            new { AudioText = "한 시 십오 분", CorrectAnswer = "1:15", Distractors = new[] { "1:50", "11:15" } },
            new { AudioText = "네 시 오 분", CorrectAnswer = "4:05", Distractors = new[] { "4:50", "4:15" } }
        };

        var item = items[random.Next(items.Length)];

        // Build 3 choices: correct + 2 distractors, then shuffle
        var choices = new List<string> { item.CorrectAnswer };
        choices.AddRange(item.Distractors);
        choices = choices.OrderBy(_ => random.Next()).ToList();

        _logger.LogTrace("📐 Generated ListenAndPlace item: audio={Audio} correct={Correct} choices={Choices}",
            item.AudioText, item.CorrectAnswer, string.Join(", ", choices));

        var hints = new List<string>
        {
            "Listen carefully for the hour (Native Korean) and minute (Sino Korean)",
            "시 (si) = hour, 분 (bun) = minute, 반 (ban) = 30 minutes",
            "Korean time uses Native for hours (한, 두, 세...) and Sino for minutes (일, 이, 삼...)"
        };

        var errorHints = new Dictionary<string, string>
        {
            ["pattern"] = "time_listening_comprehension",
            ["hint"] = "Focus on hour (Native) vs minute (Sino) system difference",
            ["likely_error"] = "audio_parsing_confusion"
        };

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Time",
            SubModeCode: "ListenAndPlace",
            CounterId: null,
            CounterText: null,
            System: NumberSystem.Mixed, // Hour=Native, Minute=Sino
            Bucket: "listen_place",
            DigitValue: 0, // Not a single digit value
            CanonicalAnswer: item.CorrectAnswer,
            DisplayPrompt: "Listen and select the matching time",
            AudioCue: item.AudioText,
            Hints: hints,
            AcceptableAlternates: new List<string>(), // Exact match only
            ErrorClassHints: errorHints,
            // Reuse CounterChoices for the 3 time options
            CounterChoices: choices
        );
    }

    private NumberItem GenerateDisambiguateItem(NumberItemRequest request, Random random)
    {
        // Paired prompts that test same digit in different contexts (Sino vs Native systems)
        // Matches seed: lib/content/numbers/ko.json -> disambiguatePairs
        var pairs = new[]
        {
            new { Digit = 3, ContextA = "3rd floor", ContextB = "3 floors of stairs", AnswerA = "삼 층", AnswerB = "세 층", 
                  HintA = "Ordinal (which floor?) uses Sino numbers", HintB = "Counting floors uses Native numbers",
                  ChoicesA = new[] { "삼 층", "세 층", "석 층", "삼층" }, ChoicesB = new[] { "세 층", "삼 층", "셋 층", "세층" } },
            
            new { Digit = 3, ContextA = "3 o'clock", ContextB = "3 minutes", AnswerA = "세 시", AnswerB = "삼 분",
                  HintA = "Hours use Native numbers", HintB = "Minutes use Sino numbers",
                  ChoicesA = new[] { "세 시", "삼 시", "석 시", "세시" }, ChoicesB = new[] { "삼 분", "세 분", "석 분", "삼분" } },
            
            new { Digit = 3, ContextA = "3 days (duration)", ContextB = "the 3rd day", AnswerA = "사흘", AnswerB = "셋째 날",
                  HintA = "Duration days 1-4 use special native lexical forms (하루, 이틀, 사흘, 나흘)", HintB = "Ordinal day uses Native + 째",
                  ChoicesA = new[] { "사흘", "삼 일", "세 일", "삼일" }, ChoicesB = new[] { "셋째 날", "삼째 날", "세째 날", "삼 번째 날" } },
            
            new { Digit = 3, ContextA = "3 people", ContextB = "person number 3", AnswerA = "세 명", AnswerB = "삼 번",
                  HintA = "Counting people uses Native + 명", HintB = "Numbering (ID, jersey number) uses Sino + 번",
                  ChoicesA = new[] { "세 명", "삼 명", "석 명", "세명" }, ChoicesB = new[] { "삼 번", "세 번", "석 번", "삼번" } },
            
            new { Digit = 20, ContextA = "20 years old", ContextB = "year 20 (calendar)", AnswerA = "스무 살", AnswerB = "이십 년",
                  HintA = "Age uses Native numbers + 살 (스물 → 스무 sound change)", HintB = "Calendar years use Sino numbers + 년",
                  ChoicesA = new[] { "스무 살", "이십 살", "스물 살", "스무살" }, ChoicesB = new[] { "이십 년", "스무 년", "스물 년", "이십년" } },
            
            new { Digit = 5, ContextA = "5 days (duration)", ContextB = "5th day of the month", AnswerA = "닷새", AnswerB = "오 일",
                  HintA = "Duration days 5-10 have special native forms ending in -새 (닷새, 엿새, 이레...)", HintB = "Calendar dates use Sino numbers + 일",
                  ChoicesA = new[] { "닷새", "오 일", "다섯 일", "오일" }, ChoicesB = new[] { "오 일", "닷새", "다섯 일", "오일" } },
            
            new { Digit = 2, ContextA = "2 bottles of water", ContextB = "February (month 2)", AnswerA = "두 병", AnswerB = "이월",
                  HintA = "Counting bottles uses Native + 병 (두 = sound change of 둘)", HintB = "Months use Sino numbers + 월",
                  ChoicesA = new[] { "두 병", "이 병", "둘 병", "두병" }, ChoicesB = new[] { "이월", "두 월", "둘 월", "이 월" } },
            
            new { Digit = 4, ContextA = "4 o'clock", ContextB = "April (month 4)", AnswerA = "네 시", AnswerB = "사월",
                  HintA = "Hours use Native numbers (네 = sound change of 넷)", HintB = "Months use Sino numbers + 월",
                  ChoicesA = new[] { "네 시", "사 시", "넷 시", "네시" }, ChoicesB = new[] { "사월", "네 월", "넷 월", "사 월" } }
        };

        var pair = pairs[random.Next(pairs.Length)];

        var errorHints = new Dictionary<string, string>
        {
            ["pattern"] = "pattern_disambiguation",
            ["hint"] = "Korean uses different number systems (Native vs Sino) based on context",
            ["likely_error"] = "system_confusion"
        };

        return new NumberItem(
            Id: Guid.NewGuid(),
            ContextCode: "Mixed", // Cross-context comparison
            SubModeCode: "Disambiguate",
            CounterId: null,
            CounterText: null,
            System: NumberSystem.Mixed, // Both systems in play
            Bucket: $"digit_{pair.Digit}",
            DigitValue: pair.Digit,
            CanonicalAnswer: $"{pair.AnswerA} | {pair.AnswerB}", // Both answers for telemetry
            DisplayPrompt: $"{pair.ContextA} vs {pair.ContextB}", // Summary
            AudioCue: "", // Not used in Disambiguate
            Hints: new List<string> { pair.HintA, pair.HintB },
            AcceptableAlternates: new List<string>(), // Not used — grading is exact-match per choice
            ErrorClassHints: errorHints,
            // Paired-prompt fields
            PromptA: pair.ContextA,
            PromptB: pair.ContextB,
            CorrectAnswerA: pair.AnswerA,
            CorrectAnswerB: pair.AnswerB,
            ChoicesA: pair.ChoicesA.OrderBy(_ => random.Next()).ToList(), // Shuffle
            ChoicesB: pair.ChoicesB.OrderBy(_ => random.Next()).ToList(), // Shuffle
            HintA: pair.HintA,
            HintB: pair.HintB,
            AudioCueA: pair.AnswerA, // TTS placeholder
            AudioCueB: pair.AnswerB  // TTS placeholder
        );
    }
}
