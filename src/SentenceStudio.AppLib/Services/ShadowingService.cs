using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// Service for handling the Shadowing activity, which involves generating sentences and audio for shadowing practice.
/// </summary>
public class ShadowingService
{
    private readonly AiService _aiService;
    private readonly ElevenLabsSpeechService _speechService;
    private readonly LearningResourceRepository _resourceRepository;
    private readonly SkillProfileRepository _skillRepository;
    private readonly UserProfileRepository _userProfileRepository;
    private readonly TranscriptSentenceExtractor _sentenceExtractor;
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<ShadowingService> _logger;
    private readonly VocabularyProgressService _progressService;
    private List<VocabularyWord> _words = new();

    /// <summary>
    /// Gets the vocabulary words currently loaded for shadowing practice.
    /// </summary>
    public List<VocabularyWord> Words => _words;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowingService"/> class.
    /// </summary>
    /// <param name="service">The service provider to resolve dependencies.</param>
    public ShadowingService(IServiceProvider service)
    {
        _aiService = service.GetRequiredService<AiService>();
        _speechService = service.GetRequiredService<ElevenLabsSpeechService>();
        _resourceRepository = service.GetRequiredService<LearningResourceRepository>();
        _skillRepository = service.GetRequiredService<SkillProfileRepository>();
        _userProfileRepository = service.GetRequiredService<UserProfileRepository>();
        _sentenceExtractor = service.GetRequiredService<TranscriptSentenceExtractor>();
        _fileSystem = service.GetRequiredService<IFileSystemService>();
        _logger = service.GetRequiredService<ILogger<ShadowingService>>();
        _progressService = service.GetRequiredService<VocabularyProgressService>();
    }

    /// <summary>
    /// Generates sentences for shadowing practice based on learning resource and skill level.
    /// </summary>
    /// <param name="resourceId">The Id of the learning resource to use.</param>
    /// <param name="numberOfSentences">The number of sentences to generate.</param>
    /// <param name="skillID">The Id of the skill profile to use.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of sentences for shadowing practice.</returns>
    public async Task<List<ShadowingSentence>> GenerateSentencesAsync(
        string resourceId, 
        int numberOfSentences = 10, 
        string skillID = null, 
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // Get the learning resource with vocabulary
        LearningResource resource = await _resourceRepository.GetResourceAsync(resourceId);

        // If no resource found or no vocabulary available, return empty list
        if (resource == null || resource.Vocabulary == null || !resource.Vocabulary.Any())
            return new List<ShadowingSentence>();

        var random = new Random();
        
        // Exclude Familiar words in grace period (consistent with Cloze/Translation services)
        var allVocab = resource.Vocabulary.ToList();
        var wordIds = allVocab.Select(w => w.Id).ToList();
        var progressDict = await _progressService.GetProgressForWordsAsync(wordIds);
        var eligibleWords = allVocab
            .Where(w => !progressDict.ContainsKey(w.Id) || !progressDict[w.Id].IsInGracePeriod)
            .ToList();
        _logger.LogDebug("Shadowing: {EligibleCount} eligible words ({Excluded} excluded for grace period)",
            eligibleWords.Count, allVocab.Count - eligibleWords.Count);

        // Take a random selection of vocabulary words
        _words = eligibleWords.OrderBy(t => random.Next()).Take(10).ToList();
        
        // Get the user's native language and use resource's language as target
        var userProfile = await _userProfileRepository.GetAsync();
        string nativeLanguage = userProfile?.NativeLanguage ?? "English";
        // Use resource's language as target (supports multi-language learning)
        string targetLanguage = resource.Language ?? userProfile?.TargetLanguage ?? "Korean";
        
        // Separate words that need AI generation from those that use term as-is
        var wordsNeedingAi = _words.Where(w => w.LexicalUnitType == LexicalUnitType.Word).ToList();
        var wordsAsIs = _words.Where(w => w.LexicalUnitType == LexicalUnitType.Phrase 
                                        || w.LexicalUnitType == LexicalUnitType.Sentence 
                                        || w.LexicalUnitType == LexicalUnitType.Unknown).ToList();
        
        _logger.LogDebug("Shadowing generation: {WordCount} Words (need AI), {AsIsCount} Phrase/Sentence/Unknown (as-is)",
            wordsNeedingAi.Count, wordsAsIs.Count);
        
        // Log Unknown terms for UI reclassification
        foreach (var unknownWord in wordsAsIs.Where(w => w.LexicalUnitType == LexicalUnitType.Unknown))
        {
            _logger.LogInformation(
                "ShadowingUnknownTerm: WordId={WordId} Term={Term} needs classification",
                unknownWord.Id, unknownWord.TargetLanguageTerm);
        }
        
        // Create as-is sentences for Phrase/Sentence/Unknown (no AI round-trip)
        var asIsSentences = wordsAsIs.Select(word =>
        {
            _logger.LogDebug("ShadowingAsIs: WordId={WordId} LexicalUnitType={Type} Term={Term}",
                word.Id, word.LexicalUnitType, word.TargetLanguageTerm);
            
            return new ShadowingSentence
            {
                TargetLanguageText = word.TargetLanguageTerm,
                NativeLanguageText = word.NativeLanguageTerm,
                PronunciationNotes = null
            };
        }).ToList();
        
        // Generate AI sentences only for Words
        List<ShadowingSentence> aiSentences = new();
        if (wordsNeedingAi.Any())
        {
            var prompt = string.Empty;     
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GetShadowingSentences.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { 
                    terms = wordsNeedingAi,
                    native_language = nativeLanguage,
                    target_language = targetLanguage
                });
            }
            
            try
            {
                var response = await _aiService.SendPrompt<ShadowingSentencesResponse>(prompt);
                aiSentences = response?.Sentences ?? new List<ShadowingSentence>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ An error occurred in GenerateSentencesAsync");
            }
        }
        
        // Combine both sets and return
        var allSentences = asIsSentences.Concat(aiSentences).ToList();
        _logger.LogDebug("Shadowing result: {AsIsCount} as-is + {AiCount} AI-generated = {TotalCount} total",
            asIsSentences.Count, aiSentences.Count, allSentences.Count);
        
        return allSentences;
    }

    /// <summary>
    /// Gets shadowing sentences from transcript or generates via AI.
    /// Randomizes selection for variety in daily practice.
    /// For transcript mode: No translation, pure shadowing.
    /// For vocabulary mode: AI-generated with pronunciation notes.
    /// </summary>
    /// <param name="resourceId">Learning resource ID</param>
    /// <param name="count">Number of sentences to return</param>
    /// <param name="skillId">Skill profile ID for AI generation</param>
    /// <returns>List of shadowing sentences</returns>
    public async Task<List<ShadowingSentence>> GetOrGenerateSentencesAsync(
        string resourceId,
        int count = 10,
        string skillId = null)
    {
        _logger.LogInformation("🔍 GetOrGenerateSentencesAsync called - ResourceId: {ResourceId}, Count: {Count}, SkillId: {SkillId}",
            resourceId, count, skillId);

        // Load resource
        var resource = await _resourceRepository.GetResourceAsync(resourceId);

        if (resource == null)
        {
            _logger.LogWarning("❌ Resource {ResourceId} not found", resourceId);
            return new List<ShadowingSentence>();
        }

        _logger.LogInformation("📚 Resource loaded: '{ResourceTitle}', Language: {Language}",
            resource.Title, resource.Language);

        var hasTranscript = !string.IsNullOrWhiteSpace(resource.Transcript);
        _logger.LogInformation("📝 Transcript exists: {HasTranscript}", hasTranscript);

        if (hasTranscript)
        {
            _logger.LogInformation("📝 Transcript length: {TranscriptLength} characters", resource.Transcript.Length);
        }

        // DECISION POINT: Does resource have a transcript?
        if (hasTranscript)
        {
            _logger.LogInformation("🎯 Using TRANSCRIPT MODE (no AI, no translation)");
            // TRANSCRIPT MODE: Extract sentences (no AI, no translation)
            return ExtractSentencesFromTranscript(resource, count);
        }
        else
        {
            _logger.LogInformation("🤖 Using GENERATION MODE (AI-generated sentences)");
            // GENERATION MODE: Use existing AI generation logic
            return await GenerateSentencesAsync(resourceId, count, skillId);
        }
    }

    /// <summary>
    /// Extracts shadowing sentences from resource transcript.
    /// Pure shadowing mode: No translation, just target language text.
    /// </summary>
    private List<ShadowingSentence> ExtractSentencesFromTranscript(
        LearningResource resource,
        int count)
    {
        _logger.LogInformation("📝 ExtractSentencesFromTranscript - Resource: '{ResourceTitle}', Language: {Language}, Count: {Count}",
            resource.Title, resource.Language, count);

        // Extract random sentences from transcript
        var targetSentences = _sentenceExtractor.ExtractRandomSentences(
            resource.Transcript,
            resource.Language,
            count);

        _logger.LogInformation("✅ Extracted {SentenceCount} sentences from transcript", targetSentences.Count);

        if (!targetSentences.Any())
        {
            _logger.LogWarning("⚠️ No sentences extracted - returning empty list");
            return new List<ShadowingSentence>();
        }

        // Log first few sentences for debugging
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            for (int i = 0; i < Math.Min(3, targetSentences.Count); i++)
            {
                var preview = targetSentences[i].Substring(0, Math.Min(50, targetSentences[i].Length));
                _logger.LogDebug("  Sentence {Index}: {Preview}...", i + 1, preview);
            }
        }

        // Convert to ShadowingSentence objects (no translation, no pronunciation notes)
        var result = targetSentences.Select(sentence => new ShadowingSentence
        {
            TargetLanguageText = sentence,
            NativeLanguageText = null,  // No translation for pure shadowing
            PronunciationNotes = null   // No pronunciation notes from transcript
        }).ToList();

        _logger.LogInformation("🎉 Returning {ResultCount} ShadowingSentence objects", result.Count);
        return result;
    }

    /// <summary>
    /// Generates audio for the given text using ElevenLabs API.
    /// </summary>
    /// <param name="text">The text to convert to audio.</param>
    /// <param name="voice">The voice Id or name to use (from ElevenLabsSpeechService.VoiceOptions).</param>
    /// <param name="speed">Speech speed multiplier (0.5 to 2.0).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An audio stream.</returns>
    public async Task<Stream> GenerateAudioAsync(
        string text, 
        string voice = "echo", 
        float speed = 1.0f, 
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        try
        {
            // Map from the legacy "echo" voice name if needed
            var mappedVoice = voice;
            
            // Set appropriate stability and similarity boost based on the voice and content type
            float stability = 0.5f;       // Default mid-level stability
            float similarityBoost = 0.75f; // Default high similarity to original voice
            
            // Use ElevenLabs for higher quality text-to-speech
            return await _speechService.TextToSpeechAsync(
                text: text,
                voiceId: mappedVoice,
                stability: stability,
                similarityBoost: similarityBoost,
                speed: speed,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Error in ElevenLabs speech generation, attempting fallback to OpenAI TTS");

            // Fallback to OpenAI TTS if ElevenLabs fails
            try
            {
                _logger.LogInformation("🔄 Falling back to OpenAI TTS");
                return await _aiService.TextToSpeechAsync(text, voice, speed);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "❌ Fallback TTS also failed");
                return null;
            }
        }
    }
}