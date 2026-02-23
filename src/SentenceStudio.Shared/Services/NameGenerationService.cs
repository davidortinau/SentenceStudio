using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

public class NameGenerationService
{
    private readonly AiService _aiService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NameGenerationService> _logger;
    private readonly IConnectivityService _connectivity;

    // Hardcoded names by language and gender
    private readonly Dictionary<string, Dictionary<string, string[]>> _hardcodedNames = new()
    {
        ["English"] = new()
        {
            ["masculine"] = new[] { "James", "William", "Benjamin", "Michael" },
            ["feminine"] = new[] { "Emma", "Olivia", "Sophia", "Isabella" }
        },
        ["Korean"] = new()
        {
            ["masculine"] = new[] { "지훈", "민수", "현우", "태양" },
            ["feminine"] = new[] { "지은", "수빈", "예린", "하은" }
        }
    };

    public NameGenerationService(
        AiService aiService,
        IConfiguration configuration,
        ILogger<NameGenerationService> logger,
        IConnectivityService connectivity)
    {
        _aiService = aiService;
        _configuration = configuration;
        _logger = logger;
        _connectivity = connectivity;
    }

    public async Task<string[]> GenerateNamesAsync(string targetLanguage)
    {
        var settings = _configuration.GetRequiredSection("Settings").Get<Settings>();
        var hasApiKey = !string.IsNullOrEmpty(settings?.OpenAIKey);

        if (hasApiKey && _connectivity.IsInternetAvailable)
        {
            try
            {
                return await GenerateNamesWithAiAsync(targetLanguage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI name generation failed");
                // Fall back to hardcoded names
            }
        }

        return GetHardcodedNames(targetLanguage);
    }

    private async Task<string[]> GenerateNamesWithAiAsync(string targetLanguage)
    {
        var prompt = $"Generate 8 popular names from {targetLanguage} culture in the native script/language - 4 masculine and 4 feminine names. " +
                    $"If the language is Korean, use Hangul characters. If Japanese, use appropriate Japanese characters. " +
                    $"Return only the names separated by commas, no additional text, formatting, or romanization. " +
                    $"For example for Korean: 지훈, 민수, 현우, 태양, 지은, 수빈, 예린, 하은";

        var response = await _aiService.SendPrompt<string>(prompt);

        if (!string.IsNullOrEmpty(response))
        {
            var names = response.Split(',')
                               .Select(name => name.Trim())
                               .Where(name => !string.IsNullOrEmpty(name))
                               .Take(8)
                               .ToArray();

            if (names.Length == 8)
                return names;
        }

        // Fallback if AI response is invalid
        return GetHardcodedNames(targetLanguage);
    }

    private string[] GetHardcodedNames(string targetLanguage)
    {
        if (_hardcodedNames.TryGetValue(targetLanguage, out var languageNames))
        {
            var masculineNames = languageNames["masculine"];
            var feminineNames = languageNames["feminine"];
            return masculineNames.Concat(feminineNames).ToArray();
        }

        // Default to English if language not found
        var defaultNames = _hardcodedNames["English"];
        return defaultNames["masculine"].Concat(defaultNames["feminine"]).ToArray();
    }
}
