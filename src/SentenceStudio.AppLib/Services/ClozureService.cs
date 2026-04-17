using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Scriban;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

public class ClozureService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ClozureService> _logger;
    private AiService _aiService;
    private SkillProfileRepository _skillRepository;
    private LearningResourceRepository _resourceRepository;
    private ISyncService _syncService;
    private readonly IFileSystemService _fileSystem;

    private List<VocabularyWord> _words;

    public List<VocabularyWord> Words
    {
        get
        {
            return _words;
        }
    }

    private readonly VocabularyProgressService _progressService;

    public ClozureService(IServiceProvider serviceProvider, ILogger<ClozureService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _aiService = serviceProvider.GetRequiredService<AiService>();
        _skillRepository = serviceProvider.GetRequiredService<SkillProfileRepository>();
        _resourceRepository = serviceProvider.GetRequiredService<LearningResourceRepository>();
        _syncService = serviceProvider.GetService<ISyncService>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystemService>();
        _progressService = serviceProvider.GetRequiredService<VocabularyProgressService>();
    }

    public async Task<List<Challenge>> GetSentences(string resourceID, int numberOfSentences, string skillID)
    {
        _logger.LogDebug("GetSentences called with resourceID={ResourceID}, numberOfSentences={NumberOfSentences}, skillID={SkillID}",
            resourceID, numberOfSentences, skillID);
        var watch = new Stopwatch();
        watch.Start();

        if (string.IsNullOrEmpty(resourceID))
        {
            _logger.LogDebug("Resource ID is 0 - no resource selected");
            return new List<Challenge>();
        }

        if (string.IsNullOrEmpty(skillID))
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

        // Send vocabulary words to AI, excluding Familiar words in grace period
        var allVocab = resource.Vocabulary.ToList();
        var wordIds = allVocab.Select(w => w.Id).ToList();
        var progressDict = await _progressService.GetProgressForWordsAsync(wordIds);
        _words = allVocab
            .Where(w => !progressDict.ContainsKey(w.Id) || !progressDict[w.Id].IsInGracePeriod)
            .ToList();
        _logger.LogDebug("Sending {WordCount} vocabulary words to AI ({Excluded} excluded for grace period)",
            _words.Count, allVocab.Count - _words.Count);

        var skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);
        _logger.LogDebug("Skill profile retrieved: {SkillTitle}", skillProfile?.Title ?? "null");

        // Get user's native language and use resource's language as target
        var userProfileRepo = _serviceProvider.GetRequiredService<UserProfileRepository>();
        var userProfile = await userProfileRepo.GetAsync();
        string nativeLanguage = userProfile?.NativeLanguage ?? "English";
        string targetLanguage = resource.Language ?? userProfile?.TargetLanguage ?? "Korean";

        var prompt = string.Empty;
        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GetClozuresV2.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
            prompt = await template.RenderAsync(new { 
                terms = _words, 
                number_of_sentences = numberOfSentences, 
                skills = skillProfile?.Description,
                native_language = nativeLanguage,
                target_language = targetLanguage
            });
        }

        _logger.LogDebug("Prompt created, length: {PromptLength}", prompt.Length);
        try
        {
            _logger.LogDebug("Sending prompt to AI service");
            var reply = await _aiService.SendPrompt<ClozureResponse>(prompt);

            if (reply?.Sentences != null)
            {
                _logger.LogDebug("AI returned {SentenceCount} sentences", reply.Sentences.Count);

                // 🏴‍☠️ IMPORTANT: Convert ClozureDto objects to Challenge objects and link vocabulary
                _logger.LogDebug("Converting {DtoCount} ClozureDto objects to Challenge objects", reply.Sentences.Count);
                var challenges = new List<Challenge>();

                foreach (var clozureDto in reply.Sentences)
                {
                    _logger.LogDebug("Processing clozure DTO");
                    _logger.LogDebug("DTO.VocabularyWord: '{VocabularyWord}'", clozureDto.VocabularyWord);
                    _logger.LogDebug("DTO.VocabularyWordAsUsed: '{VocabularyWordAsUsed}'", clozureDto.VocabularyWordAsUsed);
                    _logger.LogDebug("DTO.VocabularyWordGuesses: {GuessCount} items", clozureDto.VocabularyWordGuesses?.Count ?? 0);

                    // 🏴‍☠️ Normalize and self-heal the AI response so the UI never receives a broken cloze.
                    // Two frequent AI failure modes are handled here:
                    //   (1) vocabularyWordAsUsed isn't a verbatim substring of sentenceText (particles split, etc.)
                    //   (2) the correct answer is missing from the guesses list
                    var sentenceText = clozureDto.SentenceText ?? string.Empty;
                    var wordAsUsed = (clozureDto.VocabularyWordAsUsed ?? string.Empty).Trim();
                    var dictionaryForm = (clozureDto.VocabularyWord ?? string.Empty).Trim();

                    // Repair wordAsUsed if it's not a substring of the sentence.
                    if (!string.IsNullOrEmpty(sentenceText) && !string.IsNullOrEmpty(wordAsUsed)
                        && !sentenceText.Contains(wordAsUsed, StringComparison.Ordinal))
                    {
                        var repaired = SentenceStudio.Shared.Services.ClozureAnswerHelper.TryRepairWordAsUsed(sentenceText, wordAsUsed, dictionaryForm);
                        if (!string.IsNullOrEmpty(repaired) && !string.Equals(repaired, wordAsUsed, StringComparison.Ordinal))
                        {
                            _logger.LogWarning("Repaired VocabularyWordAsUsed '{Old}' -> '{New}' to match sentence '{Sentence}'",
                                wordAsUsed, repaired, sentenceText);
                            wordAsUsed = repaired;
                        }
                    }

                    // Ensure the guesses list always includes the correct answer as an option, and is exactly 5 unique items.
                    var repairedGuesses = SentenceStudio.Shared.Services.ClozureAnswerHelper.EnsureGuessesIncludeAnswer(
                        clozureDto.VocabularyWordGuesses, wordAsUsed, out var guessesWereFixed);
                    if (guessesWereFixed)
                    {
                        _logger.LogWarning("Repaired VocabularyWordGuesses: correct answer '{Answer}' was missing; final options=[{Options}]",
                            wordAsUsed, string.Join(", ", repairedGuesses));
                    }

                    // Convert the list of guesses to a comma-separated string for storage
                    var guessesString = repairedGuesses.Any()
                        ? string.Join(", ", repairedGuesses)
                        : string.Empty;

                    // Create Challenge object from DTO - keep it simple
                    var challenge = new Challenge
                    {
                        SentenceText = sentenceText,
                        RecommendedTranslation = clozureDto.RecommendedTranslation,
                        VocabularyWord = clozureDto.VocabularyWord,
                        VocabularyWordAsUsed = wordAsUsed,
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

    public async Task<string> SaveChallenges(Challenge item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Set timestamps
        if (item.CreatedAt == default)
            item.CreatedAt = DateTime.UtcNow;

        item.UpdatedAt = DateTime.UtcNow;

        try
        {
            if (!string.IsNullOrEmpty(item.Id))
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

    public async Task<Challenge> GetChallengeAsync(string id)
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

    public async Task<List<GradeResponse>> GetGradeResponsesForChallengeAsync(string challengeId)
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
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
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
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("Translate.scriban-txt");
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
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GradeSentence.scriban-txt");
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
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GradeMyDescription.scriban-txt");
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
