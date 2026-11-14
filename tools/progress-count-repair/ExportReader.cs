using System.Globalization;
using System.Text.Json;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Tools.ProgressCountRepair;

/// <summary>
/// Reads the JSON arrays a database export writes (PostgreSQL <c>json_agg</c>, SQLite <c>-json</c>)
/// column by column: the model's own JSON attributes skip CreatedAt and UpdatedAt, and the two
/// databases write timestamps and booleans differently.
/// </summary>
public static class ExportReader
{
    /// <summary>Progress rows, with each row's UpdatedAt exactly as the database wrote it.</summary>
    public static (List<VocabularyProgress> Rows, Dictionary<string, string> RawUpdatedAt) ReadProgress(string json)
    {
        var rows = new List<VocabularyProgress>();
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in Items(json))
        {
            var row = new VocabularyProgress
            {
                Id = Text(item, "Id")!,
                VocabularyWordId = Text(item, "VocabularyWordId")!,
                UserId = Text(item, "UserId")!,
                MasteryScore = Float(item, "MasteryScore"),
                TotalAttempts = Int(item, "TotalAttempts"),
                CorrectAttempts = Int(item, "CorrectAttempts"),
                CurrentStreak = Float(item, "CurrentStreak"),
                ProductionInStreak = Int(item, "ProductionInStreak"),
                QuizRecognitionDemonstrations = Int(item, "QuizRecognitionDemonstrations"),
                QuizProductionDemonstrations = Int(item, "QuizProductionDemonstrations"),
                RecognitionAttempts = Int(item, "RecognitionAttempts"),
                RecognitionCorrect = Int(item, "RecognitionCorrect"),
                ProductionAttempts = Int(item, "ProductionAttempts"),
                ProductionCorrect = Int(item, "ProductionCorrect"),
                ApplicationAttempts = Int(item, "ApplicationAttempts"),
                ApplicationCorrect = Int(item, "ApplicationCorrect"),
                CurrentPhase = (LearningPhase)Int(item, "CurrentPhase"),
                NextReviewDate = NullableDate(item, "NextReviewDate"),
                ReviewInterval = Int(item, "ReviewInterval"),
                EaseFactor = Float(item, "EaseFactor"),
                MultipleChoiceCorrect = Int(item, "MultipleChoiceCorrect"),
                TextEntryCorrect = Int(item, "TextEntryCorrect"),
                IsPromoted = Bool(item, "IsPromoted"),
                IsCompleted = Bool(item, "IsCompleted"),
                IsUserDeclared = Bool(item, "IsUserDeclared"),
                UserDeclaredAt = NullableDate(item, "UserDeclaredAt"),
                VerificationState = (VerificationStatus)Int(item, "VerificationState"),
                FirstSeenAt = Date(item, "FirstSeenAt"),
                LastPracticedAt = Date(item, "LastPracticedAt"),
                MasteredAt = NullableDate(item, "MasteredAt"),
                LastExposedAt = NullableDate(item, "LastExposedAt"),
                ExposureCount = Int(item, "ExposureCount"),
                CreatedAt = Date(item, "CreatedAt"),
                UpdatedAt = Date(item, "UpdatedAt"),
            };
            rows.Add(row);
            raw[row.Id] = Text(item, "UpdatedAt")!;
        }
        return (rows, raw);
    }

    public static List<VocabularyLearningContext> ReadContexts(string json) =>
        Items(json).Select(item => new VocabularyLearningContext
        {
            Id = Text(item, "Id")!,
            VocabularyProgressId = Text(item, "VocabularyProgressId")!,
            LearningResourceId = Text(item, "LearningResourceId"),
            Activity = Text(item, "Activity") ?? string.Empty,
            InputMode = Text(item, "InputMode") ?? string.Empty,
            WasCorrect = Bool(item, "WasCorrect"),
            DifficultyScore = Float(item, "DifficultyScore"),
            ResponseTimeMs = Int(item, "ResponseTimeMs"),
            UserConfidence = item.TryGetProperty("UserConfidence", out var confidence)
                && confidence.ValueKind == JsonValueKind.Number ? confidence.GetSingle() : null,
            ContextType = Text(item, "ContextType"),
            UserInput = Text(item, "UserInput"),
            ExpectedAnswer = Text(item, "ExpectedAnswer"),
            LearnedAt = Date(item, "LearnedAt"),
            CorrectAnswersInContext = Int(item, "CorrectAnswersInContext"),
            CreatedAt = Date(item, "CreatedAt"),
            UpdatedAt = Date(item, "UpdatedAt"),
        }).ToList();

    /// <summary>
    /// A timestamp as UTC: PostgreSQL's <c>2026-06-22T20:54:34.54512+00:00</c> or SQLite's
    /// <c>2026-06-22 20:54:34.54512</c> (stored as UTC); <c>-infinity</c> is <see cref="DateTime.MinValue"/>.
    /// </summary>
    public static DateTime ParseDate(string text)
    {
        if (text == "-infinity")
            return DateTime.MinValue;
        if (text == "infinity")
            return DateTime.MaxValue;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var stamp)
            ? stamp.UtcDateTime
            : throw new FormatException($"Unreadable timestamp '{text}'.");
    }

    private static IEnumerable<JsonElement> Items(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (document.RootElement.ValueKind == JsonValueKind.Null)
            return [];
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static int Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static float Float(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetSingle() : 0f;

    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetInt32() != 0,
            _ => false,
        };

    private static DateTime Date(JsonElement item, string name) =>
        NullableDate(item, name) ?? throw new FormatException($"{name} is missing.");

    private static DateTime? NullableDate(JsonElement item, string name) =>
        Text(item, name) is { } text ? ParseDate(text) : null;
}
