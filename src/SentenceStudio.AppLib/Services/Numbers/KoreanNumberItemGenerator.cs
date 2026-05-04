using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

public class KoreanNumberItemGenerator : INumberItemGenerator
{
    public string LanguageCode => "ko";

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

        return request.ContextCode switch
        {
            "Counting" => GenerateCountingItem(request, random),
            "Time" => GenerateTimeItem(request, random),
            "Age" => GenerateAgeItem(request, random),
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

        // For ListenAndType: audio plays Korean, user types digit
        // For ReadAndProduce: prompt shows digit + counter image, user types Hangul
        var displayPrompt = request.SubModeCode == "ListenAndType" 
            ? "" // Audio-only prompt
            : $"{value} {counterText}";
        
        var audioCue = request.SubModeCode == "ListenAndType"
            ? canonicalAnswer
            : ""; // No audio for ReadAndProduce

        var hints = new List<string>
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
            DisplayPrompt: displayPrompt,
            AudioCue: audioCue,
            Hints: hints,
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
}
