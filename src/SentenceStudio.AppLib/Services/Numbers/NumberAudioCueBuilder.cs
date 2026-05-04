using System.Text;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

/// <summary>
/// Helper for building TTS audio cue text from NumberItems and generating prewarm lists.
/// </summary>
public static class NumberAudioCueBuilder
{
    /// <summary>
    /// Builds the canonical audio cue text for a NumberItem.
    /// Normalizes and trims the item's AudioCue field.
    /// This is the text that will be used as the TTS cache key.
    /// </summary>
    public static string BuildAudioCue(NumberItem item)
    {
        if (string.IsNullOrWhiteSpace(item.AudioCue))
            return string.Empty;

        // Trim and apply Unicode NFC normalization (same as NumberAudioCache.NormalizeText)
        return item.AudioCue.Trim().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Generates the full prewarm list for a given language code.
    /// Phase 1: Korean only (~750 items).
    /// Future phases: Add Japanese, Mandarin, Spanish, etc.
    /// </summary>
    public static List<string> BuildPrewarmList(string languageCode)
    {
        return languageCode.ToLowerInvariant() switch
        {
            "ko" => BuildKoreanPrewarmList(),
            _ => new List<string>() // Other languages not yet implemented
        };
    }

    private static List<string> BuildKoreanPrewarmList()
    {
        var items = new List<string>();
        var generator = new KoreanNumberItemGenerator();

        // Phase 1 prewarm scope (per plan.md):
        // - Native numbers 1–99 standalone
        // - Native + counter: {1–99} × {잔, 개, 명, 마리, 권} → 495 items
        // - Time H:MM where H ∈ 1–12, MM ∈ {00, 15, 30, 45} → 48 items
        // - Age N + 살 for N ∈ 1–99 → 99 items
        // Total ~750 items

        var counters = new[] { "잔", "개", "명", "마리", "권" };
        var timeMinutes = new[] { 0, 15, 30, 45 };

        // 1. Native numbers 1–99 standalone (Counting context, no counter)
        for (int i = 1; i <= 99; i++)
        {
            var item = generator.GenerateItem(new NumberItemRequest(
                ContextCode: "Counting",
                SubModeCode: "ListenAndType",
                Bucket: i <= 10 ? "1-10" : "11-99",
                CounterId: null,
                Difficulty: 1,
                RandomSeed: i
            ));
            items.Add(item.AudioCue);
        }

        // 2. Native + counter: {1–99} × 5 counters
        foreach (var counter in counters)
        {
            for (int i = 1; i <= 99; i++)
            {
                // Generate with counter (we need the counter ID, but for prewarm we just need the audio)
                // The generator will produce the correct audio cue with the counter
                var item = generator.GenerateItem(new NumberItemRequest(
                    ContextCode: "Counting",
                    SubModeCode: "ListenAndType",
                    Bucket: i <= 10 ? "1-10" : "11-99",
                    CounterId: null, // Counter will be embedded in the audio cue
                    Difficulty: 1,
                    RandomSeed: i + counter.GetHashCode()
                ));

                // Build the audio cue manually with counter appended
                // This matches what the generator produces for counting items
                var nativeNum = ConvertToNativeKorean(i);
                items.Add($"{nativeNum} {counter}");
            }
        }

        // 3. Time H:MM where H ∈ 1–12, MM ∈ {00, 15, 30, 45}
        for (int hour = 1; hour <= 12; hour++)
        {
            foreach (var minute in timeMinutes)
            {
                var item = generator.GenerateItem(new NumberItemRequest(
                    ContextCode: "Time",
                    SubModeCode: "ListenAndType",
                    Bucket: hour <= 10 ? "1-10" : "11-99",
                    CounterId: null,
                    Difficulty: 1,
                    RandomSeed: hour * 100 + minute
                ));
                items.Add(item.AudioCue);
            }
        }

        // 4. Age N + 살 for N ∈ 1–99
        for (int age = 1; age <= 99; age++)
        {
            var item = generator.GenerateItem(new NumberItemRequest(
                ContextCode: "Age",
                SubModeCode: "ListenAndType",
                Bucket: age <= 10 ? "1-10" : "11-99",
                CounterId: null,
                Difficulty: 1,
                RandomSeed: age
            ));
            items.Add(item.AudioCue);
        }

        return items.Distinct().ToList();
    }

    /// <summary>
    /// Helper to convert digit to native Korean for counter combinations.
    /// This is a simplified version for prewarm only - the full generator handles sound changes.
    /// </summary>
    private static string ConvertToNativeKorean(int num)
    {
        if (num <= 0 || num > 99)
            return num.ToString();

        // Native Korean numbers 1-10
        var ones = new[] { "", "한", "두", "세", "네", "다섯", "여섯", "일곱", "여덟", "아홉" };
        var tens = new[] { "", "열", "스물", "서른", "마흔", "쉰", "예순", "일흔", "여든", "아흔" };

        if (num <= 10)
        {
            return ones[num];
        }

        var tensDigit = num / 10;
        var onesDigit = num % 10;

        if (onesDigit == 0)
        {
            return tens[tensDigit];
        }

        return tens[tensDigit] + ones[onesDigit];
    }
}
