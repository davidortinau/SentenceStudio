using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Scriban;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

public class ClozureService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ClozureService> _logger;
    private AiService _aiService;
    private SkillProfileRepository _skillRepository;
    private LearningResourceRepository _resourceRepository;
    private ISyncService _syncService;

    private List<VocabularyWord> _words;

    public List<VocabularyWord> Words
    {
        get
        {
            return _words;
        }
    }

    private readonly string _openAiApiKey;

    public ClozureService(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<ClozureService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
        _aiService = serviceProvider.GetRequiredService<AiService>();
        _skillRepository = serviceProvider.GetRequiredService<SkillProfileRepository>();
        _resourceRepository = serviceProvider.GetRequiredService<LearningResourceRepository>();
        _syncService = serviceProvider.GetService<ISyncService>();
    }

    public async Task<List<Challenge>> GetSentences(int resourceID, int numberOfSentences, int skillID)
    {
        _logger.LogDebug("GetSentences called with resourceID={ResourceID}, numberOfSentences={NumberOfSentences}, skillID={SkillID}",
            resourceID, numberOfSentences, skillID);
        var watch = new Stopwatch();
        watch.Start();

        if (resourceID == 0)
        {
            _logger.LogDebug("Resource ID is 0 - no resource selected");
            return new List<Challenge>();
        }

        if (skillID == 0)
        {
            _logger.LogDebug("Skill ID is 0 - no skill selected");
            return new List<Challenge>();
        }

        var resource = await _resourceRepository.GetResourceAsync(resourceID);
        _logger.LogDebug("Resource retrieved: {ResourceTitle}", resource?.Title ?? "null");

        if (resource is null || resource.Vocabulary is null || !resource.Vocabulary.Any())
        {
            _logger.LogDebug("No resource or vocabulary found - returning empty list");
            return new List<Challenge>(); // Return empty list instead of null
        }

        // 🏴‍☠️ CRITICAL FIX: Send ALL vocabulary words to AI, not just a subset
        // This ensures the AI can only use words from our vocabulary list
        _words = resource.Vocabulary.ToList();
        _logger.LogDebug("Sending ALL {WordCount} vocabulary words to AI (not just {SentenceCount})",
            _words.Count, numberOfSentences);

        var skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);
        _logger.LogDebug("Skill profile retrieved: {SkillTitle}", skillProfile?.Title ?? "null");

        var prompt = string.Empty;
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetClozuresV2.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
            prompt = await template.RenderAsync(new { terms = _words, number_of_sentences = numberOfSentences, skills = skillProfile?.Description });
        }

        _logger.LogDebug("Prompt created, length: {PromptLength}", prompt.Length);
        try
        {
            IChatClient client =
            new OpenAIClient(_openAiApiKey)
                .GetChatClient(model: "gpt-4o-mini").AsIChatClient();

            _logger.LogDebug("Sending prompt to AI service");

            // Use GetResponseAsync which handles structured outputs properly
            var reply = await client.GetResponseAsync<ClozureResponse>(prompt);

            if (reply != null && reply?.Result?.Sentences != null)
            {
                _logger.LogDebug("AI returned {SentenceCount} sentences", reply.Result.Sentences.Count);

                // 🏴‍☠️ IMPORTANT: Convert ClozureDto objects to Challenge objects and link vocabulary
                _logger.LogDebug("Converting {DtoCount} ClozureDto objects to Challenge objects", reply.Result.Sentences.Count);
                var challenges = new List<Challenge>();

                foreach (var clozureDto in reply.Result.Sentences)
                {
                    _logger.LogDebug("Processing clozure DTO");
                    _logger.LogDebug("DTO.VocabularyWord: '{VocabularyWord}'", clozureDto.VocabularyWord);
                    _logger.LogDebug("DTO.VocabularyWordAsUsed: '{VocabularyWordAsUsed}'", clozureDto.VocabularyWordAsUsed);
                    _logger.LogDebug("DTO.VocabularyWordGuesses: {GuessCount} items", clozureDto.VocabularyWordGuesses?.Count ?? 0);

                    // Convert the list of guesses to a comma-separated string for storage
                    var guessesString = clozureDto.VocabularyWordGuesses != null && clozureDto.VocabularyWordGuesses.Any()
                        ? string.Join(", ", clozureDto.VocabularyWordGuesses)
                        : string.Empty;

                    // Create Challenge object from DTO - keep it simple
                    var challenge = new Challenge
                    {
                        SentenceText = clozureDto.SentenceText,
                        RecommendedTranslation = clozureDto.RecommendedTranslation,
                        VocabularyWord = clozureDto.VocabularyWord,
                        VocabularyWordAsUsed = clozureDto.VocabularyWordAsUsed,
                        VocabularyWordGuesses = guessesString,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };                    // Try to find matching vocabulary word for progress tracking
                    var matchingWord = _words.FirstOrDefault(w =>
                        string.Equals(w.TargetLanguageTerm, clozureDto.VocabularyWord, StringComparison.OrdinalIgnoreCase));

                    if (matchingWord != null)
                    {
                        challenge.Vocabulary = new List<VocabularyWord> { matchingWord };
                        _logger.LogDebug("Linked to vocabulary word ID {WordId} ('{TargetTerm}')",
                            matchingWord.Id, matchingWord.TargetLanguageTerm);
                    }
                    else
                    {
                        _logger.LogWarning("Could not find vocabulary word '{VocabularyWord}' in lesson vocabulary",
                            clozureDto.VocabularyWord);
                        challenge.Vocabulary = new List<VocabularyWord>();
                    }

                    challenges.Add(challenge);
                    _logger.LogDebug("Added clozure challenge");
                }

                return challenges;
            }
            else
            {
                _logger.LogWarning("Reply or Sentences is null");
                return new List<Challenge>();
            }
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            _logger.LogError(ex, "Error occurred in GetChallenges");
            return new List<Challenge>();
        }
    }

    public async Task<int> SaveChallenges(Challenge item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Set timestamps
        if (item.CreatedAt == default)
            item.CreatedAt = DateTime.UtcNow;

        item.UpdatedAt = DateTime.UtcNow;

        try
        {
            if (item.Id != 0)
            {
                db.Challenges.Update(item);
            }
            else
            {
                db.Challenges.Add(item);
            }

            await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveChallenges");
        }
        return item.Id;
    }

    public async Task<int> SaveGrade(GradeResponse item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Set timestamps
        if (item.CreatedAt == default)
            item.CreatedAt = DateTime.UtcNow;

        try
        {
            if (item.Id != 0)
            {
                db.GradeResponses.Update(item);
            }
            else
            {
                db.GradeResponses.Add(item);
            }

            await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveGrade");
        }

        return item.Id;
    }

    public async Task<List<Challenge>> GetChallengesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Challenges.ToListAsync();
    }

    public async Task<Challenge> GetChallengeAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Challenges.Where(c => c.Id == id).FirstOrDefaultAsync();
    }

    public async Task<List<GradeResponse>> GetGradeResponsesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GradeResponses.ToListAsync();
    }

    public async Task<GradeResponse> GetGradeResponseAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GradeResponses.Where(gr => gr.Id == id).FirstOrDefaultAsync();
    }

    public async Task<List<GradeResponse>> GetGradeResponsesForChallengeAsync(int challengeId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GradeResponses.Where(gr => gr.ChallengeID == challengeId).ToListAsync();
    }

    public async Task<GradeResponse> GradeTranslation(string userInput, string originalSentence, string recommendedTranslation)
    {
        try
        {
            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { original_sentence = originalSentence, recommended_translation = recommendedTranslation, user_input = userInput });

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<GradeResponse>(prompt);
            return response;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            _logger.LogError(ex, "Error occurred in GradeTranslation");
            return new GradeResponse();
        }
    }

    public async Task<string> Translate(string userInput)
    {
        try
        {
            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Translate.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { user_input = userInput });

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<string>(prompt);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in Translate");
            return string.Empty;
        }
    }

    public async Task<GradeResponse> GradeSentence(string userInput, string userMeaning)
    {
        try
        {
            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeSentence.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { user_input = userInput, user_meaning = userMeaning });

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<GradeResponse>(prompt);
            return response;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            _logger.LogError(ex, "Error occurred in GradeSentence");
            return new GradeResponse();
        }
    }

    public async Task<GradeResponse> GradeDescription(string myDescription, string aiDescription)
    {
        try
        {
            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeMyDescription.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { my_description = myDescription, ai_description = aiDescription });

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<GradeResponse>(prompt);
            return response;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            _logger.LogError(ex, "Error occurred in GradeDescription");
            return new GradeResponse();
        }
    }
}