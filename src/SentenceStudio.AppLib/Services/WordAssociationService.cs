using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public class WordAssociationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WordAssociationService> _logger;
    private readonly AiService _aiService;
    private readonly LearningResourceRepository _resourceRepository;
    private readonly IFileSystemService _fileSystem;

    public WordAssociationService(IServiceProvider serviceProvider, ILogger<WordAssociationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _aiService = serviceProvider.GetRequiredService<AiService>();
        _resourceRepository = serviceProvider.GetRequiredService<LearningResourceRepository>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystemService>();
    }

    /// <summary>
    /// Select vocabulary words for a round from the given Learning Resource(s).
    /// </summary>
    public async Task<List<VocabularyWord>> GetRoundWordsAsync(string resourceIds, int count = 5)
    {
        var ids = resourceIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var allWords = new List<VocabularyWord>();

        foreach (var id in ids)
        {
            var resource = await _resourceRepository.GetResourceAsync(id);
            if (resource?.Vocabulary != null)
                allWords.AddRange(resource.Vocabulary);
        }

        // Deduplicate by Id, shuffle, take requested count
        var distinct = allWords
            .GroupBy(w => w.Id)
            .Select(g => g.First())
            .OrderBy(_ => Random.Shared.Next())
            .Take(count)
            .ToList();

        _logger.LogDebug("GetRoundWordsAsync selected {Count} words from {ResourceCount} resources",
            distinct.Count, ids.Length);

        return distinct;
    }

    /// <summary>
    /// Batch-grade all user clues for a vocabulary word using AI.
    /// </summary>
    public async Task<WordAssociationGradeResponse> GradeCluesAsync(
        VocabularyWord vocabWord,
        List<string> clues,
        string nativeLanguage,
        string targetLanguage)
    {
        if (clues == null || clues.Count == 0)
            return new WordAssociationGradeResponse();

        try
        {
            using var stream = await _fileSystem.OpenAppPackageFileAsync("GradeWordAssociation.scriban-txt");
            using var reader = new StreamReader(stream);
            var templateContent = await reader.ReadToEndAsync();

            var template = Template.Parse(templateContent);
            var prompt = await template.RenderAsync(new
            {
                native_language = nativeLanguage,
                target_language = targetLanguage,
                target_term = vocabWord.TargetLanguageTerm,
                native_term = vocabWord.NativeLanguageTerm,
                entries = clues
            });

            _logger.LogDebug("Grading {ClueCount} clues for word '{Word}'", clues.Count, vocabWord.TargetLanguageTerm);

            var response = await _aiService.SendPrompt<WordAssociationGradeResponse>(prompt);
            return response ?? new WordAssociationGradeResponse();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error grading word association clues for '{Word}'", vocabWord.TargetLanguageTerm);
            return new WordAssociationGradeResponse();
        }
    }

    /// <summary>
    /// Calculate score for graded entries. Related clue = 1pt, related cloze = 2pt.
    /// </summary>
    public static int CalculateScore(List<ClueGrade> grades)
    {
        int score = 0;
        foreach (var g in grades)
        {
            if (g.Related)
                score += g.IsCloze ? 2 : 1;
        }
        return score;
    }

    /// <summary>
    /// Calculate the maximum possible score for graded entries.
    /// </summary>
    public static int CalculateMaxScore(List<ClueGrade> grades)
    {
        int max = 0;
        foreach (var g in grades)
        {
            max += g.IsCloze ? 2 : 1;
        }
        return max;
    }

    /// <summary>
    /// Save a round score to the database.
    /// </summary>
    public async Task SaveRoundScoreAsync(string userId, int roundScore, int totalClues, List<string> wordIds)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var score = new WordAssociationScore
            {
                UserProfileId = userId,
                RoundScore = roundScore,
                TotalClues = totalClues,
                WordCount = wordIds.Count,
                WordIds = string.Join(",", wordIds),
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            db.WordAssociationScores.Add(score);
            await db.SaveChangesAsync();

            _logger.LogDebug("Saved round score {Score} for user {UserId}", roundScore, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving round score");
        }
    }

    /// <summary>
    /// Get the user's all-time high score.
    /// </summary>
    public async Task<int> GetAllTimeHighAsync(string userId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await db.WordAssociationScores
                .Where(s => s.UserProfileId == userId)
                .Select(s => s.RoundScore)
                .DefaultIfEmpty(0)
                .MaxAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all-time high score");
            return 0;
        }
    }

    /// <summary>
    /// Get the user's highest score for the current week (Monday–Sunday).
    /// </summary>
    public async Task<int> GetWeeklyHighAsync(string userId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var today = DateTime.Now.Date;
            var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
            var weekStart = today.AddDays(-daysSinceMonday);

            return await db.WordAssociationScores
                .Where(s => s.UserProfileId == userId && s.CreatedAt >= weekStart)
                .Select(s => s.RoundScore)
                .DefaultIfEmpty(0)
                .MaxAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting weekly high score");
            return 0;
        }
    }

    /// <summary>
    /// Check if a score is a new all-time or weekly high.
    /// Returns: "alltime", "weekly", or null.
    /// </summary>
    public async Task<string?> CheckHighScoreAsync(string userId, int currentScore)
    {
        var allTimeHigh = await GetAllTimeHighAsync(userId);
        if (currentScore > allTimeHigh)
            return "alltime";

        var weeklyHigh = await GetWeeklyHighAsync(userId);
        if (currentScore > weeklyHigh)
            return "weekly";

        return null;
    }
}
