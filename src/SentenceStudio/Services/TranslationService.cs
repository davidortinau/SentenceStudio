using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Scriban;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services
{
    public class TranslationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly AiService _aiService;
        private readonly LearningResourceRepository _resourceRepo;
        private readonly SkillProfileRepository _skillRepository;
        private readonly ISyncService _syncService;
        private readonly ILogger<TranslationService> _logger;
        private readonly string _openAiApiKey;

        private List<VocabularyWord> _words;

        public List<VocabularyWord> Words
        {
            get
            {
                return _words;
            }
        }

        public TranslationService(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<TranslationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
            _aiService = serviceProvider.GetRequiredService<AiService>();
            _resourceRepo = serviceProvider.GetRequiredService<LearningResourceRepository>();
            _skillRepository = serviceProvider.GetRequiredService<SkillProfileRepository>();
            _syncService = serviceProvider.GetService<ISyncService>();
        }

        public async Task<List<Challenge>> GetTranslationSentences(int resourceID, int numberOfSentences, int skillID)
        {
            _logger.LogDebug("GetTranslationSentences called with resourceID={ResourceID}, numberOfSentences={NumberOfSentences}, skillID={SkillID}", resourceID, numberOfSentences, skillID);
            var watch = new Stopwatch();
            watch.Start();

            if (resourceID == 0)
            {
                _logger.LogWarning("Resource ID is 0 - no resource selected");
                return new List<Challenge>();
            }

            if (skillID == 0)
            {
                _logger.LogWarning("Skill ID is 0 - no skill selected");
                return new List<Challenge>();
            }

            var resource = await _resourceRepo.GetResourceAsync(resourceID);
            _logger.LogDebug("Resource retrieved: {ResourceTitle}", resource?.Title ?? "null");

            if (resource is null || resource.Vocabulary is null || !resource.Vocabulary.Any())
            {
                _logger.LogWarning("No resource or vocabulary found - returning empty list");
                return new List<Challenge>();
            }

            // Send ALL vocabulary words to AI to ensure it only uses words from our vocabulary list
            _words = resource.Vocabulary.ToList();
            _logger.LogDebug("Sending ALL {VocabularyCount} vocabulary words to AI", _words.Count);

            var skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);
            _logger.LogDebug("Skill profile retrieved: {SkillProfileTitle}", skillProfile?.Title ?? "null");

            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetTranslations.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { terms = _words, number_of_sentences = numberOfSentences, skills = skillProfile?.Description });
            }

            _logger.LogTrace("Prompt created, length: {PromptLength}", prompt.Length);
            try
            {
                IChatClient client =
                new OpenAIClient(_openAiApiKey)
                    .GetChatClient(model: "gpt-4o-mini").AsIChatClient();

                _logger.LogDebug("Sending prompt to AI service");
                var reply = await client.GetResponseAsync<TranslationResponse>(prompt);

                if (reply != null && reply.Result.Sentences != null)
                {
                    _logger.LogDebug("AI returned {SentenceCount} sentences", reply.Result.Sentences.Count);

                    // Convert TranslationDto objects to Challenge objects and link vocabulary
                    _logger.LogTrace("Converting {SentenceCount} TranslationDto objects to Challenge objects", reply.Result.Sentences.Count);
                    var challenges = new List<Challenge>();

                    foreach (var translationDto in reply.Result.Sentences)
                    {
                        _logger.LogTrace("=== Processing translation DTO ===");
                        _logger.LogTrace("DTO.SentenceText: '{SentenceText}'", translationDto.SentenceText);
                        _logger.LogTrace("DTO.RecommendedTranslation: '{RecommendedTranslation}'", translationDto.RecommendedTranslation);
                        _logger.LogTrace("DTO.TranslationVocabulary: [{TranslationVocabulary}]", string.Join(", ", translationDto.TranslationVocabulary));

                        // Create Challenge object from DTO - simply map TranslationVocabulary to Challenge.Vocabulary
                        var challenge = new Challenge
                        {
                            SentenceText = translationDto.SentenceText,
                            RecommendedTranslation = translationDto.RecommendedTranslation,
                            Vocabulary = translationDto.TranslationVocabulary.Select(word => new VocabularyWord
                            {
                                TargetLanguageTerm = word,
                                NativeLanguageTerm = "", // We don't need this for the vocabulary building blocks
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            }).ToList(),
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };

                        challenges.Add(challenge);
                        _logger.LogTrace($"🏴‍☠️ TranslationService: ✅ Added challenge with {translationDto.TranslationVocabulary.Count} vocabulary building blocks");
                    }

                    watch.Stop();
                    _logger.LogTrace($"🏴‍☠️ TranslationService: Generated {challenges.Count} translation challenges in {watch.Elapsed}");
                    return challenges;
                }
                else
                {
                    _logger.LogTrace("🏴‍☠️ TranslationService: Reply or Sentences is null");
                    return new List<Challenge>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"🏴‍☠️ TranslationService: Error in GetTranslationSentences - {ex.Message}");
                return new List<Challenge>();
            }
        }
        public async Task<string> TranslateAsync(string text, string context = null)
        {
            // Attempt to find an existing translation in the local database
            var existingWord = await _resourceRepo.GetWordByTargetTermAsync(text);
            if (existingWord != null && !string.IsNullOrEmpty(existingWord.NativeLanguageTerm))
            {
                return existingWord.NativeLanguageTerm;
            }

            existingWord = await _resourceRepo.GetWordByNativeTermAsync(text);
            if (existingWord != null && !string.IsNullOrEmpty(existingWord.TargetLanguageTerm))
            {
                return existingWord.TargetLanguageTerm;
            }

            try
            {
                string prompt;
                using (Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Translate.scriban-txt"))
                using (StreamReader reader = new StreamReader(templateStream))
                {
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    prompt = await template.RenderAsync(new
                    {
                        user_input = text,
                        context = context ?? string.Empty
                    });
                }

                // Get translation using the AI service
                string translation = await _aiService.SendPrompt<string>(prompt);

                // Save the new translation to the local database for future use
                var vocabWord = new VocabularyWord
                {
                    NativeLanguageTerm = text,
                    TargetLanguageTerm = translation,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                await _resourceRepo.SaveWordAsync(vocabWord);

                return translation;
            }
            catch (Exception ex)
            {
                _logger.LogError($"An error occurred during translation: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
