using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Scriban;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Services
{
    public class TranslationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly AiService _aiService;
        private readonly LearningResourceRepository _resourceRepo;
        private readonly SkillProfileRepository _skillRepository;
        private readonly ISyncService _syncService;
        private readonly IFileSystemService _fileSystem;
        private readonly ILogger<TranslationService> _logger;
        private readonly IPreferencesService? _preferences;

        private List<VocabularyWord> _words;

        public List<VocabularyWord> Words
        {
            get
            {
                return _words;
            }
        }

        private readonly VocabularyProgressService _progressService;
        private string ActiveUserId => _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;

        public TranslationService(IServiceProvider serviceProvider, ILogger<TranslationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _aiService = serviceProvider.GetRequiredService<AiService>();
            _resourceRepo = serviceProvider.GetRequiredService<LearningResourceRepository>();
            _skillRepository = serviceProvider.GetRequiredService<SkillProfileRepository>();
            _syncService = serviceProvider.GetService<ISyncService>();
            _fileSystem = serviceProvider.GetRequiredService<IFileSystemService>();
            _preferences = serviceProvider.GetService<IPreferencesService>();
            _progressService = serviceProvider.GetRequiredService<VocabularyProgressService>();
        }

        public async Task<List<Challenge>> GetTranslationSentences(
            string resourceID,
            int numberOfSentences,
            string skillID,
            IReadOnlyList<string>? focusVocabularyIds = null)
        {
            var normalizedFocusIds = FocusVocabularySelection.NormalizeFocusVocabularyIds(focusVocabularyIds);
            _logger.LogDebug("GetTranslationSentences called with resourceID={ResourceID}, numberOfSentences={NumberOfSentences}, skillID={SkillID}, focusVocabularyCount={FocusVocabularyCount}",
                resourceID, numberOfSentences, skillID, normalizedFocusIds.Count);
            var watch = new Stopwatch();
            watch.Start();

            var userProfileRepo = _serviceProvider.GetRequiredService<UserProfileRepository>();
            var userProfile = await userProfileRepo.GetAsync();
            string nativeLanguage = userProfile?.NativeLanguage ?? "English";
            string targetLanguage = userProfile?.TargetLanguage ?? "Korean";

            LearningResource? resource = null;
            if (!string.IsNullOrEmpty(resourceID))
            {
                resource = await _resourceRepo.GetResourceAsync(resourceID);
                _logger.LogDebug("Resource retrieved: {ResourceTitle}", resource?.Title ?? "null");
                targetLanguage = resource?.Language ?? targetLanguage;
            }
            else if (normalizedFocusIds.Count == 0)
            {
                _logger.LogWarning("Resource ID is empty and no FocusVocabularyIds were provided - no translation source selected");
                return new List<Challenge>();
            }

            if (string.IsNullOrEmpty(skillID) && normalizedFocusIds.Count == 0)
            {
                _logger.LogWarning("Skill ID is empty - no skill selected");
                return new List<Challenge>();
            }

            var resourceVocabulary = resource?.Vocabulary?.ToList() ?? new List<VocabularyWord>();
            if (resourceVocabulary.Count == 0 && normalizedFocusIds.Count == 0)
            {
                _logger.LogWarning("No resource or vocabulary found - returning empty list");
                return new List<Challenge>();
            }

            var allUserVocabulary = normalizedFocusIds.Count > 0
                ? await _resourceRepo.GetAllVocabularyWordsWithResourcesAsync()
                : new List<VocabularyWord>();
            var focusWords = FocusVocabularySelection.SelectFocusWords(allUserVocabulary, normalizedFocusIds);
            if (normalizedFocusIds.Count > 0 && focusWords.Count == 0)
            {
                _logger.LogWarning("FocusVocabularyIds were provided for Translation but no matching vocabulary was found; falling back to resource vocabulary");
            }

            var contextVocabulary = resourceVocabulary.Any() ? resourceVocabulary : allUserVocabulary;
            var wordIds = contextVocabulary.Select(w => w.Id).ToList();
            var progressDict = await _progressService.GetProgressForWordsAsync(wordIds, ActiveUserId);
            var eligibleWords = contextVocabulary
                .Where(FocusVocabularySelection.IsUsableVocabularyWord)
                .Where(w => !progressDict.ContainsKey(w.Id) || !progressDict[w.Id].IsInGracePeriod)
                .ToList();

            const int MaxVocabSample = 40;
            if (focusWords.Any())
            {
                var sampledContext = eligibleWords.Count > MaxVocabSample
                    ? eligibleWords.OrderBy(_ => Random.Shared.Next()).Take(MaxVocabSample).ToList()
                    : eligibleWords;
                _words = FocusVocabularySelection.BuildRequiredFirstPromptVocabulary(focusWords, sampledContext, MaxVocabSample);
                _logger.LogDebug("Sending {FocusCount} required focus words and {TotalCount} total vocabulary words to translation AI",
                    focusWords.Count, _words.Count);
            }
            else if (eligibleWords.Count > MaxVocabSample)
            {
                _words = eligibleWords
                    .OrderBy(_ => Random.Shared.Next())
                    .Take(MaxVocabSample)
                    .ToList();
                _logger.LogDebug("Sampled {SampleCount} of {EligibleCount} eligible vocabulary words for AI prompt ({Excluded} excluded for grace period)",
                    _words.Count, eligibleWords.Count, contextVocabulary.Count - eligibleWords.Count);
            }
            else
            {
                _words = eligibleWords;
                _logger.LogDebug("Sending {VocabularyCount} vocabulary words to AI ({Excluded} excluded for grace period)",
                    _words.Count, contextVocabulary.Count - _words.Count);
            }

            if (!_words.Any())
            {
                _logger.LogWarning("No eligible translation vocabulary found - returning empty list");
                return new List<Challenge>();
            }

            SkillProfile? skillProfile = null;
            if (!string.IsNullOrEmpty(skillID))
            {
                skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);
                _logger.LogDebug("Skill profile retrieved: {SkillProfileTitle}", skillProfile?.Title ?? "null");
            }

            var requiredTerms = focusWords.Where(FocusVocabularySelection.IsUsableVocabularyWord).ToList();
            var requiredIdSet = requiredTerms.Select(word => word.Id).ToHashSet(StringComparer.Ordinal);
            var contextTerms = requiredTerms.Any()
                ? _words.Where(word => !requiredIdSet.Contains(word.Id)).ToList()
                : _words;
            var prompt = string.Empty;
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GetTranslations.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new {
                    terms = _words,
                    required_terms = requiredTerms,
                    context_terms = contextTerms,
                    has_required_terms = requiredTerms.Any(),
                    number_of_sentences = numberOfSentences,
                    skills = skillProfile?.Description,
                    native_language = nativeLanguage,
                    target_language = targetLanguage
                });
            }

            _logger.LogTrace("Prompt created, length: {PromptLength}", prompt.Length);
            try
            {
                _logger.LogDebug("Sending prompt to AI service");
                var reply = await _aiService.SendPrompt<TranslationResponse>(prompt);

                if (reply?.Sentences != null)
                {
                    _logger.LogDebug("AI returned {SentenceCount} sentences", reply.Sentences.Count);

                    // Convert TranslationDto objects to Challenge objects and link vocabulary
                    _logger.LogTrace("Converting {SentenceCount} TranslationDto objects to Challenge objects", reply.Sentences.Count);
                    var vocabularyByTargetTerm = _words
                        .Where(FocusVocabularySelection.IsUsableVocabularyWord)
                        .GroupBy(word => word.TargetLanguageTerm!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                    var challenges = new List<Challenge>();

                    foreach (var translationDto in reply.Sentences)
                    {
                        _logger.LogTrace("=== Processing translation DTO ===");
                        _logger.LogTrace("DTO.SentenceText: '{SentenceText}'", translationDto.SentenceText);
                        _logger.LogTrace("DTO.RecommendedTranslation: '{RecommendedTranslation}'", translationDto.RecommendedTranslation);
                        _logger.LogTrace("DTO.TranslationVocabulary: [{TranslationVocabulary}]", string.Join(", ", translationDto.TranslationVocabulary));

                        var challenge = new Challenge
                        {
                            SentenceText = translationDto.SentenceText,
                            RecommendedTranslation = translationDto.RecommendedTranslation,
                            Vocabulary = translationDto.TranslationVocabulary.Select(word =>
                                vocabularyByTargetTerm.TryGetValue(word, out var existingWord)
                                    ? existingWord
                                    : new VocabularyWord
                                    {
                                        TargetLanguageTerm = word,
                                        NativeLanguageTerm = "",
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    }).ToList(),
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };

                        challenges.Add(challenge);
                        _logger.LogTrace("TranslationService added challenge with {VocabularyCount} vocabulary building blocks", translationDto.TranslationVocabulary.Count);
                    }

                    watch.Stop();
                    _logger.LogTrace("TranslationService generated {ChallengeCount} translation challenges in {Elapsed}", challenges.Count, watch.Elapsed);
                    return challenges;
                }
                else
                {
                    _logger.LogTrace("TranslationService reply or Sentences is null");
                    return new List<Challenge>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TranslationService error in GetTranslationSentences");
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
                using (Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("Translate.scriban-txt"))
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
