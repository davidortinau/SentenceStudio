namespace SentenceStudio.Services.Numbers;

public record NumberItemRequest(
    string ContextCode,
    string SubModeCode,
    string? Bucket = null,
    string? CounterId = null,
    int Difficulty = 1,
    int? RandomSeed = null
);
