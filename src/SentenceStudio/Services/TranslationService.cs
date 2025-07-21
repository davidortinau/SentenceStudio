using Microsoft.Extensions.AI;
using OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Scriban;

namespace SentenceStudio.Services
{
    public class TranslationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly AiService _aiService;
        private readonly LearningResourceRepository _resourceRepo;
        private readonly SkillProfileRepository _skillRepository;
        private readonly ISyncService _syncService;
        private readonly string _openAiApiKey;
        
        private List<VocabularyWord> _words;

        public List<VocabularyWord> Words {
            get {
                return _words;
            }
        }

        public TranslationService(IServiceProvider serviceProvider, IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
            _aiService = serviceProvider.GetRequiredService<AiService>();
            _resourceRepo = serviceProvider.GetRequiredService<LearningResourceRepository>();
            _skillRepository = serviceProvider.GetRequiredService<SkillProfileRepository>();
            _syncService = serviceProvider.GetService<ISyncService>();
        }

        public async Task<List<Challenge>> GetTranslationSentences(int resourceID, int numberOfSentences, int skillID)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationService: GetTranslationSentences called with resourceID={resourceID}, numberOfSentences={numberOfSentences}, skillID={skillID}");
            var watch = new Stopwatch();
            watch.Start();

            if (resourceID == 0)
            {
                Debug.WriteLine("🏴‍☠️ TranslationService: Resource ID is 0 - no resource selected");
                return new List<Challenge>();
            }

            if (skillID == 0)
            {
                Debug.WriteLine("🏴‍☠️ TranslationService: Skill ID is 0 - no skill selected");
                return new List<Challenge>();
            }

            var resource = await _resourceRepo.GetResourceAsync(resourceID);
            Debug.WriteLine($"🏴‍☠️ TranslationService: Resource retrieved: {resource?.Title ?? "null"}");

            if (resource is null || resource.Vocabulary is null || !resource.Vocabulary.Any())
            {
                Debug.WriteLine($"🏴‍☠️ TranslationService: No resource or vocabulary found - returning empty list");
                return new List<Challenge>();
            }

            // Send ALL vocabulary words to AI to ensure it only uses words from our vocabulary list
            _words = resource.Vocabulary.ToList();
            Debug.WriteLine($"🏴‍☠️ TranslationService: Sending ALL {_words.Count} vocabulary words to AI");

            var skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);
            Debug.WriteLine($"🏴‍☠️ TranslationService: Skill profile retrieved: {skillProfile?.Title ?? "null"}");
            
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetTranslations.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { terms = _words, number_of_sentences = numberOfSentences, skills = skillProfile?.Description}); 
            }

            Debug.WriteLine($"🏴‍☠️ TranslationService: Prompt created, length: {prompt.Length}");
            try
            {
                IChatClient client =
                new OpenAIClient(_openAiApiKey)
                    .AsChatClient(modelId: "gpt-4o-mini");

                Debug.WriteLine("🏴‍☠️ TranslationService: Sending prompt to AI service");
                var reply = await client.GetResponseAsync<TranslationResponse>(prompt);
                
                if (reply != null && reply.Result.Sentences != null)
                {
                    Debug.WriteLine($"🏴‍☠️ TranslationService: AI returned {reply.Result.Sentences.Count} sentences");
                    
                    // Convert TranslationDto objects to Challenge objects and link vocabulary
                    Debug.WriteLine($"🏴‍☠️ TranslationService: Converting {reply.Result.Sentences.Count} TranslationDto objects to Challenge objects");
                    var challenges = new List<Challenge>();
                    
                    foreach (var translationDto in reply.Result.Sentences)
                    {
                        Debug.WriteLine($"🏴‍☠️ TranslationService: === Processing translation DTO ===");
                        Debug.WriteLine($"🏴‍☠️ TranslationService: DTO.SentenceText: '{translationDto.SentenceText}'");
                        Debug.WriteLine($"🏴‍☠️ TranslationService: DTO.RecommendedTranslation: '{translationDto.RecommendedTranslation}'");
                        Debug.WriteLine($"🏴‍☠️ TranslationService: DTO.TranslationVocabulary: [{string.Join(", ", translationDto.TranslationVocabulary)}]");
                        
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
                        Debug.WriteLine($"🏴‍☠️ TranslationService: ✅ Added challenge with {translationDto.TranslationVocabulary.Count} vocabulary building blocks");
                    }
                    
                    watch.Stop();
                    Debug.WriteLine($"🏴‍☠️ TranslationService: Generated {challenges.Count} translation challenges in {watch.Elapsed}");
                    return challenges;
                }
                else
                {
                    Debug.WriteLine("🏴‍☠️ TranslationService: Reply or Sentences is null");
                    return new List<Challenge>();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"🏴‍☠️ TranslationService: Error in GetTranslationSentences - {ex.Message}");
                return new List<Challenge>();
            }
        }
        public async Task<string> TranslateAsync(string text)
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
                    prompt = await template.RenderAsync(new { user_input = text });
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
                Debug.WriteLine($"An error occurred during translation: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
